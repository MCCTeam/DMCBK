// A host that connects, displays chat, reads player state and sends one message.
// The application supplies console output. Core has no console UI dependency.

using DMCBK.Core;
using Microsoft.Extensions.Logging;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Text;

namespace DMCBK.Samples.HeadlessClient;

/// <summary>Runs one bounded client session and reports its outcome.</summary>
internal static class Program
{
    // Exit codes mirror the CLI host so a supervising script can treat both hosts identically.
    private const int ExitClean = 0;             // connected, ran the scripted action, disconnected cleanly
    private const int ExitUsage = 1;             // bad arguments
    private const int ExitVersionResolution = 2; // could not determine / resolve the server version
    private const int ExitConnectionLost = 3;    // could not connect, or the connection dropped unexpectedly
    private const int ExitLoginRejected = 4;     // the server rejected the login
    private const int ExitActionFailed = 5;      // connected, but the post-join scripted action failed

    // Defaults so the sample runs with no arguments at all against a local offline server.
    private const string DefaultUsername = "HostSample";
    private const string DefaultHost = "localhost";
    private const ushort DefaultPort = 25565;

    /// <summary>
    /// How long the scripted action waits for the server to place the player before reporting that it could not.
    /// Generous: a slow or busy server can take several seconds between login and the first teleport.
    /// </summary>
    private static readonly TimeSpan SpawnWait = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        #region 1. Parse the tiny argument surface (all optional; constants above are the defaults)
        // Usage: DMCBK.Samples.HeadlessClient [username] [host[:port]] [version]
        //   - username : offline account name (<= 16 chars).
        //   - host:port: the server address; ":port" is optional and defaults to 25565.
        //   - version  : a Minecraft version name UMPK knows (e.g. "1.21.5"); omit or "auto" to auto-detect the server version by status ping.
        if (args.Length == 1 && (args[0] is "-h" or "--help"))
        {
            Console.WriteLine("Usage: DMCBK.Samples.HeadlessClient [username] [host[:port]] [version|auto]");
            return ExitUsage;
        }

        string username = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : DefaultUsername;
        (string host, ushort port) = ParseEndpoint(args.Length > 1 ? args[1] : null);
        string? versionName = args.Length > 2 && !string.Equals(args[2], "auto", StringComparison.OrdinalIgnoreCase)
            ? args[2]
            : null;

        if (username.Length > 16)
        {
            Console.Error.WriteLine("An offline username must be at most 16 characters.");
            return ExitUsage;
        }

        #endregion
        #region 2. A minimal logger factory
        // The core takes an ILoggerFactory (never Console directly).
        // Here we funnel the core's own logs to the console at Information+.
        // A real worker would plug in Serilog / Microsoft.Extensions.Logging.Console / OpenTelemetry here instead; the point is the core does not care.
        using ILoggerFactory loggerFactory = new ConsoleLoggerFactory(LogLevel.Information);
        ILogger log = loggerFactory.CreateLogger("HostSample");

        #endregion
        #region 3. Build the client FROM CODE
        // No configurations/ folder, no DmcbkConfiguration snapshot: a pure builder-driven construction, which is one supported host composition (the CLI drives the config-folder path; embedders can skip it).
        var builder = new ClientBuilder()
            .UseServer(host, port)
            .UseUsername(username)                 // offline account sugar; use UseAccount(...) for online flows
            .UseLoggerFactory(loggerFactory);

        // Pin the version if the caller named one; otherwise leave it unset so the core auto-detects by ping.
        if (versionName is not null)
        {
            if (!JavaVersions.TryGetByName(versionName, out var version))
            {
                Console.Error.WriteLine($"Unknown Minecraft version '{versionName}'. Use a name UMPK knows, or 'auto'.");
                return ExitUsage;
            }

            builder.UseVersion(version);
        }

        await using Client client = builder.Build();

        #endregion
        #region 4. Observe the session
        // Inbound chat: the core surfaces UMPK's structured Component (never a pre-flattened string), so the host owns rendering.
        // Here we flatten to plain text through the core translation source; a richer host would emit ANSI or feed a UI.
        // ToPlainText resolves translate-keys (chat.type.text, multiplayer.player.joined, ...) using the vanilla en_us tables UMPK ships.
        ITranslationSource translations = client.Translations;
        client.Game.Chat.MessageReceived += (_, message) =>
        {
            string line = message.Message.ToPlainText(translations);
            if (!string.IsNullOrWhiteSpace(line))
                Console.WriteLine($"[chat] {line}");
        };

        // Lifecycle transitions (Connecting -> Authenticating -> ... -> Playing, and the terminal Disconnected).
        var remoteDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, e) =>
        {
            log.LogInformation("Status: {Previous} -> {Current}", e.Previous, e.Current);
            if (e.Current == ClientStatus.Disconnected && e.Disconnect is { WasLocal: false })
                // The server (or the network) ended the session; unblock Main so it can report the drop.
                remoteDrop.TrySetResult();
        };

        // A wall-clock guard so a wedged connect or a silent server never hangs the sample forever.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        #endregion
        #region 5. Connect
        try
        {
            log.LogInformation("Connecting to {Host}:{Port} as {User} ({Version})...",
                host, port, username, versionName ?? "auto-detect");
            await client.StartAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (VersionResolutionException ex)
        {
            Console.Error.WriteLine($"Version resolution failed: {ex.Message}");
            return ExitVersionResolution;
        }
        catch (LoginRejectedException ex)
        {
            string reason = ex.Reason?.ToPlainText(translations) is { Length: > 0 } text ? text : ex.Message;
            Console.Error.WriteLine($"Login rejected: {reason}");
            return ExitLoginRejected;
        }
        catch (ConnectFailedException ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message}");
            return ExitConnectionLost;
        }
        catch (Umpk.Auth.AuthException ex)
        {
            Console.Error.WriteLine($"Authentication failed: {ex.Message}");
            return ExitLoginRejected;
        }
        catch (DmcbkAuthInteractionUnavailableException ex)
        {
            Console.Error.WriteLine($"Authentication failed: {ex.Message}");
            return ExitLoginRejected;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Timed out before the play session went live.");
            return ExitConnectionLost;
        }

        // StartAsync returned, so we are live.
        // This is the harness contract line the CLI also prints; keeping it here lets the same live-test scripts confirm a join from either host.
        Console.WriteLine("Server was successfully joined");
        log.LogInformation("Joined as {User}; negotiated version {Version}.",
            username, client.NegotiatedVersion?.Version.Name ?? "unknown");

        #endregion
        #region 6. ONE scripted action after join
        // Read the player vitals (proves world/self state is flowing) and send a single chat line (proves the serverbound path).
        // A real bot would drive MoveTo / dig / inventory here; a single read+say keeps the proof minimal and works on every version (no inventory traffic, so it is safe even on 1.8-1.12.2 where UMPK's legacy numeric item decode is incomplete).
        int result = ExitClean;
        try
        {
            // WAIT BEFORE READING.
            // StartAsync confirms connection readiness, which does not guarantee that the initial player placement was applied, so a read taken right here returns the tracker's DEFAULTS: origin and survival, on a server whose spawn is elsewhere and whose game mode is creative.
            // Defaults are indistinguishable from a reading, which is why this sample used to print numbers that looked real and were not.
            using var spawnWait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            spawnWait.CancelAfter(SpawnWait);
            bool spawned;
            try
            {
                spawned = await client.Game.Player.WaitForSpawnAsync(spawnWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                // The wait timed out on its own; the session is still live, so carry on and report honestly.
                spawned = false;
            }

            PlayerStatus status = await client.Game.Player.GetStatusAsync(lifetime.Token).ConfigureAwait(false);
            if (spawned && status.HasSpawned)
            {
                log.LogInformation(
                    "Player status: health={Health} food={Food} pos=({X:0.0},{Y:0.0},{Z:0.0}) mode={Mode} onGround={OnGround}",
                    status.Health, status.Food, status.Position.X, status.Position.Y, status.Position.Z,
                    status.GameMode, status.OnGround);
            }
            else
            {
                // Name what is missing instead of printing a placeholder position and game mode that read exactly like a measurement.
                // PlayerStatus.HasSpawned is what makes the difference visible.
                log.LogWarning(
                    "Player status unavailable: the server has not placed us within {Seconds}s, so position and game mode are unknown.",
                    SpawnWait.TotalSeconds);
            }

            await client.Game.Chat.SendAsync("DMCBK.Samples.HeadlessClient connected and running one scripted action.", lifetime.Token)
                .ConfigureAwait(false);
            log.LogInformation("Scripted action complete: read vitals and sent one chat line.");

            // Linger briefly so any server echo / join broadcast is received and logged before we disconnect.
            using var linger = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, linger.Token);
            await Task.WhenAny(remoteDrop.Task, Task.Delay(Timeout.Infinite, settle.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The 60s lifetime guard fired mid-action; treat as an unexpected drop.
            result = ExitConnectionLost;
        }
        catch (DmcbkClientException ex)
        {
            Console.Error.WriteLine($"Scripted action failed: {ex.Message}");
            result = ExitActionFailed;
        }

        if (remoteDrop.Task.IsCompleted && result == ExitClean)
        {
            // The server ended the session while we lingered; report it rather than claiming a clean exit.
            Console.Error.WriteLine($"Disconnected by the server: {Describe(client.LastDisconnect)}");
            result = ExitConnectionLost;
        }

        #endregion
        #region 7. Clean shutdown
        // StopAsync disconnects and stops any auto-reconnect; DisposeAsync (via the await using) is idempotent.
        log.LogInformation("Disconnecting...");
        await client.StopAsync().ConfigureAwait(false);
        log.LogInformation("Done (exit code {Code}).", result);
        return result;
        #endregion
    }

    /// <summary>Parses "host" or "host:port"; a missing or malformed port falls back to the default.</summary>
    private static (string Host, ushort Port) ParseEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (DefaultHost, DefaultPort);

        int colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return (value, DefaultPort);

        string host = value[..colon];
        return ushort.TryParse(value[(colon + 1)..], out ushort port)
            ? (host, port)
            : (host, DefaultPort);
    }

    /// <summary>
    /// A compact, host-friendly description of a disconnect for the log line.
    /// DMCBK.Core owns the wording, so an embedding host gets the real kick reason for free without re-implementing it (and this file stays within the embeddability law: it still references nothing but DMCBK.Core).
    /// </summary>
    private static string Describe(DisconnectInfo? info) => DisconnectDescription.Describe(info);
}

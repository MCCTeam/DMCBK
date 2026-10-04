using DMCBK.PluginSdk;
using DMCBK.Core;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Transport;
using DMCBK.Testing.Server;

namespace DMCBK.Testing;

/// <summary>
/// A whole client, plugin host and Minecraft server, in memory, for testing one plugin.
/// It writes plugin folders into a temp root, loads them through the real <see cref="PluginHost"/>, and drives a real handshake, login and configuration exchange against UMPK's <see cref="FakeJavaServer"/>, so what a plugin meets is a genuine session: a real per-plugin context and detach token, real events, a real scheduler and real plugin channels.
/// No socket is opened and no Minecraft server is needed.
/// <para>
/// Nothing here depends on a test framework.
/// A broken expectation throws, which every runner reports.
/// </para>
/// </summary>
/// <example>
/// <code>
/// await using var host = PluginTestHost.Create();
/// host.AddSourcePlugin("greeter", GreeterSource);
/// await host.LoadAsync();
/// await host.RunSessionAsync(async session =&gt;
/// {
///     await session.SendPluginMessageAsync(new Identifier("mcc", "demo"), [0x48, 0x69]);
///     await host.WaitForAsync(() =&gt; File.Exists(host.DataFile("greeter", "seen.log")));
/// });
/// </code>
/// </example>
public sealed class PluginTestHost : IAsyncDisposable
{
    private readonly FakeJavaServer _server;
    private readonly Dictionary<string, (string Folder, bool Enabled)> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsRoot;
    private readonly CancellationTokenSource _budget;

    private PluginTestHost(PluginTestHostOptions options)
    {
        Version = ResolveVersion(options.Version);
        Budget = options.Budget;
        _budget = new CancellationTokenSource(options.Budget);
        _server = FakeJavaServer.Create();

        _ownsRoot = options.PluginsRoot is null;
        PluginsRoot = options.PluginsRoot
            ?? Path.Combine(Path.GetTempPath(), "mcc-plugin-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(PluginsRoot);

        ILoggerFactory loggers = options.LoggerFactory ?? NullLoggerFactory.Instance;
        Client = new ClientBuilder()
            .UseUsername(options.Username)
            .UseServer("plugin-test-host", 25565)
            .UseVersion(Version)
            .UseProxy(new PipeConnectionFactory(_server.ClientPipe))
            .UseLoggerFactory(loggers)
            .ConfigureFeatures(f =>
            {
                f.Physics = false;
                f.Pathfinding = false;
            })
            .UseCommands()
            .UseBeacon()
            .Build();

        Plugins = new PluginHost(
            Client, PluginsRoot, loggers, Client.Translations, Client.Variables, null, options.Limits);
        Plugins.InstallationSource = _ =>
        {
            var packages = _packages.ToDictionary(pair => pair.Key, pair =>
            {
                if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(pair.Value.Folder, PluginManifest.FileName)), out PluginManifest manifest, out string? error))
                    throw new InvalidDataException(error);
                return new PluginInstallation(manifest, pair.Value.Folder, Path.Combine(PluginsRoot, "userdata", pair.Key), pair.Value.Enabled);
            }, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyList<PluginInstallation>>(PluginDependencyGraph.Build(packages.Values.Select(package => package.Manifest))
                .Order.Select(id => packages[id]).ToArray());
        };
    }

    /// <summary>The plugins root this host discovers from and the plugin folders are written into.</summary>
    public string PluginsRoot { get; }

    /// <summary>The client the plugins are loaded into. It is not connected until a session runs.</summary>
    public Client Client { get; }

    /// <summary>The real plugin host, for the states and reports a test wants to assert on.</summary>
    public PluginHost Plugins { get; }

    /// <summary>The version the session speaks.</summary>
    public JavaVersion Version { get; }

    /// <summary>How long anything in this harness is allowed to take.</summary>
    public TimeSpan Budget { get; }

    /// <summary>Cancelled once the budget is spent. Pass it to anything a test awaits.</summary>
    public CancellationToken Token => _budget.Token;

    /// <summary>Builds a harness. Dispose it to stop the client and delete the temp root.</summary>
    public static PluginTestHost Create(PluginTestHostOptions? options = null)
        => new(options ?? new PluginTestHostOptions());

    /// <summary>
    /// Writes a single-file plugin into the root and returns its folder.
    /// The manifest declares the current api version, so the plugin under test is gated exactly as a shipped one is.
    /// </summary>
    /// <param name="id">The plugin id, which is also the folder name.</param>
    /// <param name="source">The C# source of the entry file.</param>
    /// <param name="entryFileName">The entry file name. Null derives one from the id.</param>
    /// <param name="enabled">Whether the manifest says <c>enabled = true</c>.</param>
    /// <param name="extraManifestKeys">Raw TOML appended to the manifest, for tables like <c>[requires]</c>.</param>
    public string AddSourcePlugin(
        string id,
        string source,
        string? entryFileName = null,
        bool enabled = true,
        string extraManifestKeys = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);

        string entry = entryFileName ?? PluginScaffold.TypeName(id) + ".cs";
        string folder = Path.Combine(PluginsRoot, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, entry), source);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName),
            $"schema-version = 2\nid = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\n"
            + $"api-version = \"{PluginApiVersion.Current}\"\n"
            + "kind = \"source\"\ntarget = \"any\"\nframework = \"net10.0\"\n"
            + "dmcbk = \"*\"\numpk = \"*\"\n" + extraManifestKeys);
        _packages[id] = (folder, enabled);
        return folder;
    }

    /// <summary>
    /// Copies an existing plugin folder into the root, skipping build output and anything the last run left behind, and returns the copy.
    /// This is how a plugin that ships in a repository is tested as it ships.
    /// </summary>
    /// <param name="folder">The plugin folder to copy.</param>
    /// <param name="enable">
    /// True rewrites the copy's <c>enabled</c> to true, so a plugin that ships disabled still loads here.
    /// </param>
    public string AddPluginFolder(string folder, bool enable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string source = Path.GetFullPath(folder);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"No plugin folder at '{source}'.");

        string target = Path.Combine(PluginsRoot, Path.GetFileName(source));
        CopyTree(source, target);

        if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(target, PluginManifest.FileName)), out PluginManifest manifest, out string? error))
            throw new InvalidDataException(error);
        _packages[manifest.Id] = (target, enable);

        return target;
    }

    /// <summary>Loads every enabled plugin in the root, the way a real client start does.</summary>
    public Task<PluginActionResult> LoadAsync(CancellationToken ct = default)
        => Plugins.LoadAllAsync(Link(ct));

    /// <summary>The folder one plugin lives in.</summary>
    public string PluginFolder(string id) => Path.Combine(PluginsRoot, id);

    /// <summary>A path inside one plugin's <c>data/</c> sandbox, for reading back what it wrote.</summary>
    public string DataFile(string id, string relative) => Path.Combine(PluginsRoot, "userdata", id, "data", relative);

    /// <summary>
    /// Connects, drives the login and configuration exchange to the start of play, runs <paramref name="body"/> with the live session, then stops the client.
    /// Anything the body throws comes back out.
    /// </summary>
    public async Task RunSessionAsync(Func<PluginTestSession, Task> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        CancellationToken token = Link(ct);

        Task starting = Client.StartAsync(token);
        await DriveLoginAsync(token).ConfigureAwait(false);
        await starting.WaitAsync(Budget, token).ConfigureAwait(false);

        try
        {
            await body(new PluginTestSession(this, _server)).ConfigureAwait(false);
        }
        finally
        {
            await Client.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or the budget runs out.
    /// Returns whether it held.
    /// The work a plugin does off an inbound message lands on another thread, so a test that reads the result immediately reads it too early.
    /// </summary>
    public async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CancellationToken token = Link(ct);
        while (!condition())
        {
            if (token.IsCancellationRequested)
                return false;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return condition();
            }
        }

        return true;
    }

    /// <summary>Stops the client, closes the fake server, and deletes the temp root if it made one.</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        _budget.Dispose();

        if (!_ownsRoot)
            return;

        try
        {
            Directory.Delete(PluginsRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal CancellationToken Link(CancellationToken ct) => ct == default ? Token : ct;

    internal async Task DriveLoginAsync(CancellationToken ct)
    {
        await _server.NextFrameAsync(ct).ConfigureAwait(false);
        _server.ServerConnection.SetPhase(Umpk.Protocol.Java.ProtocolPhase.Login);
        await _server.NextFrameAsync(ct).ConfigureAwait(false);
        await PluginTestSession.SendAsync(
            this,
            _server,
            Umpk.Protocol.Java.ProtocolPhase.Login,
            new Umpk.Protocol.Java.Packets.ClientboundLoginFinishedPacket(Guid.NewGuid(), "Tester", [], null),
            ct).ConfigureAwait(false);
        await _server.NextFrameAsync(ct).ConfigureAwait(false);
        _server.ServerConnection.SetPhase(Umpk.Protocol.Java.ProtocolPhase.Configuration);
        await _server.NextFrameAsync(ct).ConfigureAwait(false);
        await PluginTestSession.SendAsync(
            this,
            _server,
            Umpk.Protocol.Java.ProtocolPhase.Configuration,
            new Umpk.Protocol.Java.Packets.ClientboundFinishConfigurationPacket(),
            ct).ConfigureAwait(false);
        await _server.NextFrameAsync(ct).ConfigureAwait(false);
        _server.ServerConnection.SetPhase(Umpk.Protocol.Java.ProtocolPhase.Play);

        // UMPK's connect-time readiness gate: the client reports no PLAY session until the first clientbound PLAY item crosses the wire.
        // A response-free time update, mirroring UMPK's own ScriptedServer.SendPlayReadinessFrameAsync; without it StartAsync never completes.
        await PluginTestSession.SendAsync(
            this,
            _server,
            Umpk.Protocol.Java.ProtocolPhase.Play,
            new Umpk.Protocol.Java.Packets.ClientboundSetTimePacket(
                GameTime: 0, DayTime: 0, TickDayTime: false, ClockUpdates: []),
            ct).ConfigureAwait(false);
    }

    private static JavaVersion ResolveVersion(string name)
        => JavaVersions.TryGetByName(name, out JavaVersion version)
            ? version
            : throw new ArgumentException($"'{name}' is not a Minecraft version UMPK knows.", nameof(name));

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(directory);

            // bin and obj are the plugin's compiled form, .cache is the last Roslyn compile, and data is whatever the last run wrote.
            // None of them is part of the plugin as it ships.
            if (name is "bin" or "obj" or "data" || name.StartsWith('.'))
                continue;

            CopyTree(directory, Path.Combine(target, name));
        }
    }

    private sealed class PipeConnectionFactory(System.IO.Pipelines.IDuplexPipe pipe) : IConnectionFactory
    {
        public ValueTask<System.IO.Pipelines.IDuplexPipe> ConnectAsync(Umpk.ServerEndpoint endpoint, CancellationToken ct)
            => ValueTask.FromResult(pipe);
    }
}

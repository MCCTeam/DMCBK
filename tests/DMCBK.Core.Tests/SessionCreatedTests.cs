using DMCBK.PluginSdk;
using Tomlet;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Pins <see cref="PluginContext.SessionCreated"/> (the pre-play capture hook): it fires with the still-unconnected client before that client ever dials, which is the only moment a subscription can see the handshake, login and configuration frames - <see cref="PluginContext.SessionStarted"/> and the per-session <see cref="ISessionScope.ObservePackets"/> it carries both fire only once play begins, by which point that traffic is gone.
/// <para>
/// Hermetic, same <c>FakeJavaServer</c> + <see cref="Fakes.McPluginSessionHarness"/> pattern as <see cref="SessionDescribeTests.ObservePackets_HandleDetaches_WhenThePluginUnloads"/>: a real plugin, loaded through the real <see cref="PluginHost"/>, so the assertion is on the actual wiring (<c>PluginHost</c> -&gt; <c>McPluginExtension</c> -&gt; UMPK's own <c>ClientExtensionContext.SessionCreated</c>) rather than on <see cref="PluginContext"/> in isolation.
/// </para>
/// </summary>
public sealed class SessionCreatedTests
{
    [Fact]
    public async Task SessionCreated_FiresBeforeTheClientDials_AndItsObservePacketsHandleSeesTheLoginFrames()
    {
        using var cts = new CancellationTokenSource(Fakes.McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using DMCBK.Testing.Server.FakeJavaServer server = DMCBK.Testing.Server.FakeJavaServer.Create();
        string root = Path.Combine(Path.GetTempPath(), "mcc-session-created-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Client client = Fakes.McPluginSessionHarness.BuildClient(
            server, "Tester", root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(
                client, root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));

            const string id = "session-created-observer";
            string folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Observer.cs"), ObserverPluginSource);
            File.WriteAllText(
                Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"Observer.cs\"\napi-version = \"1\"\nenabled = true\n"));
            await host.LoadAllAsync(ct);

            // The plugin subscribed to SessionCreated in ActivateAsync, which ran above, BEFORE StartAsync ever asks the client to dial.
            // If the wiring is right, its ObservePackets handle is therefore already live for the very first byte the handshake sends.
            Task starting = client.StartAsync(ct);
            await Fakes.McPluginSessionHarness.DriveLoginAsync(server, ct);
            await starting.WaitAsync(Fakes.McPluginSessionHarness.Budget, ct);

            string storagePath = Path.Combine(TestPackages.UserFolder(folder), "data", "storage.toml");
            Assert.True(
                await WaitForAsync(() => ReadValue(storagePath, "frames") is { Length: > 0 }, ct),
                "SessionCreated's ObservePackets handle never saw a frame.");

            // The client was genuinely unconnected at the moment the event fired: UMPK raises SessionCreated before UmpkClient.ConnectAsync ever runs, so Status is still Created, not Connecting.
            Assert.Equal("Created", ReadValue(storagePath, "statusAtSessionCreated"));

            string[] frames = (ReadValue(storagePath, "frames") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(frames);

            // The earliness claim: the very first frame the subscription observed is the client's own handshake ("intention") packet, sent before login even starts.
            // Catching frame #1 is only possible if the handle was live before the client dialled, not merely before play began.
            string[] first = frames[0].Split('|');
            Assert.Equal("Handshake", first[0]);
            Assert.Equal("Serverbound", first[1]);

            // The capture claim: frames from the login and configuration phases - which SessionStarted's per-session feed is already too late for - reached this SessionCreated-taken handle.
            Assert.Contains(frames, f => f.StartsWith("Login|", StringComparison.Ordinal));
            Assert.Contains(frames, f => f.StartsWith("Configuration|", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A plugin that subscribes at <c>SessionCreated</c> (not <c>SessionStarted</c>) and records, into storage so the test can read it back: the client's own <c>Status</c> at the moment the event fired, and up to 25 observed frames as <c>Phase|Flow|WireId</c>, semicolon-joined and re-saved on every frame so a test waiting on the file sees partial progress rather than an all-or-nothing write.
    /// </summary>
    private const string ObserverPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class SessionCreatedObserverPlugin : IPlugin
        {
            private const int MaxFrames = 25;

            private readonly object _gate = new();
            private readonly System.Collections.Generic.List<string> _frames = new();
            private PluginContext? _context;

            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "session-created-observer";

            public Task ActivateAsync(PluginContext context)
            {
                _context = context;
                context.SessionCreated += (_, e) =>
                {
                    context.Storage.Set("statusAtSessionCreated", e.Client.Status.ToString());
                    context.Storage.Save();
                    e.Client.ObservePackets(OnFrame);
                };
                return Task.CompletedTask;
            }

            private void OnFrame(in Umpk.Protocol.Java.PacketFrame frame)
            {
                lock (_gate)
                {
                    if (_frames.Count >= MaxFrames)
                    {
                        return;
                    }

                    _frames.Add($"{frame.Phase}|{frame.Flow}|{frame.WireId}");
                    _context!.Storage.Set("frames", string.Join(';', _frames));
                }

                _context!.Storage.Save();
            }
        }
        """;

    private static string? ReadValue(string storagePath, string key)
    {
        if (!File.Exists(storagePath))
            return null;

        using var reader = new StreamReader(new FileStream(
            storagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var store = TomletMain.To<Dictionary<string, string>>(reader.ReadToEnd());
        return store.TryGetValue(key, out string? value) ? value : null;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            if (ct.IsCancellationRequested)
                return false;

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }

        return true;
    }
}

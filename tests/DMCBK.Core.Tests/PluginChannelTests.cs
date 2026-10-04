using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Umpk;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using Umpk.Protocol.Java.Packets;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The plugin-channel surface a real plugin reaches: a login-phase claim made on <see cref="IPreSessionScope"/> before the dial, and the play-phase channels and announced set on <see cref="ISessionScope.Channels"/>.
/// Every assertion is on the bytes a scripted <see cref="FakeJavaServer"/> exchanged with a plugin the real <see cref="PluginHost"/> loaded, so the whole chain (host, extension, scope, UMPK) is what is under test.
/// </summary>
public sealed class PluginChannelTests
{
    private static readonly Identifier VelocityPlayerInfo = new("velocity", "player_info");
    private static readonly Identifier RegisterChannel = new("minecraft", "register");

    /// <summary>
    /// The forwarding case: a proxy asks during login, and the answer has to be on the wire before the login can finish.
    /// Nothing that runs at SessionStarted could ever answer it.
    /// </summary>
    [Fact]
    public async Task LoginQueryResponder_ClaimedAtSessionCreated_AnswersTheServer()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        string root = NewRoot("mcc-login-query");
        Client client = McPluginSessionHarness.BuildClient(server, "Tester", root, NullLoggerFactory.Instance);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(client, root, NullLoggerFactory.Instance, client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            string folder = WritePlugin(root, "login-answerer", "Answerer.cs", LoginAnswererSource);
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);

            await server.NextFrameAsync(ct); // handshake
            server.ServerConnection.SetPhase(ProtocolPhase.Login);
            await server.NextFrameAsync(ct); // hello

            await McPluginSessionHarness.SendAsync(
                server,
                ProtocolPhase.Login,
                new ClientboundLoginCustomQueryPacket(7, VelocityPlayerInfo, [0x01, 0x02]),
                ct);

            InboundFrame answer = await server.NextFrameAsync(ct);
            var reader = new PacketReader(answer.Payload);
            int transaction = reader.ReadVarInt();
            bool understood = reader.ReadBool();
            byte[] body = reader.ReadRemaining().ToArray();

            Assert.Equal(7, transaction);
            Assert.True(understood);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, body);

            await McPluginSessionHarness.SendAsync(
                server, ProtocolPhase.Login, new ClientboundLoginFinishedPacket(Guid.NewGuid(), "Tester", [], null), ct);
            await server.NextFrameAsync(ct); // login_acknowledged
            server.ServerConnection.SetPhase(ProtocolPhase.Configuration);
            await server.NextFrameAsync(ct); // client_information
            await McPluginSessionHarness.SendAsync(
                server, ProtocolPhase.Configuration, new ClientboundFinishConfigurationPacket(), ct);
            await server.NextFrameAsync(ct); // finish_configuration
            server.ServerConnection.SetPhase(ProtocolPhase.Play);
            // UMPK's connect-time readiness gate: no PLAY session until the first clientbound PLAY item.
            await McPluginSessionHarness.SendAsync(
                server, ProtocolPhase.Play,
                new ClientboundSetTimePacket(GameTime: 0, DayTime: 0, TickDayTime: false, ClockUpdates: []), ct);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);

            string storage = Path.Combine(TestPackages.UserFolder(folder), "data", "storage.toml");
            Assert.Equal("0102", ReadValue(storage, "queryPayload"));
        }
    }

    /// <summary>
    /// The other half of a channel conversation: what the server says it speaks.
    /// A bot that asks before speaking needs the announced set, which 1.x kept and this client did not.
    /// </summary>
    [Fact]
    public async Task ServerAnnounced_CarriesTheChannelsTheServerRegistered()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        string root = NewRoot("mcc-announced");
        Client client = McPluginSessionHarness.BuildClient(server, "Tester", root, NullLoggerFactory.Instance);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(client, root, NullLoggerFactory.Instance, client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            string folder = WritePlugin(root, "announce-watcher", "Watcher.cs", AnnounceWatcherSource);
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);
            await McPluginSessionHarness.DriveLoginAsync(server, ct);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);

            // What a server sends the moment play begins: its own channel list, NUL separated, on minecraft:register.
            await McPluginSessionHarness.SendPlayPluginMessageAsync(
                server, RegisterChannel, System.Text.Encoding.UTF8.GetBytes("bungeecord:main\0mcc:demo"), ct);

            string storage = Path.Combine(TestPackages.UserFolder(folder), "data", "storage.toml");
            Assert.True(
                await WaitForAsync(() => ReadValue(storage, "announced") is { Length: > 0 }, ct),
                "The announced-channel change never reached the plugin.");

            string[] announced = (ReadValue(storage, "announced") ?? string.Empty).Split(';');
            Assert.Contains("bungeecord:main", announced);
            Assert.Contains("mcc:demo", announced);
        }
    }

    /// <summary>
    /// A plugin that claims a login channel and records what it was asked, answering with two fixed bytes.
    /// The claim is made on the pre-session scope, which is the only surface early enough.
    /// </summary>
    private const string LoginAnswererSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class LoginAnswererPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "login-answerer";

            public Task ActivateAsync(PluginContext context)
            {
                context.SessionCreated += (_, e) => e.Session.RegisterLoginQuery(
                    new Umpk.Identifier("velocity", "player_info"),
                    (payload, ct) =>
                    {
                        context.Storage.Set("queryPayload", Convert.ToHexString(payload.Span));
                        context.Storage.Save();
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 0xAA, 0xBB });
                    });
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>A plugin that records the announced channel set as it changes.</summary>
    private const string AnnounceWatcherSource = """
        using System.Linq;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class AnnounceWatcherPlugin : IPlugin
        {
            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, e) =>
                {
                    e.Session.Channels.ServerAnnouncedChanged += (_, args) =>
                    {
                        context.Storage.Set("announced", string.Join(';', args.Announced.Select(c => c.ToString())));
                        context.Storage.Save();
                    };
                };
                return Task.CompletedTask;
            }

            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "announce-watcher";
        }
        """;

    private static string NewRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WritePlugin(string root, string id, string entry, string source)
    {
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, entry), source);
        File.WriteAllText(
            Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\napi-version = \"1.0\"\nenabled = true\n"));
        return folder;
    }

    private static string? ReadValue(string storagePath, string key)
    {
        if (!File.Exists(storagePath))
            return null;

        var store = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(storagePath));
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

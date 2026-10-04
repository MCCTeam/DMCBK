using System.Text;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Umpk;
using Umpk.Data.Java;
using Umpk.Nbt;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using Umpk.Protocol.Java.Transport;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Hermetic end to end for the configuration-phase dialog path: a real Client over a FakeJavaServer pipe on 1.21.6 (the first dialog protocol), driven by hand so a dialog can be injected DURING configuration, before FinishConfiguration.
/// <para>
/// A live server cannot hold this test: the only vanilla route into another player's configuration phase (<c>debugconfig</c>) makes this client's UMPK session reconnect instead of sitting in configuration, so the dialog never arrives that way.
/// Here every frame is scripted, which is exactly what makes the phase assertable: the answer must arrive BEFORE the test sends FinishConfiguration, proving it travelled the configuration wire (<c>ServerboundConfigCustomClickActionPacket</c>), and the plugin must have seen the dialog through <c>IPreSessionScope.SubscribeDialog</c>, the hook that only exists before the dial.
/// </para>
/// </summary>
public sealed class ConfigDialogSessionTests
{
    private const string InputKey = "cfgkey";
    private const string Secret = "hermetic-secret";
    private const string ActionId = "t5:cfganswer";

    /// <summary>
    /// A single-file plugin that watches for dialogs before the dial and answers the login one off the connect path, the way the manual tells authors to.
    /// </summary>
    private const string PluginSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ConfigDialogPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "config-dialog";

            public Task ActivateAsync(PluginContext context)
            {
                context.SessionCreated += (_, e) =>
                {
                    IPreSessionScope session = e.Session;
                    session.SubscribeDialog(shown => { _ = AnswerOnceAsync(context, session); });
                };
                return Task.CompletedTask;
            }

            private static async Task AnswerOnceAsync(PluginContext context, IPreSessionScope session)
            {
                try
                {
                    PreSessionDialogInfo? dialog = session.GetDialog();
                    if (dialog is null)
                    {
                        return;
                    }

                    context.Storage.Set("seen_title", dialog.Title);
                    context.Storage.Set("seen_keys", string.Join(",", dialog.InputKeys));
                    context.Storage.Set("seen_registry", dialog.RegistryId?.ToString() ?? "none");
                    if (dialog.HasInput("cfgkey"))
                    {
                        await session.AnswerDialogAsync(
                            0, new Dictionary<string, string> { ["cfgkey"] = "hermetic-secret" });
                        context.Storage.Set("answered", "1");
                    }

                    context.Storage.Save();
                }
                catch (Exception ex)
                {
                    context.Storage.Set("answer_error", ex.Message);
                    context.Storage.Save();
                }
            }
        }
        """;

    [Fact]
    public async Task ConfigPhaseDialog_IsSeenBeforeTheDial_AndAnsweredOnTheConfigWire()
    {
        Assert.True(JavaVersions.TryGetByName("1.21.6", out JavaVersion version));
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        string root = NewPluginsRoot();
        var loggers = new CapturingLoggerFactory();
        Client client = McPluginSessionHarness.BuildClient(
            server, "Tester", root, loggers, version: version);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(
                client, root, loggers, client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            WritePlugin(root, "config-dialog", "ConfigDialog.cs", PluginSource);
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);

            // Handshake + login, copied from the harness so the dialog can be injected mid-configuration instead of after play begins.
            await server.NextFrameAsync(ct); // handshake (intention)
            server.ServerConnection.SetPhase(ProtocolPhase.Login);
            await server.NextFrameAsync(ct); // hello
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Login,
                new ClientboundLoginFinishedPacket(Guid.NewGuid(), "Tester", [], null), ct, version);
            await server.NextFrameAsync(ct); // login_acknowledged
            server.ServerConnection.SetPhase(ProtocolPhase.Configuration);
            await server.NextFrameAsync(ct); // client_information

            // The dialog arrives while the connection is still configuring.
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Configuration,
                new ClientboundConfigShowDialogPacket(BuildDialog()), ct, version);

            // The plugin answers from its pre-session subscription.
            // This frame must arrive BEFORE FinishConfiguration is sent below: that ordering is the whole proof that the answer travelled the configuration wire, not the play one.
            InboundFrame answer;
            try
            {
                using var answerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                answerCts.CancelAfter(TimeSpan.FromSeconds(5));
                answer = await server.NextFrameAsync(answerCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Assert.Fail(
                    "No answer frame arrived within 5s of the config dialog.\n"
                    + "Recent client logs:\n"
                    + string.Join("\n", loggers.Lines.TakeLast(40)));
                throw;
            }

            Assert.Equal(ConfigCustomClickWireId(version), answer.WireId);
            string body = Encoding.UTF8.GetString(answer.CopyPayload());
            Assert.Contains(ActionId, body, StringComparison.Ordinal);
            Assert.Contains(InputKey, body, StringComparison.Ordinal);
            Assert.Contains(Secret, body, StringComparison.Ordinal);

            // The plugin saw the dialog body through the pre-session snapshot.
            Assert.Equal("CfgTitle", await PollStorageAsync(root, "config-dialog", "seen_title", ct));
            Assert.Equal(InputKey, await PollStorageAsync(root, "config-dialog", "seen_keys", ct));
            Assert.Equal("none", await PollStorageAsync(root, "config-dialog", "seen_registry", ct));
            string? answered = await PollStorageAsync(root, "config-dialog", "answered", ct);
            string? answerError = ReadStorageValue(root, "config-dialog", "answer_error");
            Assert.True(answered == "1", $"answered={answered ?? "<null>"}, answer_error={answerError ?? "<null>"}");

            // Finish the login; the session must reach play with no fault from the dialog traffic.
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Configuration,
                new ClientboundFinishConfigurationPacket(), ct, version);
            await server.NextFrameAsync(ct); // finish_configuration (ack)
            server.ServerConnection.SetPhase(ProtocolPhase.Play);
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                new ClientboundSetTimePacket(GameTime: 0, DayTime: 0, TickDayTime: false, ClockUpdates: []),
                ct, version);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);
        }
    }

    /// <summary>The wire id of the configuration-phase custom_click_action on this version.</summary>
    private static int ConfigCustomClickWireId(JavaVersion version)
    {
        Assert.True(version.Protocol.TryGetRegistry(
            ProtocolPhase.Configuration, PacketFlow.Serverbound, out PhaseRegistry registry));
        foreach ((int wireId, PacketType type) in registry.Packets)
        {
            if (type.Id == Identifier.Minecraft("custom_click_action"))
                return wireId;
        }

        throw new Xunit.Sdk.XunitException("No serverbound configuration custom_click_action on 1.21.6.");
    }

    /// <summary>A minimal notice dialog with one text input and one dynamic/custom button.</summary>
    private static NbtCompound BuildDialog()
    {
        var click = new NbtCompound();
        click.PutString("type", "minecraft:dynamic/custom");
        click.PutString("id", ActionId);

        var action = new NbtCompound();
        action.PutString("label", "Go");
        action.Put("action", click);

        var input = new NbtCompound();
        input.PutString("type", "minecraft:text");
        input.PutString("key", InputKey);
        input.PutString("label", "K");
        input.PutString("initial", string.Empty);
        var inputs = new NbtList();
        inputs.Add(input);

        var bodyElement = new NbtCompound();
        bodyElement.PutString("type", "minecraft:plain_message");
        bodyElement.PutString("contents", "cfg body");
        var body = new NbtList();
        body.Add(bodyElement);

        var root = new NbtCompound();
        root.PutString("type", "minecraft:notice");
        root.PutString("title", "CfgTitle");
        root.Put("body", body);
        root.Put("inputs", inputs);
        root.Put("action", action);
        return root;
    }

    /// <summary>Polls the plugin's storage file until the key appears (the answer hops off-loop).</summary>
    private static async Task<string?> PollStorageAsync(
        string root, string id, string key, CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            string? value = ReadStorageValue(root, id, key);
            if (value is not null)
                return value;

            await Task.Delay(50, ct);
        }

        return ReadStorageValue(root, id, key);
    }

    private static string NewPluginsRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-configdlg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WritePlugin(string root, string id, string entry, string source)
    {
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, entry), source);
        File.WriteAllText(
            Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\napi-version = \"1\"\nenabled = true\n"));
    }

    private static string? ReadStorageValue(string root, string id, string key)
    {
        string path = Path.Combine(root, "userdata", id, "data", "storage.toml");
        if (!File.Exists(path))
            return null;

        var store = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(path));
        return store.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>An ILoggerFactory that keeps every formatted line, so a failure can show them.</summary>
    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _lines = [];

        public IReadOnlyCollection<string> Lines => _lines;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capturing(_lines);

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capturing(System.Collections.Concurrent.ConcurrentBag<string> sink)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Add(formatter(state, exception));
        }
    }
}

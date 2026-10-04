using DMCBK.Core.Commands;
using DMCBK.Core.Localization;
using DMCBK.Core.Plugins;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Client.Plugins;
using Umpk.Geometry;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Four client-side failures.
/// Each test is written so that undoing the fix makes it FAIL, not merely stop being exercised:
/// <list type="number">
/// <item>
/// The client never told the server it had moved: <c>AutoSendPosition</c> was forced off, and even with it on UMPK stays silent for the whole of a navigation, so the walk arrived as one jump.
/// </item>
/// <item>
/// <c>chunk _delete</c> reported "Deleted chunk" while only clearing a display overlay, so the column stayed loaded and kept answering block reads.
/// </item>
/// <item>
/// A plugin kept firing after <c>unload</c> and fired TWICE after <c>reload</c>, because its event subscriptions were billed to the session rather than to the plugin.
/// </item>
/// <item>
/// <c>inventory list &lt;id&gt;</c> was not registered, so it fell through to the server even though <c>inventory inventories</c> hands the user those very ids.
/// </item>
/// </list>
/// </summary>
public sealed class AcceptanceRegressionTests
{
    // ---------------------------------------------------------------------------------------------
    // 1. The client tells the server where it is.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SessionOptions_SendThePosition()
    {
        var options = new ClientOptions();
        Client.ApplySessionOptions(options, configuration: null);

        // With this false, /move, ranged /dig and every pathfinding plugin move the client locally and leave the server holding the pre-walk position.
        Assert.True(options.AutoSendPosition);
    }

    // PositionReporter_StaysSilentWhileStanding and PositionReporter_ReportsAWalkingStep moved to UMPK: Umpk.Client.Tests.PositionReportUnderLeaseTests.PositionReportPolicy_StaysSilentBelowVanillasThreshold and .PositionReportPolicy_ReportsAWalkingStep, now that UMPK's own tick loop owns the report.

    // ---------------------------------------------------------------------------------------------
    // 2. chunk _delete either deletes or says it did not.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChunkDelete_WithNoSession_DoesNotClaimSuccess()
    {
        await using Client client = BuildClient();

        CmdResult result = await client.Commands.DispatchAsync("chunk _delete 3 4");

        // It used to answer "Deleted chunk (3, 4)." with no connection at all, because it only removed a debug-overlay entry and never asked the world for anything.
        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.DoesNotContain("Deleted chunk", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDelete_WithTerrainDisabled_FailsNeedTerrain()
    {
        // A coherent composition: terrain off derives physics and pathfinding off too (the config-loaded path always produces this shape now; see ConfigurationValidation.ValidateGameplay).
        await using Client client = BuildClient(new ClientFeatures { Terrain = false, Physics = false, Pathfinding = false });

        CmdResult result = await client.Commands.DispatchAsync("chunk _delete 3 4");

        Assert.Equal(CmdStatus.FailNeedTerrain, result.Status);
    }

    [Fact]
    public void BlockInfo_CarriesWhetherTheChunkIsEvenLoaded()
    {
        // An unloaded column reads back as air (state 0) with no way to tell that apart from a real air block, which is what made "the world keeps answering reads after a delete" invisible.
        var unloaded = new BlockInfo(
            new BlockPos(0, 64, 0), 0, "minecraft:air", true, false, false, false, false, ChunkLoaded: false);
        Assert.False(unloaded.ChunkLoaded);

        var loaded = new BlockInfo(
            new BlockPos(0, 64, 0), 1, "minecraft:stone", false, true, false, false, true);
        Assert.True(loaded.ChunkLoaded);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. A plugin fires once while loaded, once after reload, and never after unload.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A .cs plugin that subscribes to a session event exactly the way the shipped Alerts plugin does: re-subscribe on every SessionStarted, discard the handle, and let the host own the lifetime.
    /// It counts its own SessionStarted calls into storage (so a test can prove a reload/re-enable gets a genuinely fresh attach) and records when its Detached token fires.
    /// </summary>
    private const string SubscriberPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;
        using Umpk.Client.Events;

        public sealed class SubscriberPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "subscriber";

            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, e) =>
                {
                    int started = context.Storage.TryGet("started", out string? v) && int.TryParse(v, out int n) ? n : 0;
                    context.Storage.Set("started", (started + 1).ToString());
                    context.Storage.Save();

                    e.Session.Events.Subscribe<Died>(_ => { });
                    e.Session.Scheduler.OnTick(() => { });
                    e.Session.Detached.Register(() =>
                    {
                        context.Storage.Set("detached", "1");
                        context.Storage.Save();
                    });
                };
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>
    /// Hermetic, end to end: a real Client over a FakeJavaServer pipe, real login/configuration to play, so this exercises the actual UMPK extension wiring (McPluginExtension, ClientExtensionCollection, ClientPluginContext) rather than a hand-rolled ISessionScope stand-in.
    /// Events/Scheduler are now bare passthroughs to UMPK's own sealed types (nothing left to fake), so what is left to prove on the MCC side is exactly this: unloading a plugin mid-session cancels THAT plugin's own Detached token (and only that plugin's), and re-enabling it while the session is still live gets a genuinely fresh, independent scope rather than reusing or double-subscribing the old one.
    /// </summary>
    [Fact]
    public async Task PluginSession_GetsAFreshScopePerAttach_AndDetachesOnUnload()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        string root = NewPluginsRoot();
        Client client = McPluginSessionHarness.BuildClient(server, "Tester", root, NullLoggerFactory.Instance);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(client, root, NullLoggerFactory.Instance, client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            WritePlugin(root, "subscriber", "Subscriber.cs", SubscriberPluginSource);
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);
            await McPluginSessionHarness.DriveLoginAsync(server, ct);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);

            PluginContext context = host.GetContext("subscriber")!;
            Assert.True(context.InSession);
            ISessionScope firstScope = context.CurrentSession!;
            Assert.False(firstScope.Detached.IsCancellationRequested);
            Assert.Equal("1", ReadStorageValue(root, "subscriber", "started"));

            // Unload mid-session: the plugin's own Detached token must fire (proving UMPK tore down exactly this plugin's bridge, not merely stopped calling it), and the storage callback it registered through that token must have run.
            PluginActionResult unloaded = await host.UnloadAsync("subscriber", ct);
            Assert.True(unloaded.Success, unloaded.Message);
            Assert.True(firstScope.Detached.IsCancellationRequested);
            Assert.False(context.InSession);
            Assert.Equal("1", ReadStorageValue(root, "subscriber", "detached"));

            // Re-enable while the session is still live: ClientExtensionCollection.AddAsync bridges a newly added extension into the CURRENT session right away, so this plugin gets a second, independent SessionStarted without any reconnect.
            // The old defect doubled a shared subscription instead; proving the count is exactly 2 (not 1, not 3) and that the two scopes are different objects is what tells a fresh attach apart from a reused or duplicated one.
            PluginActionResult enabled = await host.EnableAsync("subscriber", ct);
            Assert.True(enabled.Success, enabled.Message);

            PluginContext reloadedContext = host.GetContext("subscriber")!;
            Assert.True(reloadedContext.InSession);
            ISessionScope secondScope = reloadedContext.CurrentSession!;
            Assert.NotSame(firstScope, secondScope);
            Assert.False(secondScope.Detached.IsCancellationRequested);
            Assert.Equal("2", ReadStorageValue(root, "subscriber", "started"));

            // Final unload: the second scope detaches too, and nothing is left live.
            PluginActionResult unloadedAgain = await host.UnloadAsync("subscriber", ct);
            Assert.True(unloadedAgain.Success, unloadedAgain.Message);
            Assert.True(secondScope.Detached.IsCancellationRequested);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 4. inventory <id> list is a command, not a line that falls through to the server.
    //
    // These cases used to dispatch "inventory list <id>", a form only this client ever had.
    // Legacy names the window FIRST and hangs the verb off it: "/inventory <player|container|<id>> list" (MinecraftClient/Commands/Inventory.cs:64-99, and the usage line it prints at Inventory.cs:123).
    // The grammar is now legacy's, so the old word order is correctly refused, with the usage, by the command that owns it. ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task InventoryList_AcceptsTheIdItsOwnInventoriesCommandPrints()
    {
        await using Client client = BuildClient();

        // NotRun is the "no such command, send it to the server" result.
        // Anything else means the dispatcher owns the line; with no session it stops at NotConnected.
        CmdResult withId = await client.Commands.DispatchAsync("inventory 1 list");
        Assert.NotEqual(CmdStatus.NotRun, withId.Status);

        CmdResult playerWindow = await client.Commands.DispatchAsync("inventory 0 list");
        Assert.NotEqual(CmdStatus.NotRun, playerWindow.Status);

        // Legacy's two named windows reach the same action (Inventory.cs:82-99).
        CmdResult named = await client.Commands.DispatchAsync("inventory player list");
        Assert.NotEqual(CmdStatus.NotRun, named.Status);

        CmdResult container = await client.Commands.DispatchAsync("inventory container list");
        Assert.NotEqual(CmdStatus.NotRun, container.Status);

        // The bare form still lists everything (legacy ListAllInventories, Inventory.cs:48).
        CmdResult bare = await client.Commands.DispatchAsync("inventory");
        Assert.NotEqual(CmdStatus.NotRun, bare.Status);

        // And the new-client-only word order is gone.
        // It is refused here, with the command's usage, rather than forwarded to the server: a misuse of a command MCC owns is MCC's to explain.
        // The assertion moved from NotRun with the incomplete-command fix (see IncompleteCommandTests); what it pins, that this word order does not run, is unchanged.
        CmdResult oldForm = await client.Commands.DispatchAsync("inventory list 1");
        Assert.Equal(CmdStatus.Fail, oldForm.Status);
        Assert.Contains("inventory ", oldForm.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryList_WithIdStillRespectsTheInventoryGate()
    {
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });

        CmdResult result = await client.Commands.DispatchAsync("inventory 1 list");

        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // 5. Inventory window guard and lookup.
    //
    // Each of list, open, close, click and drop runs the feature gate, then the session check, then resolves the window before doing any work.
    // These tests pin that order, the localized missing-window messages, the player versus container resolution, the empty-slot guard on drop, and how snapshot failures and cancellation surface through the dispatcher. ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inventory 0 list")]
    [InlineData("inventory 0 open")]
    [InlineData("inventory 0 close")]
    [InlineData("inventory 0 click 0")]
    [InlineData("inventory 0 drop 0")]
    public async Task GuardOrder_InventoryGateBeatsSessionGate_ForAllWindowActions(string line)
    {
        // No session either, so a reordered guard would answer NotConnected instead.
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Theory]
    [InlineData("inventory 0 list")]
    [InlineData("inventory 0 open")]
    [InlineData("inventory 0 close")]
    [InlineData("inventory 0 click 0")]
    [InlineData("inventory 0 drop 0")]
    public async Task Disconnected_AllWindowActionsReportNotConnected_AndWriteNothing(string line)
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("Not connected", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(output.Lines);
    }

    [Theory]
    [InlineData("inventory 9999 list")]
    [InlineData("inventory 9999 open")]
    [InlineData("inventory 9999 close")]
    [InlineData("inventory 9999 click 0")]
    [InlineData("inventory 9999 drop 0")]
    public async Task UnknownWindow_AllActionsFailWithNotExist(string line)
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        int faults = 0;
        client.Commands.CommandFaulted += (_, _) => faults++;

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(McStrings.Format("cmd.inventory.not_exist", 9999), result.Message);
        Assert.Empty(output.Lines);
        Assert.Equal(0, faults);
    }

    [Theory]
    [InlineData("inventory container list")]
    [InlineData("inventory container open")]
    [InlineData("inventory container close")]
    [InlineData("inventory container click 0")]
    [InlineData("inventory container drop 0")]
    public async Task ContainerWithNoneOpen_AllActionsFailWithContainerNotFound(string line)
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        int faults = 0;
        client.Commands.CommandFaulted += (_, _) => faults++;

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(McStrings.Get("cmd.inventory.container_not_found"), result.Message);
        Assert.Empty(output.Lines);
        Assert.Equal(0, faults);
    }

    [Fact]
    public async Task PlayerWindow_ListParity_AndContainerMissing_SameIdleSession()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        // The numeric id and the named alias resolve the same player window.
        CmdResult byId = await client.Commands.DispatchAsync("inventory 0 list");
        Assert.Equal(CmdStatus.Done, byId.Status);
        Assert.NotEmpty(output.Lines);
        string byIdText = output.Text;
        output.Clear();

        CmdResult byName = await client.Commands.DispatchAsync("inventory player list");
        Assert.Equal(CmdStatus.Done, byName.Status);
        Assert.NotEmpty(output.Lines);
        Assert.Equal(byIdText, output.Text);
        output.Clear();

        // The same session has no open container, so the container selector fails instead.
        CmdResult container = await client.Commands.DispatchAsync("inventory container list");
        Assert.Equal(CmdStatus.Fail, container.Status);
        Assert.Equal(McStrings.Get("cmd.inventory.container_not_found"), container.Message);
        Assert.Empty(output.Lines);
    }

    [Fact]
    public async Task PlayerOpen_WithoutUi_ReportsTuiOnly()
    {
        await using Client client = BuildClient();
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        CmdResult result = await client.Commands.DispatchAsync("inventory 0 open");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(McStrings.Get("cmd.inventory.tui_only"), result.Message);
    }

    [Fact]
    public async Task DropEmptySlot_ReportsNoItem_WithoutFault()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        int faults = 0;
        client.Commands.CommandFaulted += (_, _) => faults++;

        // The idle player inventory is empty, so slot 0 fails the empty-slot guard before any send.
        CmdResult result = await client.Commands.DispatchAsync("inventory 0 drop 0");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(McStrings.Format("cmd.inventory.no_item", 0), result.Message);
        Assert.Empty(output.Lines);
        Assert.Equal(0, faults);
    }

    [Theory]
    [InlineData("inventory 0 list")]
    [InlineData("inventory 0 open")]
    [InlineData("inventory 0 close")]
    [InlineData("inventory 0 click 0")]
    [InlineData("inventory 0 drop 0")]
    public async Task SnapshotFailure_AllActionsReportOneFault_AndNoActionMessage(string line)
    {
        // MCC keeps inventory enabled so the gate and session check pass; the bound UMPK client has inventory disabled, so the first snapshot read throws.
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient(inventoryDisabled: true);
        client.AttachIdleSessionForTesting(umpk);

        var faults = new List<CommandFaultedEventArgs>();
        client.Commands.CommandFaulted += (_, e) => faults.Add(e);

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        CommandFaultedEventArgs fault = Assert.Single(faults);
        var snapshotFault = Assert.IsType<DmcbkFeatureDisabledException>(fault.Exception);
        Assert.Equal(CommandStrings.Error(snapshotFault.Message), result.Message);
        Assert.Empty(output.Lines);
        Assert.DoesNotContain(McStrings.Get("cmd.inventory.close_fail"), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(McStrings.Get("cmd.inventory.shiftclick_fail"), result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inventory 0 list")]
    [InlineData("inventory 0 open")]
    [InlineData("inventory 0 close")]
    [InlineData("inventory 0 click 0")]
    [InlineData("inventory 0 drop 0")]
    public async Task Cancellation_Propagates_WithoutFault(string line)
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        await using Umpk.Client.UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        int faults = 0;
        client.Commands.CommandFaulted += (_, _) => faults++;

        // A cancelled dispatch escapes the command and the dispatcher rethrows without recording a fault.
        // A pre-cancelled token may be observed upstream, so this does not pin snapshot cancellation timing specifically.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.Commands.DispatchAsync(line, new CancellationToken(canceled: true)));
        Assert.Equal(0, faults);
    }

    // ---------------------------------------------------------------------------------------------

    private static Client BuildClient(ClientFeatures? features = null, BufferedCommandOutput? output = null)
    {
        var builder = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost");
        if (features is not null)
            builder.UseFeatures(features);

        if (output is not null)
            builder.UseHostInterface(new CapturingHost(output));

        return builder.Build();
    }

    private sealed class CapturingHost(BufferedCommandOutput output) : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public Umpk.Auth.IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => output;

        public IHostUi? Ui => null;
    }

    private static Umpk.Client.UmpkClient IdleUmpkClient(bool inventoryDisabled = false)
    {
        Assert.True(
            Umpk.Data.Java.JavaVersions.TryGetByName("1.21.5", out Umpk.Protocol.Java.JavaVersion? version),
            "Unknown version '1.21.5'.");
        return new Umpk.Client.UmpkClientBuilder()
            .UseVersion(version!)
            .UseProfile(new Umpk.GameProfile(Guid.NewGuid(), "Tester"))
            .ConfigureFeatures(f =>
            {
                f.Physics = false;
                f.Pathfinding = false;
                if (inventoryDisabled)
                    f.Inventory = false;
            })
            .Build();
    }

    /// <summary>A fresh temp plugins root for the hermetic session test.</summary>
    private static string NewPluginsRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-m6-tests", Guid.NewGuid().ToString("N"));
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
}

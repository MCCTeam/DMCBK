using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Client;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Entity handling, villager trading, enchanting, and disconnect tests: reads, actions, target resolution, bounds, manifests, events, and the movement follow-entity kind.
/// All headless through the script test host; fake-server round trips live next door.
/// </summary>
public sealed class EntityTradeEnchantTests
{
    private static (BeaconEngine Engine, ScriptTestHost Host) FreshEngine()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        return (engine, host);
    }

    private static BeaconEntityInfo Mob(
        int id, string type, double x, double y = 64, double z = 0, string? customName = null) =>
        new(id, Guid.NewGuid().ToString("N"), type, x, y, z, 0, 0, "Standing", true, null, customName, false);

    private static BeaconEntityInfo Player(string name, int id, double x, double y = 64, double z = 0) =>
        new(id, Guid.NewGuid().ToString("N"), "minecraft:player", x, y, z, 90, 0, "Standing", true, name, null, true);

    private static void SeedNearby(ScriptTestHost host)
    {
        host.PositionValue = new BeaconPosition(0, 64, 0, null, null);
        host.Entities = [
            Mob(1, "minecraft:zombie", 3),
            Mob(2, "minecraft:creeper", 10),
            Mob(3, "minecraft:zombie", 5, customName: "Bob"),
            Player("Steve", 4, 30),
        ];
    }

    private static async Task<string> ShowAsync(BeaconEngine engine, string body)
    {
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", body);
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        return Assert.Single(run.LocalOutput);
    }

    #region entities.* reads

    [Fact]
    public async Task Near_ReturnsRowsNearestFirst_WithDistance()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(engine, "set near to entities.near()\nshow \"{len(near)} {near[0].type} {near[0].distance} {near[1].type}\"");
        Assert.Equal("4 minecraft:zombie 3 minecraft:zombie", output);
    }

    [Fact]
    public async Task Near_RadiusAndMax_Paginate()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(engine, "set near to entities.near(6, 10)\nshow len(near)");
        Assert.Equal("2", output);
    }

    [Fact]
    public async Task Near_EmptyWhenNobodyTracked()
    {
        var (engine, host) = FreshEngine();
        host.PositionValue = new BeaconPosition(0, 64, 0, null, null);

        string output = await ShowAsync(engine, "show len(entities.near())");
        Assert.Equal("0", output);
    }

    [Fact]
    public async Task Near_NegativeRadius_RefusesCatchably()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe",
            "try\nshow len(entities.near(-1))\ncatch err\nshow \"{err.code}\"\nend try");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task OfType_MatchesBareAndNamespaced()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine,
            "show \"{len(entities.of_type(\"zombie\"))} {len(entities.of_type(\"minecraft:player\"))} {len(entities.of_type(\"player\", 64, 5))}\"");
        Assert.Equal("2 1 1", output);
    }

    [Fact]
    public async Task ById_FoundAndMissing()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine,
            "set z to entities.by_id(1)\nset nope to entities.by_id(999)\nshow \"{z.type} {nope is none}\"");
        Assert.Equal("minecraft:zombie yes", output);
    }

    [Fact]
    public async Task Nearest_TextMatcherPicksClosestHit()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine,
            "set z to entities.nearest(\"zombie\")\nshow \"{z.id} {z.distance}\"");
        Assert.Equal("1 3", output);
    }

    [Fact]
    public async Task Nearest_PlayerNameAndCustomNameMatch()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine,
            "show \"{entities.nearest(\"steve\").id} {entities.nearest(\"bob\").id}\"");
        Assert.Equal("4 3", output);
    }

    [Fact]
    public async Task Nearest_MissAnswersNone()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(engine, "show entities.nearest(\"enderman\") is none");
        Assert.Equal("yes", output);
    }

    [Fact]
    public async Task Count_AllAndFiltered()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine, "show \"{entities.count()} {entities.count(\"zombie\")} {entities.count(4)}\"");
        Assert.Equal("4 2 1", output);
    }

    [Fact]
    public async Task RowMap_CarriesIdentityAndPose()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        string output = await ShowAsync(
            engine,
            "set s to entities.nearest(\"steve\")\nshow \"{s.name} {s.is_player} {s.pose} {s.x} {s.y} {s.z}\"");
        Assert.Equal("Steve yes Standing 30 64 0", output);
    }

    #endregion

    #region attack / interact

    [Fact]
    public async Task Attack_Text_UsesLegacyPath()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "attack(\"zombie\")");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("attack zombie", Assert.Single(host.Moves));
    }

    [Fact]
    public async Task Attack_IdAndRow_UseIdPath()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "attack(2)\nattack(entities.by_id(1))");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(["attack-id 2", "attack-id 1"], host.EntityActions);
    }

    [Fact]
    public async Task Attack_WrongKind_RefusesWithFix()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "attack(yes)");
        Assert.False(run.Success);
        Assert.Contains("attack", run.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interact_TextAndId()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "interact(\"villager\")\ninteract(3)");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("interact villager", Assert.Single(host.Moves));
        Assert.Equal("interact-id 3", Assert.Single(host.EntityActions));
    }

    #endregion

    #region trade.*

    private static void SeedMerchant(ScriptTestHost host)
    {
        host.Trades = [
            new BeaconTradeOffer(
                0,
                new BeaconTradeItem("minecraft:emerald", "emerald", 5),
                null,
                new BeaconTradeItem("minecraft:bread", "bread", 3),
                1, 12, false, 2),
            new BeaconTradeOffer(
                1,
                new BeaconTradeItem("minecraft:emerald", "emerald", 32),
                new BeaconTradeItem("minecraft:book", "book", 1),
                new BeaconTradeItem("minecraft:enchanted_book", "enchanted book", 1),
                12, 12, true, 5),
        ];
        host.TradeBuyResult = 2;
    }

    [Fact]
    public async Task TradeList_RowsCarryOfferShape()
    {
        var (engine, host) = FreshEngine();
        SeedMerchant(host);

        string output = await ShowAsync(
            engine,
            "set offers to trade.list()\nset o to offers[0]\nshow \"{len(offers)} {o.result.type} {o.first.count} {o.uses} {o.max_uses} {o.sold_out}\"");
        Assert.Equal("2 minecraft:bread 5 1 12 no", output);
    }

    [Fact]
    public async Task TradeList_SecondInput_AndSoldOut()
    {
        var (engine, host) = FreshEngine();
        SeedMerchant(host);

        string output = await ShowAsync(
            engine,
            "set o to trade.list()[1]\nshow \"{o.second.type} {o.sold_out} {o.xp}\"");
        Assert.Equal("minecraft:book yes 5", output);
    }

    [Fact]
    public async Task TradeList_NoMerchant_RaisesCatchable()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe",
            "try\ntrade.list()\ncatch err\nshow \"{err.code}\"\nend try");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("B4002", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task TradeSelect_GoodIndexAnswersYes_BadAnswersNo()
    {
        var (engine, host) = FreshEngine();
        SeedMerchant(host);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "show trade.select(0)\nshow trade.select(9)");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(["yes", "no"], run.LocalOutput);
        Assert.Equal([0, 9], host.TradeSelects);
    }

    [Fact]
    public async Task TradeBuy_AnswersCompletedUnits()
    {
        var (engine, host) = FreshEngine();
        SeedMerchant(host);

        string output = await ShowAsync(engine, "show trade.buy(0, 2)");
        Assert.Equal("2", output);
        Assert.Equal((0, 2), Assert.Single(host.TradeBuys));
    }

    [Fact]
    public async Task TradeBuy_BadCount_Refuses()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "trade.buy(0, 0)");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, run.Error!.Code);
    }

    #endregion

    #region enchant.*

    [Fact]
    public async Task EnchantOptions_RowsCarrySlotAndLevel()
    {
        var (engine, host) = FreshEngine();
        host.Enchantments = [
            new BeaconEnchantOption(0, 1),
            new BeaconEnchantOption(1, 7),
            new BeaconEnchantOption(2, null),
        ];

        string output = await ShowAsync(
            engine,
            "set opts to enchant.options()\nshow \"{len(opts)} {opts[0].level} {opts[1].level} {opts[2].level is none}\"");
        Assert.Equal("3 1 7 yes", output);
    }

    [Fact]
    public async Task EnchantOptions_NoTable_RaisesCatchable()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe",
            "try\nenchant.options()\ncatch err\nshow \"{err.code}\"\nend try");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("B4002", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task EnchantChoose_NamesAndSlots()
    {
        var (engine, host) = FreshEngine();
        host.Enchantments = [new BeaconEnchantOption(0, 1), new(1, 7), new(2, 30)];

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "show enchant.choose(\"bottom\")\nshow enchant.choose(0)");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(["yes", "yes"], run.LocalOutput);
        Assert.Equal([2, 0], host.EnchantChooses);
    }

    [Fact]
    public async Task EnchantChoose_BadSlot_RefusesWithFix()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "enchant.choose(\"side\")");
        Assert.False(run.Success);
        Assert.Contains("bottom", run.Error!.Message, StringComparison.Ordinal);
    }

    #endregion

    #region disconnect

    [Fact]
    public async Task Disconnect_BareAndReason_Recorded()
    {
        var (engine, host) = FreshEngine();

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "disconnect\ndisconnect \"bye\"");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal([null, "bye"], host.Disconnects);
    }

    [Fact]
    public async Task Disconnect_WrongKind_RefusesWithFix()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "disconnect 123");
        Assert.False(run.Success);
        Assert.Contains("disconnect", run.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VanishOnSight_PlayerInRange_Disconnects()
    {
        var (engine, host) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "watcher",
            "on entity_add when is_player is yes\n  disconnect(\"player nearby\")\nend on\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");

        BeaconFireResult fire = await engine.FireEventAsync(
            "entity_add", BeaconEventFields.EntityAdd(Player("Steve", 9, 12), 12));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Equal("player nearby", Assert.Single(host.Disconnects));
    }

    [Fact]
    public async Task VanishOnSight_MobInRange_StaysConnected()
    {
        var (engine, host) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "watcher",
            "on entity_add when is_player is yes\n  disconnect(\"player nearby\")\nend on\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");

        BeaconFireResult fire = await engine.FireEventAsync(
            "entity_add", BeaconEventFields.EntityAdd(Mob(7, "minecraft:zombie", 4), 4));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Empty(host.Disconnects);
    }

    #endregion

    #region manifests

    [Fact]
    public void Manifest_EntitiesRead_Inferred()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource("radar", "radar.mcc", "# beacon 1\n# needs: chat.send\nshow len(entities.near())");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("radar"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("entity.read", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("# needs: chat.send entity.read", refusal.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_AttackAndInteract_NeedEntityWrite()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource(
            "guard", "guard.mcc",
            "# beacon 1\n# needs: entity.read\nattack(\"zombie\")\ninteract(1)");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("guard"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("entity.write", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_Disconnect_NeedsServerDisconnect()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource("leave", "leave.mcc", "# beacon 1\ndisconnect \"done\"");

        Assert.DoesNotContain(
            engine.Lint("leave"), d => d.Severity == BeaconSeverity.Error);

        engine.LoadSource("leave2", "leave2.mcc", "# beacon 1\n# needs: chat.send\ndisconnect");
        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("leave2"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("server.disconnect", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_EntityHooks_InferEntityRead()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource(
            "watcher", "watcher.mcc",
            "# beacon 1\n# needs: chat.send\non entity_add when is_player is yes\nshow \"hi\"\nend on\n");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("watcher"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("entity.read", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_TradeSelect_NeedsInventoryWrite()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource(
            "shop", "shop.mcc",
            "# beacon 1\n# needs: inventory.read\ntrade.select(0)");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("shop"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("inventory.write", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_EntityHooks_Known_NoWarning()
    {
        var (engine, _) = FreshEngine();
        engine.LoadSource(
            "watcher", "watcher.mcc",
            "# beacon 1\non entity_add\nshow \"hi\"\nend on\non entity_remove\nshow \"bye\"\nend on\n");

        Assert.DoesNotContain(
            engine.Lint("watcher"), d => d.Code == BeaconDiagnosticCodes.UnknownEvent);
    }

    #endregion

    #region diff + movement

    [Fact]
    public void EntityWatch_Diff_AddedRemovedAndStable()
    {
        BeaconEntityInfo a = Mob(1, "minecraft:zombie", 3);
        BeaconEntityInfo b = Mob(2, "minecraft:creeper", 10);
        BeaconEntityInfo c = Player("Steve", 4, 30);

        BeaconEntityWatch.Diff([a, b], [b, c], out List<BeaconEntityInfo> added, out List<BeaconEntityInfo> removed);
        Assert.Equal(c, Assert.Single(added));
        Assert.Equal(a, Assert.Single(removed));

        BeaconEntityWatch.Diff([a], [a], out added, out removed);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task MovementRunner_FollowEntity_ReachesThroughExecutor()
    {
        var scope = new BeaconFreeMovementScope();
        BeaconMoveTarget? seen = null;
        var runner = new BeaconMovementRunner(
            scope, BeaconGameplayGates.AllOn,
            (target, _) =>
            {
                seen = target;
                return Task.FromResult(new BeaconMoveArrival(true, "follow ended"));
            });

        BeaconValue arrival = await runner.FollowEntityAsync(4242, "#4242", "s1");
        Assert.NotNull(seen);
        Assert.Equal(BeaconMovementKind.FollowEntity, seen.Kind);
        Assert.Equal(4242, seen.EntityId);
        Assert.True(arrival is BeaconMapValue);
    }

    [Fact]
    public async Task MovementRunner_FollowEntity_NeedsEntityGate()
    {
        var runner = new BeaconMovementRunner(
            new BeaconFreeMovementScope(), BeaconGameplayGates.AllOn with { Entity = false });

        BeaconRuntimeException refused = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => runner.FollowEntityAsync(7, "#7", "s1"));
        Assert.Contains("Entity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveGoto_Row_WalksToDescribedPosition()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);
        BeaconMoveTarget? seen = null;
        engine.MovementBinder = _ => new BeaconMovementBinding(
            new BeaconFreeMovementScope(),
            (target, _) =>
            {
                seen = target;
                return Task.FromResult(new BeaconMoveArrival(true, "arrived"));
            },
            null);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "move_goto(entities.by_id(1))");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.NotNull(seen);
        Assert.Equal(BeaconMovementKind.Goto, seen.Kind);
        Assert.Equal(3, seen.X);
        Assert.Equal(64, seen.Y);
        Assert.Equal(0, seen.Z);
    }

    [Fact]
    public async Task MoveGoto_RowWithOptions_PassesToleranceThrough()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);
        BeaconMoveTarget? seen = null;
        engine.MovementBinder = _ => new BeaconMovementBinding(
            new BeaconFreeMovementScope(),
            (target, _) =>
            {
                seen = target;
                return Task.FromResult(new BeaconMoveArrival(true, "arrived"));
            },
            null);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "move_goto(entities.nearest(\"steve\"), {tolerance: 2})");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.NotNull(seen);
        Assert.Equal(30, seen.X);
        Assert.Equal(2, seen.Options?.Tolerance);
    }

    [Fact]
    public async Task MoveGoto_RowWithoutPosition_RefusesWithFix()
    {
        var (engine, _) = FreshEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "probe", "move_goto({a: 1})");
        Assert.False(run.Success);
        Assert.Contains("x, y, z", run.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookAt_Row_AimsAtDescribedPosition()
    {
        var (engine, host) = FreshEngine();
        SeedNearby(host);

        BeaconRunResult run = await ScriptTestHelpers.RunAsync(
            engine, "probe", "look_at(entities.by_id(2))");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("look 10 64 0", Assert.Single(host.Moves));
    }

    #endregion
}

/// <summary>
/// Fake-server round trips: spawn/remove packets reach the tracker, fire <c>entity_add</c>/<c>entity_remove</c> into scripts, and id-targeted actions put attack frames on the wire.
/// No live server needed.
/// </summary>
public sealed class EntityRoundTripTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task<(FakeJavaServer Server, Client Client, string Root)> StartClientAsync(
        CancellationToken ct)
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-entity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        var server = FakeJavaServer.Create();
        Client client = McPluginSessionHarness.BuildClient(
            server, "Tester", Path.Combine(root, "plugins"), NullLoggerFactory.Instance);
        Task starting = client.StartAsync(ct);
        await McPluginSessionHarness.DriveLoginAsync(server, ct);
        await starting.WaitAsync(McPluginSessionHarness.Budget, ct);
        Assert.Equal(ClientStatus.Playing, client.Status);
        return (server, client, root);
    }

    private static async Task RunScriptAsync(Client client, string root, string name, string body)
    {
        string path = Path.Combine(root, name + ".mcc");
        await File.WriteAllTextAsync(path, body);
        CmdResult run = await client.Commands.DispatchAsync($"scripts run \"{path}\"");
        Assert.Equal(CmdStatus.Done, run.Status);
    }

    private static async Task SendAsync<TPacket>(FakeJavaServer server, TPacket packet, CancellationToken ct)
        where TPacket : class, Umpk.Protocol.Java.IPacket
        => await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play, packet, ct);

    private static async Task WaitForServerTextAsync(
        FakeJavaServer server, string needle, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            InboundFrame frame;
            try
            {
                frame = await server.NextFrameAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for a serverbound frame containing '{needle}'.");
                throw new InvalidOperationException("Unreachable.");
            }

            if (Encoding.UTF8.GetString(frame.Payload).Contains(needle, StringComparison.Ordinal))
                return;
        }
    }

    private static ClientboundAddEntityPacket SpawnPacket(int id, double x, double y, double z) =>
        new(id, Guid.NewGuid(), 999, x, y, z, 0, 0, 0, 0, 0, 0, 0, null);

    [Fact]
    public async Task EntityAdd_PacketToSay_RoundTrip()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "spotter",
                "# beacon 1\n# needs: chat.send entity.read\n" +
                "on entity_add when id is 4242\nsay \"saw {id} {type}\"\nend on\n");
            // The first poll only baselines: let two pump cycles pass before spawning.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            await SendAsync(server, SpawnPacket(4242, 0, 64, 0), ct);
            await WaitForServerTextAsync(server, "saw 4242 minecraft:unknown", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task EntityRemove_PacketToSay_RoundTrip()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "watcher",
                "# beacon 1\n# needs: chat.send entity.read\n" +
                "on entity_add when id is 4243\nsay \"saw {id}\"\nend on\n" +
                "on entity_remove when id is 4243\nsay \"gone {id}\"\nend on\n");
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            await SendAsync(server, SpawnPacket(4243, 0, 64, 0), ct);
            await WaitForServerTextAsync(server, "saw 4243", TimeSpan.FromSeconds(10), ct);
            await SendAsync(server, new ClientboundRemoveEntitiesPacket([4243]), ct);
            await WaitForServerTextAsync(server, "gone 4243", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task NearbyEntities_SeesSpawnedEntity_ByIdAndRadius()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, _) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            var pose = await client.Game.Movement.GetPoseAsync(ct);
            await SendAsync(
                server, SpawnPacket(4244, pose.Position.X + 5, pose.Position.Y, pose.Position.Z), ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            var host = new BeaconClientHost(client, () => false, () => { });
            Assert.Contains(host.NearbyEntities(64), e => e.Id == 4244);
            Assert.DoesNotContain(host.NearbyEntities(2), e => e.Id == 4244);
            Assert.NotNull(host.EntityById(4244));
            Assert.Null(host.EntityById(9999));
        }
    }

    [Fact]
    public async Task StopAsync_WithScriptsPumpRunning_Completes()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "pump",
                "# beacon 1\n# needs: chat.send\non chat\nshow \"hi\"\nend on\n");
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.StopAsync().WaitAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task StopAsync_WithEntityHandlers_Completes()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "watcher",
                "# beacon 1\n# needs: chat.send entity.read\n" +
                "on entity_add when is_player is yes\nsay \"hi {name}\"\nend on\n");
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.StopAsync().WaitAsync(stopCts.Token);
        }
    }
    [Fact]
    public async Task AttackEntity_PutsFrameOnWire()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, _) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            var pose = await client.Game.Movement.GetPoseAsync(ct);
            await SendAsync(
                server, SpawnPacket(4245, pose.Position.X + 2, pose.Position.Y, pose.Position.Z), ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            var host = new BeaconClientHost(client, () => false, () => { });
            Assert.NotNull(host.EntityById(4245));

            // Drain login chatter so the next frame must be ours.
            using (var drain = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            {
                try
                {
                    while (true)
                        await server.NextFrameAsync(drain.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await host.AttackEntityAsync(4245, ct);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            InboundFrame frame = await server.NextFrameAsync(budget.Token);
            Assert.True(frame.Payload.Length > 0);
        }
    }
}

using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Builtin library freeze-list tests: JSON round trip, matchers, game tables with neighbor suggestions, time rendering through the corpus, chat helpers, inventory and world reads, movement faces, math and text helpers on the seeded RNG, <c>eat()</c>, and <c>beacon.lib</c>.
/// </summary>
public sealed class BuiltinTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private static (BeaconEngine Engine, ScriptTestHost Host, VirtualClock Clock) NewEngine(int seed = 7)
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host, clock);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    private static async Task<string> ShowAsync(BeaconEngine engine, string scriptId, string expr)
    {
        BeaconRunResult run = await engine.RunScriptAsync(scriptId, WithHeader($"show {expr}\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        return Assert.Single(run.LocalOutput);
    }

    [Fact]
    public async Task Json_RoundTripsMapsListsAndScalars()
    {
        var (engine, _, _) = NewEngine();
        string back = await ShowAsync(engine, "j",
            "json_stringify(json_parse(json_stringify({name: \"bread\", price: 5, listed: yes, tags: [\"food\", none]})))");
        Assert.Contains("\"name\":\"bread\"", back);
        Assert.Contains("\"price\":5", back);
        Assert.Contains("\"listed\":true", back);
        Assert.Contains("\"tags\":[\"food\",null]", back);
        Assert.Equal("bread", await ShowAsync(engine, "j2", "json_parse(json_stringify({name: \"bread\"})).name"));
    }

    [Fact]
    public async Task Json_ParseFailure_IsCatchable()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("j", WithHeader(
            "try\nshow json_parse(\"{{oops\")\ncatch err\nshow err.code\nend try\n"));
        Assert.True(run.Success);
        Assert.Equal("B4012", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Matchers_TextFormIgnoresCasePrefixAndSeparators()
    {
        var (engine, _, _) = NewEngine();
        string count = await ShowAsync(engine, "m",
            "inv.count(\"COMPASS\")");
        Assert.Equal("0", count);

        var (engine2, host2, _) = NewEngine();
        host2.Slots =
        [
            new BeaconInvSlot(0, "minecraft:compass", "Server Compass", 1, null),
            new BeaconInvSlot(1, "minecraft:cooked_beef", "Steak", 3, null),
            new BeaconInvSlot(2, "minecraft:dirt", "Dirt", 64, null),
        ];
        Assert.Equal("1", await ShowAsync(engine2, "m2", "inv.count(\"compass\")"));
        Assert.Equal("1", await ShowAsync(engine2, "m3", "inv.count(\"minecraft:compass\")"));
        Assert.Equal("1", await ShowAsync(engine2, "m4", "inv.count(\"Server Compass\")"));
        Assert.Equal("1", await ShowAsync(engine2, "m6", "inv.find(\"cooked_beef\")"));
        Assert.Equal("1", await ShowAsync(engine2, "m6b", "inv.find(\"cooked-beef\")"));
        Assert.Equal("yes", await ShowAsync(engine2, "m7", "inv.has(\"cooked beef\", 3)"));
        Assert.Equal("[0]", await ShowAsync(engine2, "m8", "inv.find_all(\"compass\")"));
    }

    [Fact]
    public async Task Matchers_MapFormSupportsNameContainsMinCount()
    {
        var (engine, host, _) = NewEngine();
        host.Slots =
        [
            new BeaconInvSlot(0, "minecraft:compass", "Server Compass", 1, ["Given at spawn"]),
            new BeaconInvSlot(1, "minecraft:compass", "Plain Compass", 1, null),
        ];
        Assert.Equal("1", await ShowAsync(engine, "q", "inv.count({type: \"compass\", name_contains: \"server\"})"));
        Assert.Equal("0", await ShowAsync(engine, "q2", "inv.count({type: \"compass\", min_count: 2})"));
        Assert.Equal("2", await ShowAsync(engine, "q3", "inv.count({type: \"compass\", min_count: 1})"));
        Assert.Equal("1", await ShowAsync(engine, "q4", "inv.count({lore_contains: \"spawn\"})"));
    }

    [Fact]
    public async Task Inv_ReadsExposeSlotsSelectedAndArmor()
    {
        var (engine, host, _) = NewEngine();
        host.Slots = [new BeaconInvSlot(5, "minecraft:totem_of_undying", "Totem of Undying", 1, null)];
        host.SelectedSlotValue = 2;
        host.Armor["offhand"] = new BeaconInvSlot(40, "minecraft:shield", "Shield", 1, null);

        Assert.Equal("5", await ShowAsync(engine, "i", "inv.find(\"totem_of_undying\")"));
        Assert.Equal("2", await ShowAsync(engine, "i2", "inv.selected()"));
        Assert.Equal("1", await ShowAsync(engine, "i3", "len(inv.list())"));
        Assert.Contains("offhand", await ShowAsync(engine, "i4", "keys(inv.armor())"));
    }

    [Fact]
    public async Task Inv_WritesReturnYesAndRecordCalls()
    {
        var (engine, host, _) = NewEngine();
        Assert.Equal("yes", await ShowAsync(engine, "w", "inv.select(5)"));
        Assert.Equal("yes", await ShowAsync(engine, "w2", "inv.move(5, \"offhand\")"));
        Assert.Equal("yes", await ShowAsync(engine, "w3", "inv.drop()"));
        Assert.Equal("yes", await ShowAsync(engine, "w4", "inv.drop_stack()"));
        Assert.Equal("yes", await ShowAsync(engine, "w5", "inv.click(5, \"right\")"));
        Assert.Single(host.SelectCalls);
        Assert.Equal((5, "offhand"), Assert.Single(host.MoveCalls));
    }

    [Fact]
    public async Task Tables_ResolveFullIdsAndSuggestNeighbors()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("minecraft:totem_of_undying",
            await ShowAsync(engine, "t", "items.totem_of_undying"));
        Assert.Equal("minecraft:compass", await ShowAsync(engine, "t2", "items.compass"));

        BeaconRunResult run = await engine.RunScriptAsync("t3", WithHeader(
            "try\nshow items.totem_of_undynig\ncatch err\nshow err.message\nend try\n"));
        Assert.True(run.Success);
        Assert.Contains("totem_of_undying", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Tables_ReadFromDatasetWhenProtocolKnown()
    {
        IReadOnlySet<string> ids = BeaconGameTables.ItemIds(767);
        Assert.NotEmpty(ids);
        Assert.Contains("minecraft:totem_of_undying", ids);
        Assert.NotEmpty(BeaconGameTables.EffectIds(767));
        Assert.NotEmpty(BeaconGameTables.EnchantIds(767));
    }

    [Fact]
    public async Task BeaconLib_ReportsCurrentVersion()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal(BeaconLint.CurrentLibVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
            await ShowAsync(engine, "v", "beacon.lib"));
    }

    [Fact]
    public async Task TimeAgo_RendersThroughCorpus()
    {
        var (engine, host, _) = NewEngine();
        host.ProtocolValue = null;
        Assert.Equal("just now", await ShowAsync(engine, "a", "time.ago(time.stamp)"));
        string five = await ShowAsync(engine, "a2", "time.ago(time.stamp - 300)");
        Assert.Contains("5", five);
        Assert.Contains("minutes ago", five);
    }

    [Fact]
    public async Task ChatHelpers_ReadHistory()
    {
        var (engine, host, clock) = NewEngine();
        DateTimeOffset now = clock.UtcNow;
        host.ChatLines =
        [
            new BeaconChatLine("[Steve] !bid 5", now - TimeSpan.FromMinutes(2)),
            new BeaconChatLine("[Alex] hello", now - TimeSpan.FromMinutes(1)),
            new BeaconChatLine("[Steve] !bid 6", now),
        ];
        Assert.Equal("[Steve] !bid 6", await ShowAsync(engine, "c", "last_from(\"Steve\")"));
        Assert.Equal("2", await ShowAsync(engine, "c2", "count_matching(\"!bid\", 10)"));
        Assert.Equal("1", await ShowAsync(engine, "c3", "count_matching(\"!bid\", 1)"));
        Assert.Equal("3", await ShowAsync(engine, "c4", "len(chat_history(10))"));
    }

    [Fact]
    public async Task MeAndServer_RenderKnownFieldsAndNoneForUnknown()
    {
        var (engine, host, _) = NewEngine();
        host.VitalsValue = new BeaconVitals(10, 20, 14, 5, 300, 3, 2);
        host.PositionValue = new BeaconPosition(100, 64, -30, 90, 0);
        host.GamemodeValue = "survival";
        host.PingValue = 42;
        host.SneakingValue = false;
        host.EffectsValue = [new BeaconEffectInfo("minecraft:speed", 1, 60)];
        host.ServerInfoValue = new BeaconServerInfo("play.example.com", 25565, "1.21", 100, "Hi", 6000, 12, "clear", "normal");

        Assert.Equal("10", await ShowAsync(engine, "s", "me.health"));
        Assert.Equal("20", await ShowAsync(engine, "s2", "me.max_health"));
        Assert.Equal("14", await ShowAsync(engine, "s3", "food"));
        Assert.Equal("100", await ShowAsync(engine, "s4", "me.pos.x"));
        Assert.Equal("survival", await ShowAsync(engine, "s5", "me.gamemode"));
        Assert.Equal("42", await ShowAsync(engine, "s6", "me.ping"));
        Assert.Equal("no", await ShowAsync(engine, "s7", "me.is_sneaking"));
        Assert.Equal("1", await ShowAsync(engine, "s8", "len(me.effects)"));
        Assert.Equal("100", await ShowAsync(engine, "s9", "server.max_players"));
        Assert.Equal("clear", await ShowAsync(engine, "s10", "server.weather"));
        Assert.Equal("50", await ShowAsync(engine, "s11", "server.mspt"));
        Assert.Equal("none", await ShowAsync(engine, "s12", "me.uuid"));
    }

    [Fact]
    public async Task World_LightAndBiomeYieldNoneWhenUnknown()
    {
        var (engine, host, _) = NewEngine();
        host.Light[(1, 64, 1)] = 12;
        host.Biomes[(1, 64, 1)] = "minecraft:plains";
        Assert.Equal("12", await ShowAsync(engine, "l", "world.light_at(1, 64, 1)"));
        Assert.Equal("minecraft:plains", await ShowAsync(engine, "l2", "world.biome_at(1, 64, 1)"));
        Assert.Equal("none", await ShowAsync(engine, "l3", "world.light_at(9, 9, 9)"));
    }

    [Fact]
    public async Task Movement_ReturnsArrivalMapAndRecordsActions()
    {
        var (engine, host, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("mv", WithHeader(
            "set arrival to move_goto(120, 65, -40)\nshow arrival.reached\nshow stop_moving()\n" +
            "show look_at(1, 2, 3)\nshow attack(\"zombie\")\nshow use_in_hand()\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(["yes", "yes", "yes", "yes", "yes"], run.LocalOutput);
        Assert.Contains(host.Moves, m => m.StartsWith("look", StringComparison.Ordinal));
        Assert.Contains(host.Moves, m => m.StartsWith("attack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Eat_PicksBestFoodOrSaysNo()
    {
        var (engine, host, _) = NewEngine();
        host.EatResult = null;
        host.Slots =
        [
            new BeaconInvSlot(0, "minecraft:dirt", "Dirt", 64, null),
            new BeaconInvSlot(1, "minecraft:bread", "Bread", 2, null),
            new BeaconInvSlot(2, "minecraft:cooked_beef", "Steak", 1, null),
        ];
        Assert.Equal("yes", await ShowAsync(engine, "e", "eat()"));

        var (engine2, host2, _) = NewEngine();
        host2.EatResult = null;
        host2.Slots = [new BeaconInvSlot(0, "minecraft:dirt", "Dirt", 64, null)];
        Assert.Equal("no", await ShowAsync(engine2, "e2", "eat()"));
    }

    [Fact]
    public async Task Craft_StubsReadThroughHost()
    {
        var (engine, host, _) = NewEngine();
        host.CraftRecipes = ["minecraft:torch", "minecraft:bread"];
        Assert.Equal("2", await ShowAsync(engine, "cr", "len(craft_list())"));
        Assert.Equal("yes", await ShowAsync(engine, "cr2", "craft_one(\"torch\")"));
    }

    [Fact]
    public async Task SeededRandom_ReplaysWithSameSeed()
    {
        static async Task<string> Roll(int seed)
        {
            var host = new ScriptTestHost();
            var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(seed), new FuelBudget());
            engine.Variables = new VariableStore();
            BeaconRunResult run = await engine.RunScriptAsync("r",
                "# beacon 1\nshow random(100)\nshow pick([\"a\", \"b\", \"c\"])\nshow chance(0.5)\n");
            Assert.True(run.Success);
            return string.Join("|", run.LocalOutput);
        }

        Assert.Equal(await Roll(99), await Roll(99));
    }
}

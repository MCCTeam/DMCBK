using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Language power: replace, sort, reverse, unique, assert, finally, value exports, one-shot timers, settings and game maps, and the chat bucket read.
/// </summary>
public sealed class BeaconLanguagePowerTests : IDisposable
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
    public async Task Replace_SwapsAllOccurrences()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("Hello, new friend", await ShowAsync(engine, "r", "replace(\"Hello, old friend\", \"old\", \"new\")"));
    }

    [Fact]
    public async Task Replace_EmptySearch_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("r", WithHeader("show replace(\"abc\", \"\", \"x\")\n"));
        Assert.False(run.Success);
        Assert.Contains("non-empty search", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sort_NumbersAndText_Pure()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("[1, 2, 3]", await ShowAsync(engine, "s", "sort([3, 1, 2])"));
        Assert.Equal("[\"apple\", \"pear\"]", await ShowAsync(engine, "s2", "sort([\"pear\", \"apple\"])"));
    }

    [Fact]
    public async Task Sort_MixedKinds_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("show sort([1, \"a\"])\n"));
        Assert.False(run.Success);
        Assert.Contains("all numbers or all text", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverse_ListAndText()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("[3, 2, 1]", await ShowAsync(engine, "r", "reverse([1, 2, 3])"));
        Assert.Equal("cba", await ShowAsync(engine, "r2", "reverse(\"abc\")"));
    }

    [Fact]
    public async Task Unique_DedupesKeepingOrder()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("[1, 2, 3]", await ShowAsync(engine, "u", "unique([1, 2, 2, 3, 1])"));
    }

    [Fact]
    public async Task Assert_Pass_ReturnsYes()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("yes", await ShowAsync(engine, "a", "assert(1 is 1, \"math works\")"));
    }

    [Fact]
    public async Task Assert_Fail_RaisesCatchable()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("a", WithHeader(
            "try\nassert(1 is 2, \"bot survived\")\ncatch err\nshow err.message\nend try\n"));
        Assert.True(run.Success);
        Assert.Contains("bot survived", Assert.Single(run.LocalOutput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finally_RunsAfterBody()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader(
            "set journal to \"start\"\ntry\nset journal to \"try\"\ncatch err\nset journal to \"catch\"\nfinally\nset journal to \"{journal}+finally\"\nend try\nshow journal\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("try+finally", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Finally_RunsAfterCatch()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader(
            "set journal to \"start\"\ntry\nserver \"no slash\"\ncatch err\nset journal to \"catch\"\nfinally\nset journal to \"{journal}+finally\"\nend try\nshow journal\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("catch+finally", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task ExportValue_PublishesReadableMap()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shop", WithHeader(
            "export set shop_name to \"Corner Shop\"\nexport set prices to {apple: 5}\nshow shop_name\n"));
        Assert.True(run.Success);
        Assert.True(engine.Bridge.TryGetValueExport("shop", "prices", out BeaconExportedValue? exported));
        Assert.NotNull(exported);
        BeaconMapValue map = Assert.IsType<BeaconMapValue>(exported!.Value);
        Assert.Equal(5, ((BeaconNumberValue)map.Entries["apple"]).Value);
    }

    [Fact]
    public async Task ExportValue_ReadableViaCall()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult first = await engine.RunScriptAsync("shop", WithHeader("export set coins to 7\n"));
        Assert.True(first.Success);
        BeaconRunResult second = await engine.RunScriptAsync("reader", WithHeader("show call \"shop.coins\"()\n"));
        Assert.True(second.Success, second.Error?.Message ?? "call failed");
        Assert.Equal("7", Assert.Single(second.LocalOutput));
    }

    [Fact]
    public async Task ExportValue_ReadableFromHost()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult first = await engine.RunScriptAsync("shop", WithHeader("export set coins to 7\n"));
        Assert.True(first.Success);
        object? value = await engine.CallExportFromHostAsync("shop", "coins", []);
        Assert.Equal(7.0, value);
    }

    [Fact]
    public async Task ExportValue_CallWithArgs_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult first = await engine.RunScriptAsync("shop", WithHeader("export set coins to 7\n"));
        Assert.True(first.Success);
        BeaconRunResult second = await engine.RunScriptAsync("reader", WithHeader("show call \"shop.coins\"(1)\n"));
        Assert.False(second.Success);
        Assert.Contains("as a value", second.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportValue_WithdrawnOnUnload()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult first = await engine.RunScriptAsync("shop", WithHeader("export set coins to 7\n"));
        Assert.True(first.Success);
        Assert.True(engine.RemoveScript("shop"));
        Assert.False(engine.Bridge.TryGetValueExport("shop", "coins", out _));
    }

    [Fact]
    public async Task Once_FiresOnceAfterDelay()
    {
        var (engine, _, clock) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("o", WithHeader("in 30 seconds do\nshow \"fired\"\nend in\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Empty(run.LocalOutput);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(await engine.TickOnceAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        IReadOnlyList<BeaconOnceRun> fires = await engine.TickOnceAsync();
        BeaconOnceRun fire = Assert.Single(fires);
        Assert.Equal("o", fire.ScriptId);
        Assert.True(fire.Result.Success);
        Assert.Equal("fired", Assert.Single(fire.Result.LocalOutput));
        Assert.Empty(await engine.TickOnceAsync());
    }

    [Fact]
    public async Task Once_CancelsOnReconnect()
    {
        var (engine, _, clock) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("o", WithHeader("in 30 seconds do\nshow \"fired\"\nend in\n"));
        Assert.True(run.Success);
        Assert.Equal(1, engine.GetLifecycle("o")?.PendingOnceCount ?? 0);
        engine.GetLifecycle("o")?.HandleReconnect();
        Assert.Equal(0, engine.GetLifecycle("o")?.PendingOnceCount ?? -1);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(await engine.TickOnceAsync());
    }

    [Fact]
    public async Task Once_ReloadRearms()
    {
        var (engine, _, clock) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("o", WithHeader("in 30 seconds do\nshow \"fired\"\nend in\n"));
        Assert.True(run.Success);
        engine.HandleReload("o");
        clock.Advance(TimeSpan.FromSeconds(31));
        IReadOnlyList<BeaconOnceRun> fires = await engine.TickOnceAsync();
        Assert.Single(fires);
    }

    [Fact]
    public async Task Settings_DefaultsVisibleWithoutFile()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "s", "# beacon 1\n# setting thirst = 5 ; seconds between sips\nshow settings.thirst\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("5", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Settings_HeaderDeclaresSchema()
    {
        const string body = "# beacon 1\n# setting thirst = 5 ; seconds between sips\nshow 1\n";
        BeaconHeaderResult header = BeaconHeader.Parse("s.mcc", body);
        Assert.True(header.Ok);
        BeaconSettingDecl decl = Assert.Single(header.Settings);
        Assert.Equal("thirst", decl.Name);
        Assert.Equal(5, ((BeaconNumberValue)decl.Default).Value);
        Assert.Equal("seconds between sips", decl.Comment);
    }

    [Fact]
    public async Task GameMap_ListsDatasetProtocols()
    {
        var (engine, host, _) = NewEngine();
        host.ProtocolValue = 769;
        string protocols = await ShowAsync(engine, "g", "game.protocols");
        Assert.Contains("47", protocols, StringComparison.Ordinal);
        Assert.Contains("769", protocols, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChatBucket_ReadReportsStatus()
    {
        var (engine, _, _) = NewEngine();
        string status = await ShowAsync(engine, "c", "chat_bucket()");
        Assert.Contains("8", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveGoto_OptionsMap_Reaches()
    {
        var (engine, _, _) = NewEngine();
        string arrival = await ShowAsync(engine, "m", "move_goto(120, 65, -40, {tolerance: 2, sneak: no})");
        Assert.Contains("reached: yes", arrival, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveGoto_BadOption_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "m", WithHeader("show move_goto(1, 2, 3, {frobnicate: 1})\n"));
        Assert.False(run.Success);
        Assert.Contains("frobnicate", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }
}

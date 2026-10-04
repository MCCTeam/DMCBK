using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Inventory and world edges.
/// </summary>
public sealed class InventoryAndWorldEdgeTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Inv_FindMiss_SurfacesNone()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.Slots = [new BeaconInvSlot(0, "minecraft:dirt", "Dirt", 64, null)];
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "i", "inv.find(\"compass\")"));
        Assert.Equal("[]", await ScriptTestHelpers.ShowAsync(engine, "i2", "inv.find_all(\"compass\")"));
        Assert.Equal("no", await ScriptTestHelpers.ShowAsync(engine, "i3", "inv.has(\"compass\")"));
        Assert.Equal("yes", await ScriptTestHelpers.ShowAsync(engine, "i4", "inv.has(\"dirt\", 0)"));
    }

    [Fact]
    public async Task Inv_CountEmpty_SumsZero()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "i", "inv.count(\"compass\")"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "i2", "len(inv.list())"));
    }

    [Fact]
    public async Task Inv_MatcherWrongKind_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "i", "show inv.count(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Inv_SelectNegative_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "i", "show inv.select(0 - 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Inv_SelectBeyondHotbar_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "i",
            "try\nshow inv.select(9999)\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Inv_TakeZero_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "i", "show inv.take(3, 0)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Inv_TakeExactCount_ReachesHost()
    {
        var writesHost = new WritesHostForTake();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(writesHost, clock, new SeededRng(7), new FuelBudget());
        engine.Variables = new VariableStore();
        BeaconRunResult run = await engine.RunScriptAsync("t", ScriptTestHelpers.WithHeader("show inv.take(2, 2)\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("yes", Assert.Single(run.LocalOutput));
        Assert.Equal((2, 2), Assert.Single(writesHost.Takes));
    }

    private sealed class WritesHostForTake : IBeaconHostServices
    {
        public List<(int Slot, int Count)> Takes { get; } = [];
        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> TakeFromContainerAsync(int slot, int count, CancellationToken ct = default)
        {
            Takes.Add((slot, count));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task World_BlockAt_MissIsCatchable()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w",
            "try\nshow world.block_at(0, 64, 0)\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task World_BlockAt_BadCoords_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w", "show world.block_at(0.5, 64, 0)\n");
        Assert.False(run.Success);
    }

    [Fact]
    public async Task World_FindSigns_EmptyNeedle_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w", "show world.find_signs(\"\", 16, 10)\n");
        Assert.False(run.Success);
        Assert.Contains("needle", run.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task World_LookingAt_BadReach_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w", "show world.looking_at(0)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }
}

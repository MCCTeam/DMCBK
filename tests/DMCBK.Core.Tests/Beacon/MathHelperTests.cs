using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Math helpers (min, max, clamp, round, abs, log).
/// </summary>
public sealed class MathHelperTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Min_Max_ArgsAndList()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("1", await ScriptTestHelpers.ShowAsync(engine, "m", "min(3, 1, 2)"));
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "m2", "max(3, 1, 2)"));
        Assert.Equal("1", await ScriptTestHelpers.ShowAsync(engine, "m3", "min([3, 1, 2])"));
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "m4", "max([3, 1, 2])"));
        Assert.Equal("7", await ScriptTestHelpers.ShowAsync(engine, "m5", "min(7)"));
    }

    [Fact]
    public async Task Min_EmptyList_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m", "show min([])\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
        Assert.Contains("at least one number", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Min_MixedKinds_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m", "show min(1, \"a\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Clamp_LowInsideHigh()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("5", await ScriptTestHelpers.ShowAsync(engine, "c", "clamp(5, 0, 10)"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "c2", "clamp(0 - 5, 0, 10)"));
        Assert.Equal("10", await ScriptTestHelpers.ShowAsync(engine, "c3", "clamp(15, 0, 10)"));
    }

    [Fact]
    public async Task Clamp_NonNumber_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "c", "show clamp(\"hi\", 0, 10)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Round_DefaultAndDigits()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "r", "round(2.5)"));
        Assert.Equal("2", await ScriptTestHelpers.ShowAsync(engine, "r2", "round(2.4)"));
        Assert.Equal("2.57", await ScriptTestHelpers.ShowAsync(engine, "r3", "round(2.567, 2)"));
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "r4", "round(2.567, 0)"));
    }

    [Fact]
    public async Task Round_NegativeDigits_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "r", "show round(2.5, 0 - 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Abs_NegativeAndPositive()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "a", "abs(0 - 3)"));
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "a2", "abs(3)"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "a3", "abs(0)"));
    }

    [Fact]
    public async Task Abs_NonNumber_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "a", "show abs(\"hi\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Log_BaseCases()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "l", "log(8, 2)"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "l2", "log(1)"));
        string natural = await ScriptTestHelpers.ShowAsync(engine, "l3", "round(log(10), 3)");
        Assert.Equal("2.303", natural);
    }

    [Fact]
    public async Task Log_NonPositive_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "l", "show log(0)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Log_BaseOne_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "l", "show log(8, 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }
}

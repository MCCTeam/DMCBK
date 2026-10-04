using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Converters, len, and RNG bounds for random, pick, and chance.
/// </summary>
public sealed class ConversionAndRandomTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Len_TextListMap()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "l", "len(\"abc\")"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "l2", "len(\"\")"));
        Assert.Equal("2", await ScriptTestHelpers.ShowAsync(engine, "l3", "len([1, 2])"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "l4", "len([])"));
        Assert.Equal("2", await ScriptTestHelpers.ShowAsync(engine, "l5", "len({a: 1, b: 2})"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "l6", "len({})"));
    }

    [Fact]
    public async Task Len_Number_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "l", "show len(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Len_None_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "l", "show len(none)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Converters_EdgeShapes()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("12", await ScriptTestHelpers.ShowAsync(engine, "c", "number(\" 12 \")"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "c2", "number(\"twelve\")"));
        Assert.Equal("1", await ScriptTestHelpers.ShowAsync(engine, "c3", "number(yes)"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "c4", "number(no)"));
        Assert.Equal("[1, 2]", await ScriptTestHelpers.ShowAsync(engine, "c5", "text([1, 2])"));
        Assert.Equal("yes", await ScriptTestHelpers.ShowAsync(engine, "c6", "yesno(\"x\")"));
        Assert.Equal("no", await ScriptTestHelpers.ShowAsync(engine, "c7", "yesno(none)"));
    }

    [Fact]
    public async Task Random_ZeroOrNegative_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult zero = await ScriptTestHelpers.RunAsync(engine, "r", "show random(0)\n");
        Assert.False(zero.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, zero.Error?.Code);
        BeaconRunResult neg = await ScriptTestHelpers.RunAsync(engine, "r2", "show random(0 - 5)\n");
        Assert.False(neg.Success);
    }

    [Fact]
    public async Task Random_Fraction_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "r", "show random(2.5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Pick_Empty_ReturnsNone()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "p", "pick([])"));
        Assert.Equal("only", await ScriptTestHelpers.ShowAsync(engine, "p2", "pick([\"only\"])"));
    }

    [Fact]
    public async Task Pick_NonList_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "p", "show pick(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Chance_BoundsAlwaysSometimes()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("yes", await ScriptTestHelpers.ShowAsync(engine, "c", "chance(1)"));
        Assert.Equal("no", await ScriptTestHelpers.ShowAsync(engine, "c2", "chance(0)"));
    }

    [Fact]
    public async Task Chance_OutOfRange_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "c", "show chance(2)\n");
        Assert.False(run.Success);
        BeaconRunResult neg = await ScriptTestHelpers.RunAsync(engine, "c2", "show chance(0 - 0.5)\n");
        Assert.False(neg.Success);
    }
}

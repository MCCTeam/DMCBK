using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Text helpers (lower, upper, trim, split, join, slice).
/// </summary>
public sealed class TextHelperTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Lower_Upper_Happy()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("hello", await ScriptTestHelpers.ShowAsync(engine, "t1", "lower(\"HeLLo\")"));
        Assert.Equal("HI", await ScriptTestHelpers.ShowAsync(engine, "t2", "upper(\"hi\")"));
    }

    [Fact]
    public async Task Lower_NonText_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show lower(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
        Assert.Contains("lower", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upper_NonText_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show upper([1])\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Trim_StripsSurroundingWhitespace()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("hi", await ScriptTestHelpers.ShowAsync(engine, "t", "trim(\"  hi  \")"));
        Assert.Equal("hi", await ScriptTestHelpers.ShowAsync(engine, "t2", "trim(\"   hi\")"));
    }

    [Fact]
    public async Task Split_Join_RoundTrip()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("[\"a\", \"b\", \"c\"]", await ScriptTestHelpers.ShowAsync(engine, "s", "split(\"a,b,c\", \",\")"));
        Assert.Equal("a, b", await ScriptTestHelpers.ShowAsync(engine, "s2", "join([\"a\", \"b\"], \", \")"));
        Assert.Equal("a|b|c", await ScriptTestHelpers.ShowAsync(engine, "s3", "join(split(\"a,b,c\", \",\"), \"|\")"));
    }

    [Fact]
    public async Task Split_NonText_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "s", "show split(5, \",\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Join_NonList_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "s", "show join(\"ab\", \",\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Slice_Text_StartEnd()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("el", await ScriptTestHelpers.ShowAsync(engine, "s", "slice(\"hello\", 1, 3)"));
        Assert.Equal("llo", await ScriptTestHelpers.ShowAsync(engine, "s2", "slice(\"hello\", 2)"));
        Assert.Equal("hi", await ScriptTestHelpers.ShowAsync(engine, "s3", "slice(\"hi\", 0, 100)"));
        Assert.Equal(string.Empty, await ScriptTestHelpers.ShowAsync(engine, "s4", "slice(\"hello\", 3, 1)"));
    }

    [Fact]
    public async Task Slice_List_StartEnd()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("[2, 3]", await ScriptTestHelpers.ShowAsync(engine, "s", "slice([1, 2, 3, 4], 1, 3)"));
        Assert.Equal("[3, 4]", await ScriptTestHelpers.ShowAsync(engine, "s2", "slice([1, 2, 3, 4], 2)"));
    }

    [Fact]
    public async Task Slice_NegativeBound_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "s", "show slice(\"hi\", 0 - 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Slice_WrongKind_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "s", "show slice(5, 0)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }
}

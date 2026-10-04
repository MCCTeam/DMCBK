using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// String builtins: index_of, replace_first, trim_start/end, pad_start/end, repeat_str, escape_regex.
/// </summary>
public sealed class StringBuiltinTests
{

    [Fact]
    public async Task IndexOf_HitAndMiss()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("6", await ScriptTestHelpers.ShowAsync(engine, "i", "index_of(\"hello world\", \"world\")"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "i2", "index_of(\"hello\", \"h\")"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "i3", "index_of(\"hello\", \"z\")"));
    }

    [Fact]
    public async Task IndexOf_FromNarrowsSearch()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "i", "index_of(\"hello\", \"l\", 3)"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "i2", "index_of(\"hello\", \"l\", 4)"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "i3", "index_of(\"hi\", \"i\", 10)"));
    }

    [Fact]
    public async Task IndexOf_WrongShape_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult arity = await ScriptTestHelpers.RunAsync(engine, "i", "show index_of(\"hi\")\n");
        Assert.False(arity.Success);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, arity.Error?.Code);

        BeaconRunResult type = await ScriptTestHelpers.RunAsync(engine, "i2", "show index_of(5, \"x\")\n");
        Assert.False(type.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, type.Error?.Code);

        BeaconRunResult fromType = await ScriptTestHelpers.RunAsync(engine, "i3",
            "try\nshow index_of(\"hi\", \"i\", \"x\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(fromType.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(fromType.LocalOutput));
    }

    [Fact]
    public async Task ReplaceFirst_SwapsFirstOnly()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("baa", await ScriptTestHelpers.ShowAsync(engine, "r", "replace_first(\"aaa\", \"a\", \"b\")"));
        Assert.Equal("abc", await ScriptTestHelpers.ShowAsync(engine, "r2", "replace_first(\"abc\", \"z\", \"q\")"));
    }

    [Fact]
    public async Task ReplaceFirst_EmptySearch_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "r", "show replace_first(\"abc\", \"\", \"x\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task TrimStartEnd_StripOneSide()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("hi  ", await ScriptTestHelpers.ShowAsync(engine, "t", "trim_start(\"  hi  \")"));
        Assert.Equal("  hi", await ScriptTestHelpers.ShowAsync(engine, "t2", "trim_end(\"  hi  \")"));
    }

    [Fact]
    public async Task TrimStart_NonText_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show trim_start(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task PadStartEnd_PadsAndKeepsLong()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("   hi", await ScriptTestHelpers.ShowAsync(engine, "p", "pad_start(\"hi\", 5)"));
        Assert.Equal("hi   ", await ScriptTestHelpers.ShowAsync(engine, "p2", "pad_end(\"hi\", 5)"));
        Assert.Equal("00hi", await ScriptTestHelpers.ShowAsync(engine, "p3", "pad_start(\"hi\", 4, \"0\")"));
        Assert.Equal("hi00", await ScriptTestHelpers.ShowAsync(engine, "p4", "pad_end(\"hi\", 4, \"0\")"));
        Assert.Equal("abahi", await ScriptTestHelpers.ShowAsync(engine, "p5", "pad_start(\"hi\", 5, \"ab\")"));
        Assert.Equal("hello", await ScriptTestHelpers.ShowAsync(engine, "p6", "pad_start(\"hello\", 3)"));
    }

    [Fact]
    public async Task Pad_EmptyPad_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "p",
            "try\nshow pad_start(\"hi\", 5, \"\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task RepeatStr_RepeatsAndEmpties()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("ababab", await ScriptTestHelpers.ShowAsync(engine, "r", "repeat_str(\"ab\", 3)"));
        Assert.Equal(string.Empty, await ScriptTestHelpers.ShowAsync(engine, "r2", "repeat_str(\"ab\", 0)"));
        Assert.Equal(string.Empty, await ScriptTestHelpers.ShowAsync(engine, "r3", "repeat_str(\"\", 100)"));
    }

    [Fact]
    public async Task RepeatStr_OverCap_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "r",
            "try\nshow repeat_str(\"ab\", 6000)\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Repeat_LoopKeywordStillParsesAsLoop()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "loop",
            "set n to 0\nrepeat 3 times\nset n to n + 1\nend repeat\nshow n\n");
        Assert.True(run.Success);
        Assert.Equal("3", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public void Repeat_AsCallNowFailsCleanlyAtLint()
    {
        BeaconLintReport report = BeaconLint.LintSource(
            "repeat-call.mcc", ScriptTestHelpers.WithHeader("show repeat(\"ab\", 3)\n"));
        Assert.False(report.Ok);
        Assert.Contains(report.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public async Task EscapeRegex_EscapesMeta()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        string escaped = await ScriptTestHelpers.ShowAsync(engine, "e", "escape_regex(\"a+b(c)\")");
        Assert.Equal(System.Text.RegularExpressions.Regex.Escape("a+b(c)"), escaped);
        Assert.Equal("a", await ScriptTestHelpers.ShowAsync(engine, "e2", "escape_regex(\"a\")"));
    }
}

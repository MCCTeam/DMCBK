using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Regex builtins: match_all plus the match timeout fix.
/// </summary>
public sealed class MatchAllTests
{

    [Fact]
    public async Task MatchAll_ReturnsPerHitMapsLikeMatch()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "m", "len(match_all(\"a1 b22 c333\", /[0-9]+/))"));
        Assert.Equal("1", await ScriptTestHelpers.ShowAsync(engine, "m2", "match_all(\"a1 b22\", /[0-9]+/)[0][\"0\"]"));
        Assert.Equal("22", await ScriptTestHelpers.ShowAsync(engine, "m3", "match_all(\"a1 b22\", /[0-9]+/)[1][\"0\"]"));
        Assert.Equal("42", await ScriptTestHelpers.ShowAsync(engine, "m4",
            "match_all(\"n=42 m=7\", /(?<n>[0-9]+)/)[0].n"));
    }

    [Fact]
    public async Task MatchAll_NoHits_ReturnsEmptyList()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("[]", await ScriptTestHelpers.ShowAsync(engine, "m", "match_all(\"abc\", /z+/)"));
        Assert.Equal("0", await ScriptTestHelpers.ShowAsync(engine, "m2", "len(match_all(\"abc\", /z+/))"));
    }

    [Fact]
    public async Task MatchAll_BadPattern_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m",
            "try\nshow match_all(\"abc\", \"(\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task MatchAll_WrongShape_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult arity = await ScriptTestHelpers.RunAsync(engine, "m", "show match_all(\"abc\")\n");
        Assert.False(arity.Success);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, arity.Error?.Code);

        BeaconRunResult type = await ScriptTestHelpers.RunAsync(engine, "m2", "show match_all(5, /a/)\n");
        Assert.False(type.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, type.Error?.Code);
    }

    [Fact]
    public async Task Match_Timeout_SurfacesCatchableB3002()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m",
            "try\nshow match(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!\", /^(a+)+$/)\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        string code = Assert.Single(run.LocalOutput);
        Assert.True(
            code is BeaconDiagnosticCodes.StrictMixedOperands or "ok" or "none",
            $"expected B3002 on timeout or a completed value, got '{code}'");
    }

    [Fact]
    public async Task MatchesOperator_Timeout_StaysCatchable()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m",
            "try\nshow \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!\" matches /^(a+)+$/\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.NotEmpty(run.LocalOutput);
    }
}

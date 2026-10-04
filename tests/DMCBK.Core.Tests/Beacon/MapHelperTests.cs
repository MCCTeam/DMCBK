using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Map helpers (keys, values, has_key) and match.
/// </summary>
public sealed class MapHelperTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Keys_SortedValues_ByKey()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("[\"a\", \"b\"]", await ScriptTestHelpers.ShowAsync(engine, "k", "keys({b: 2, a: 1})"));
        Assert.Equal("[1, 2]", await ScriptTestHelpers.ShowAsync(engine, "k2", "values({b: 2, a: 1})"));
        Assert.Equal("[]", await ScriptTestHelpers.ShowAsync(engine, "k3", "keys({})"));
    }

    [Fact]
    public async Task Keys_NonMap_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "k", "show keys([1])\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Values_NonMap_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "k", "show values(5)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task HasKey_HitAndMiss()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("yes", await ScriptTestHelpers.ShowAsync(engine, "h", "has_key({a: 1}, \"a\")"));
        Assert.Equal("no", await ScriptTestHelpers.ShowAsync(engine, "h2", "has_key({a: 1}, \"b\")"));
        Assert.Equal("no", await ScriptTestHelpers.ShowAsync(engine, "h3", "has_key({}, \"a\")"));
    }

    [Fact]
    public async Task HasKey_WrongShape_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "h", "show has_key([1], \"a\")\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, run.Error?.Code);
    }

    [Fact]
    public async Task Match_NumberedGroups()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("123", await ScriptTestHelpers.ShowAsync(engine, "m", "match(\"abc123\", /[0-9]+/)[\"0\"]"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "m2", "match(\"abc\", /z+/)"));
    }

    [Fact]
    public async Task Match_NamedGroup()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("42", await ScriptTestHelpers.ShowAsync(engine, "m", "match(\"n=42\", /n=(?<n>[0-9]+)/).n"));
    }

    [Fact]
    public async Task Match_BadPattern_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m",
            "try\nshow match(\"abc\", \"(\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, Assert.Single(run.LocalOutput));
    }
}

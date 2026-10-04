using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Manifest, throttle, jails, and movement edges plus formatter idempotency.
/// </summary>
public sealed class SandboxEdgeTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Manifest_NeedsMismatch_RefusesWithPasteLine()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("m", "# beacon 1\n# needs: chat.send\nsay \"hi\"\nserver \"/home\"\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.ManifestNeedsMismatch, run.Error?.Code);
        Assert.Contains("# needs:", run.Error?.Suggestion ?? run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRead_WithoutJail_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "f",
            "try\nshow file_read(\"notes.txt\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.FileJail, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task HttpGet_WithoutGate_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "h",
            "try\nshow http_get(\"https://example.com/x\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.NetGate, Assert.Single(run.LocalOutput));
    }

    [Fact]
    public void ChatBucket_WithoutBucket_SurfacesNoneAvailable()
    {
        var host = new ScriptTestHost();
        var interp = new BeaconInterpreter("c", "c.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget());
        Assert.Null(interp.ChatBucket);
    }

    [Fact]
    public async Task MoveFollow_ReachesHost()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "m", "show move_follow(\"Steve\")\nshow stop_moving()\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(2, run.LocalOutput.Count);
        Assert.Contains("reached", run.LocalOutput[0], StringComparison.Ordinal);
        Assert.Equal("yes", run.LocalOutput[1]);
    }

    [Fact]
    public async Task Interpolation_BraceEscapes()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("{hi}", await ScriptTestHelpers.ShowAsync(engine, "b", "\"{{hi}}\""));
    }

    [Fact]
    public async Task TripleText_Multiline()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show \"\"\"line1\nline2\"\"\"\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Contains("line1", Assert.Single(run.LocalOutput));
        Assert.Contains("line2", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Json_DeepNesting_RefusesCatchably()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        string deep = "[" + new string('[', 40) + new string(']', 40) + "]";
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "j",
            $"try\nshow json_parse(\"{deep.Replace("\"", "\\\"")}\")\ncatch err\nshow err.code\nend try\n");
        Assert.True(run.Success);
        Assert.NotEmpty(run.LocalOutput);
    }

    [Fact]
    public void Format_Idempotent_OnNewConstructs()
    {
        const string source = "# beacon 1\nshow lower(\"HI\")\nshow clamp(5, 0, 10)\nshow has_key({a: 1}, \"a\")\n";
        BeaconFormatResult first = BeaconFormat.FormatSource("g.bcn", source);
        BeaconFormatResult second = BeaconFormat.FormatSource("g.bcn", first.Formatted);
        Assert.Equal(first.Formatted, second.Formatted);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Format_CrlfUnicode_NormalizesAndKeepsText()
    {
        string source = "# beacon 1\r\nshow \"caf\u00E9  \"  \r\n";
        BeaconFormatResult result = BeaconFormat.FormatSource("u.bcn", source);
        Assert.Contains("caf\u00E9", result.Formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result.Formatted, StringComparison.Ordinal);
        Assert.EndsWith("\n", result.Formatted, StringComparison.Ordinal);
    }
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Error goldens: every diagnostic renders the one Elm-style shape (<c>file:line:col</c> plus source excerpt plus caret plus expectation plus one paste-ready fix plus did-you-mean), budget aborts share that shape, the advanced trace flag is the only place spans jargon appears, and catch values carry code plus line plus message.
/// </summary>
public sealed class ErrorTests
{
    private sealed class FailingHost : IBeaconHostServices
    {
        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
            => Task.FromException<string>(new InvalidOperationException("no such command"));
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static BeaconEngine NewEngine()
    {
        return new BeaconEngine(
            new FailingHost(), new VirtualClock(), new SeededRng(17), new FuelBudget(400));
    }

    private static void AssertElmShape(
        BeaconDiagnostic diagnostic, string source, string mustContainFix)
    {
        string rendered = BeaconErrorRenderer.Render(diagnostic, source);

        string[] lines = rendered.Split('\n');
        Assert.Matches(@"^[\w.\-]+\.bcn:\d+:\d+$", lines[0]);
        Assert.Contains(source.Split('\n')[diagnostic.Span.Origin.Line - 1].Trim(), rendered, StringComparison.Ordinal);
        Assert.Contains("^", rendered, StringComparison.Ordinal);
        Assert.Contains("Try this:", rendered, StringComparison.Ordinal);
        Assert.Contains(mustContainFix, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Golden_Misspelling_HasDidYouMeanAndFix()
    {
        const string source = "# beacon 1\nshow healt\n";
        var engine = NewEngine();
        engine.LoadSource("typo", "typo.bcn", source);
        BeaconRunResult result = await engine.RunTopLevelAsync("typo").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, result.Error!.Code);
        Assert.Contains("healt", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'health'", result.Error.Suggestion, StringComparison.Ordinal);
        AssertElmShape(result.Error, source, "health");
    }

    [Fact]
    public void Golden_EqualsForAssignment_ShowsSetToFix()
    {
        const string source = "# beacon 1\nset x = 5\n";
        var engine = NewEngine();
        engine.LoadSource("eq", "eq.bcn", source);
        BeaconDiagnostic diagnostic = Assert.Single(
            engine.Lint("eq"), d => string.Equals(d.Code, BeaconDiagnosticCodes.StrictEquals, StringComparison.Ordinal));

        AssertElmShape(diagnostic, source, "set x to 5");
        string rendered = BeaconErrorRenderer.Render(diagnostic, source);
        Assert.Contains("=", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Golden_IfCount_SuggestsComparison()
    {
        const string source = "# beacon 1\nset count to 3\nif count\nshow \"x\"\nend if\n";
        var engine = NewEngine();
        engine.LoadSource("cond", "cond.bcn", source);
        BeaconDiagnostic diagnostic = Assert.Single(
            engine.Lint("cond"),
            d => string.Equals(d.Code, BeaconDiagnosticCodes.StrictBooleanCondition, StringComparison.Ordinal));

        Assert.Contains("if count > 0", diagnostic.Message, StringComparison.Ordinal);
        AssertElmShape(diagnostic, source, "if count > 0");
    }

    [Fact]
    public async Task Golden_BudgetAbort_SharesElmShape()
    {
        const string source = "# beacon 1\nwhile yes\nshow \"spin\"\nend while\n";
        var engine = NewEngine();
        engine.LoadSource("spin", "spin.bcn", source);
        BeaconRunResult result = await engine.RunTopLevelAsync("spin").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, result.Error!.Code);
        AssertElmShape(result.Error, source, "every");
    }

    [Fact]
    public void Renderer_FirstLine_IsFileLineCol()
    {
        var diagnostic = new BeaconDiagnostic(
            BeaconDiagnosticCodes.StrictEquals, BeaconSeverity.Error,
            "I expected something.",
            new SourceSpan("quiz.bcn", 4, 7, 1),
            "Write it differently.");

        string rendered = BeaconErrorRenderer.Render(diagnostic, "a\nb\nc\nset x = 5\n");

        Assert.StartsWith("quiz.bcn:4:7", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_WithoutSource_OmitsExcerptAndCaret()
    {
        var diagnostic = new BeaconDiagnostic(
            BeaconDiagnosticCodes.StrictEquals, BeaconSeverity.Error,
            "I expected something.",
            new SourceSpan("quiz.bcn", 4, 7, 1),
            "Write it differently.");

        string rendered = BeaconErrorRenderer.Render(diagnostic, null);

        Assert.StartsWith("quiz.bcn:4:7", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("^", rendered, StringComparison.Ordinal);
        Assert.Contains("Try this:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_TraceFlag_ExposesSpans_DefaultHides()
    {
        var diagnostic = new BeaconDiagnostic(
            BeaconDiagnosticCodes.UnknownName, BeaconSeverity.Error,
            "Unknown name 'foo'.",
            new SourceSpan("a.bcn", 2, 3, 3),
            "Did you mean 'food'?");

        string plain = BeaconErrorRenderer.Render(diagnostic, "# beacon 1\nshow foo\n");
        Assert.DoesNotContain("span", plain, StringComparison.OrdinalIgnoreCase);

        string traced = BeaconErrorRenderer.Render(diagnostic, "# beacon 1\nshow foo\n", includeTrace: true);
        Assert.Contains("span", traced, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BeaconDiagnosticCodes.UnknownName, traced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchValue_CarriesCodeLineMessage()
    {
        var engine = NewEngine();
        engine.LoadSource("catchy", "catchy.bcn",
            "# beacon 1\ntry\nset x to mcc \"/bad\"\ncatch err\nshow err.code\nshow err.line\nshow err.message\nend try\n");
        BeaconRunResult result = await engine.RunTopLevelAsync("catchy").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Equal(3, result.LocalOutput.Count);
        Assert.Equal("B4001", result.LocalOutput[0]);
        Assert.Equal("3", result.LocalOutput[1]);
        Assert.Contains("no such command", result.LocalOutput[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BudgetAbort_NamesHandlerLocalsStackAndSeed()
    {
        const string source = "# beacon 1\nset quota to 3\nwhile yes\nshow quota\nend while\n";
        var engine = NewEngine();
        engine.LoadSource("ctx", "ctx.bcn", source);
        BeaconRunResult result = await engine.RunTopLevelAsync("ctx").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("top-level", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("quota", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("seed", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Golden_ServerSlash_ShowsServerFix()
    {
        const string source = "# beacon 1\nserver \"home\"\n";
        var engine = new BeaconEngine(
            new FailingHost(), new VirtualClock(), new SeededRng(17), new FuelBudget());
        engine.LoadSource("slash", "slash.bcn", source);
        BeaconRunResult result = await engine.RunTopLevelAsync("slash").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.StrictLeadingSlash, result.Error!.Code);
        AssertElmShape(result.Error, source, "server \"/home\"");
    }
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Strictness goldens: every trap rejects with a paste-ready fix.
/// Mixed +, number("twelve"), if-food/items/warns-or-0, bare shortcuts incl. no bare hunger.
/// </summary>
public sealed class StrictnessTests
{
    private sealed class RecordingHost : IBeaconHostServices
    {
        public string? SelfNameValue { get; set; } = "Tester";
        public List<string> OnlinePlayersValue { get; set; } = ["Alice", "Bob"];
        public double? ServerTpsValue { get; set; } = 20.0;
        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => SelfNameValue;
        public IReadOnlyList<string> OnlinePlayers(int limit) => OnlinePlayersValue.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static BeaconEngine NewEngine(int seed = 11)
        => new(new RecordingHost(), new VirtualClock(), new SeededRng(seed), new FuelBudget());

    private static IReadOnlyList<BeaconDiagnostic> LintErrors(string body)
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\n" + body);
        return engine.Lint("probe").Where(d => d.Severity == BeaconSeverity.Error).ToList();
    }

    private static async Task<BeaconRunResult> RunCleanAsync(string body, int seed = 11)
    {
        var engine = NewEngine(seed);
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\n" + body);
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);
        return await engine.RunTopLevelAsync("probe");
    }

    [Fact]
    public async Task MixedPlus_SuggestsInterpolationAndText()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\nset health to 20\nshow \"health: \" + health\n");
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);

        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.StrictMixedOperands, result.Error!.Code);
        Assert.Contains("interpolation", result.Error.Message + result.Error.Suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text()", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plus_SameKinds_Allowed()
    {
        BeaconRunResult numbers = await RunCleanAsync("show 1 + 2\n");
        Assert.True(numbers.Success);
        Assert.Contains("3", numbers.LocalOutput);

        BeaconRunResult texts = await RunCleanAsync("show \"a\" + \"b\"\n");
        Assert.True(texts.Success);
        Assert.Contains("ab", texts.LocalOutput);
    }

    [Fact]
    public async Task NumberTwelve_IsNone_AndIsSet()
    {
        BeaconRunResult result = await RunCleanAsync(
            "set x to number(\"twelve\")\nif x is set then\nshow \"set\"\nelse\nshow \"not set\"\nend if\nshow number(\"12\")\n");
        Assert.True(result.Success);
        Assert.Equal(["not set", "12"], result.LocalOutput);
    }

    [Fact]
    public void IfFood_Rejects_WithGreaterThanSuggestion()
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("if food\nsay \"a\"\nend if\n");
        BeaconDiagnostic diagnostic = Assert.Single(errors, d => d.Code == BeaconDiagnosticCodes.StrictBooleanCondition);
        Assert.Contains("if food > 0", diagnostic.Message + diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void IfItems_Rejects_WithIsNotEmptySuggestion()
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("if items\nsay \"a\"\nend if\n");
        BeaconDiagnostic diagnostic = Assert.Single(errors, d => d.Code == BeaconDiagnosticCodes.StrictBooleanCondition);
        Assert.Contains("is not empty", diagnostic.Message + diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void IfWarnsOrZero_Rejected_VsParenAccepted()
    {
        IReadOnlyList<BeaconDiagnostic> rejected = LintErrors("if warns[player] or 0\nsay \"a\"\nend if\n");
        Assert.Contains(rejected, d => d.Code == BeaconDiagnosticCodes.StrictBooleanCondition);

        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\nset warns to {}\nset warns[\"p\"] to 5\nif (warns[\"p\"] or 0) > 2 then\nshow \"big\"\nend if\n");
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public async Task Runtime_OrCondition_RejectsWithOperandNote()
    {
        // Passes static check via call indirection, fails at runtime with the or-operand note.
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn",
            "# beacon 1\nfunction getv()\nreturn warns[\"p\"] or 0\nend function\nset warns to {}\nif getv() then\nshow \"x\"\nend if\n");
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictBooleanCondition, result.Error!.Code);
        Assert.Contains("> 0", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareShortcut_EventFieldsWinInsideHandler()
    {
        var host = new RecordingHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(5), new FuelBudget());
        engine.LoadSource("s", "s.bcn", "# beacon 1\nset player to \"Global\"\non join:\nshow player\nend on\n");
        Assert.DoesNotContain(engine.Lint("s"), d => d.Severity == BeaconSeverity.Error);
        Assert.True((await engine.RunTopLevelAsync("s")).Success);

        BeaconRunResult handler = await engine.InvokeHandlerAsync(
            "s", "join", new Dictionary<string, BeaconValue> { ["player"] = BeaconValue.Text("Event") });
        Assert.True(handler.Success);
        Assert.Equal(["Event"], handler.LocalOutput);
    }

    [Fact]
    public async Task BareShortcut_Twins_OutsideHandler()
    {
        var host = new RecordingHost { ServerTpsValue = 19.5, OnlinePlayersValue = ["A", "B", "C"] };
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(5), new FuelBudget());
        engine.LoadSource("s", "s.bcn", "# beacon 1\nshow tps\nshow online_count\nshow me.name\nshow \"{server.tps}\"\n");
        Assert.DoesNotContain(engine.Lint("s"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult result = await engine.RunTopLevelAsync("s");
        Assert.True(result.Success);
        Assert.Equal(["19.5", "3", "Tester", "19.5"], result.LocalOutput);
    }

    [Fact]
    public async Task NoBareHunger_ErrorsSuggestingFood()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\nshow hunger\n");
        // hunger is not a static failure (unknown names are runtime); execution must reject.
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, result.Error!.Code);
        Assert.Contains("food", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("hunger", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConservativeFalsiness_ZeroEmptyListTruthy_ForOr()
    {
        BeaconRunResult result = await RunCleanAsync(
            "set x to 0 or 5\nshow x\nset y to \"\" or \"d\"\nshow y\nset z to [] or [1]\nshow z\n" +
            "set a to no or 5\nshow a\nset b to none or 5\nshow b\n");
        Assert.True(result.Success);
        Assert.Equal(["0", "", "[]", "5", "5"], result.LocalOutput);
    }

    [Fact]
    public async Task Converters_TextNumberYesno()
    {
        BeaconRunResult result = await RunCleanAsync(
            "show text(5)\nshow text(yes)\nshow number(\"12\")\nshow yesno(0)\nshow yesno(no)\nshow yesno(none)\n");
        Assert.True(result.Success);
        Assert.Equal(["5", "yes", "12", "yes", "no", "no"], result.LocalOutput);
    }

    [Fact]
    public async Task Runtime_NonYesNoCondition_SuggestsFix()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn",
            "# beacon 1\nfunction getfood()\nreturn 5\nend function\nif getfood() then\nshow \"x\"\nend if\n");
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictBooleanCondition, result.Error!.Code);
        Assert.Contains("> 0", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_ListCondition_SuggestsIsNotEmpty()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn",
            "# beacon 1\nfunction getitems()\nreturn [1]\nend function\nif getitems() then\nshow \"x\"\nend if\n");
        Assert.DoesNotContain(engine.Lint("probe"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.StrictBooleanCondition, result.Error!.Code);
        Assert.Contains("is not empty", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownName_CaseMismatch_SuggestsRightOne()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\nset player to \"Steve\"\nshow Player\n");
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, result.Error!.Code);
        Assert.Contains("player", result.Error.Message + result.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArityMismatch_NamesFunctionExpectedGot()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn",
            "# beacon 1\nfunction greet(name)\nreturn \"hi\"\nend function\nset x to greet(\"a\", \"b\")\n");
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, result.Error!.Code);
        Assert.Contains("greet", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("1", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("2", result.Error.Message, StringComparison.Ordinal);
    }
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Interpreter core: scopes, calls, recursion, two-script isolation, all control-flow shapes, functions including nested calls, plus documented snippets.
/// Executes on fakes with zero network.
/// </summary>
public sealed class InterpreterTests
{
    internal sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
        public List<string> Servers { get; } = [];
        public List<string> Mccs { get; } = [];
        public Func<string, string> MccHandler { get; set; } = _ => string.Empty;
        public string? SelfNameValue { get; set; } = "Tester";
        public List<string> OnlinePlayersValue { get; set; } = ["Alice", "Bob"];
        public double? ServerTpsValue { get; set; } = 20.0;

        public Task SayAsync(string text, CancellationToken ct = default)
        {
            Says.Add(text);
            return Task.CompletedTask;
        }

        public Task WhisperAsync(string player, string text, CancellationToken ct = default)
        {
            Whispers.Add((player, text));
            return Task.CompletedTask;
        }

        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
        {
            Servers.Add(commandLine);
            return Task.FromResult(string.Empty);
        }

        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
        {
            Mccs.Add(commandLine);
            return Task.FromResult(MccHandler(commandLine));
        }

        public string? SelfName => SelfNameValue;
        public IReadOnlyList<string> OnlinePlayers(int limit) => OnlinePlayersValue.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static (BeaconEngine Engine, RecordingHost Host, VirtualClock Clock) NewEngine(
        int seed = 42, long fuelLimit = 100_000)
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var rng = new SeededRng(seed);
        var fuel = new FuelBudget(fuelLimit);
        return (new BeaconEngine(host, clock, rng, fuel), host, clock);
    }

    private static async Task<BeaconRunResult> RunCleanAsync(
        BeaconEngine engine, string scriptId, string body)
    {
        engine.LoadSource(scriptId, scriptId + ".bcn", "# beacon 1\n" + body);
        IReadOnlyList<BeaconDiagnostic> errors = engine.Lint(scriptId)
            .Where(d => d.Severity == BeaconSeverity.Error).ToList();
        Assert.Empty(errors);
        return await engine.RunTopLevelAsync(scriptId);
    }

    [Fact]
    public async Task TopLevel_Set_And_ShowInterpolates()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t1", "set name to \"Steve\"\nshow \"hi {name}\"\n");

        Assert.True(result.Success);
        Assert.Contains("hi Steve", result.LocalOutput);
    }

    [Fact]
    public async Task Function_Call_ReturnsValue()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t2",
            "function greet(name)\nreturn \"Hello, {name}!\"\nend function\nset x to greet(\"Steve\")\nshow x\n");

        Assert.True(result.Success);
        Assert.Contains("Hello, Steve!", result.LocalOutput);
    }

    [Fact]
    public async Task NestedCalls_GreetPickOnlinePlayers()
    {
        var (engine, host, _) = NewEngine();
        host.OnlinePlayersValue = ["OnlyOne"];
        BeaconRunResult result = await RunCleanAsync(engine, "t3",
            "function greet(name)\nreturn \"Hello, {name}!\"\nend function\nsay greet(pick(online_players))\n");

        Assert.True(result.Success);
        Assert.Single(host.Says);
        Assert.Equal("Hello, OnlyOne!", host.Says[0]);
    }

    [Fact]
    public async Task Recursion_Factorial()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t4",
            "function fact(n)\nif n <= 1 then\nreturn 1\nelse\nreturn n * fact(n - 1)\nend if\nend function\nset x to fact(5)\nshow x\n");

        Assert.True(result.Success);
        Assert.Contains("120", result.LocalOutput);
    }

    [Fact]
    public async Task TwoScript_Isolation()
    {
        var host = new RecordingHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(7), new FuelBudget());
        engine.LoadSource("a", "a.bcn", "# beacon 1\nset x to 1\nshow x\n");
        engine.LoadSource("b", "b.bcn", "# beacon 1\nset x to 2\nshow x\n");

        BeaconRunResult ra = await engine.RunTopLevelAsync("a");
        BeaconRunResult rb = await engine.RunTopLevelAsync("b");

        Assert.True(ra.Success);
        Assert.True(rb.Success);
        Assert.Contains("1", ra.LocalOutput);
        Assert.DoesNotContain("2", ra.LocalOutput);
        Assert.Contains("2", rb.LocalOutput);
        Assert.DoesNotContain("1", rb.LocalOutput);

        IReadOnlyDictionary<string, BeaconValue> ga = engine.GetGlobalsSnapshot("a");
        IReadOnlyDictionary<string, BeaconValue> gb = engine.GetGlobalsSnapshot("b");
        Assert.Equal("1", ((BeaconNumberValue)ga["x"]).Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2", ((BeaconNumberValue)gb["x"]).Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Function_Locals_DoNotLeakToGlobals()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t5",
            "set x to 10\nfunction f()\nset x to 99\nreturn x\nend function\nset y to f()\nshow y\nshow x\n");

        Assert.True(result.Success);
        Assert.Contains("99", result.LocalOutput);
        // Global x stays 10: second show outputs 10, not 99.
        Assert.Equal(2, result.LocalOutput.Count);
        Assert.Equal("99", result.LocalOutput[0]);
        Assert.Equal("10", result.LocalOutput[1]);
    }

    [Fact]
    public async Task If_ElseIf_Else_NearestBind()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t6",
            "set a to 1\nset b to 3\nif a is 1 then\nif b is 2 then\nshow \"inner\"\nelse\nshow \"inner-else\"\nend if\nelse\nshow \"outer-else\"\nend if\n");

        Assert.True(result.Success);
        Assert.Single(result.LocalOutput);
        Assert.Equal("inner-else", result.LocalOutput[0]);
    }

    [Fact]
    public async Task Repeat_Counts()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t7",
            "set total to 0\nrepeat 3 times\nset total to total + 1\nend repeat\nshow total\n");

        Assert.True(result.Success);
        Assert.Contains("3", result.LocalOutput);
    }

    [Fact]
    public async Task Repeat_Zero_SkipsBody()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t7z",
            "set total to 0\nrepeat 0 times\nset total to total + 100\nend repeat\nshow total\n");

        Assert.True(result.Success);
        Assert.Contains("0", result.LocalOutput);
    }

    [Fact]
    public async Task While_Guards()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t8",
            "set i to 0\nwhile i < 3\nset i to i + 1\nend while\nshow i\n");

        Assert.True(result.Success);
        Assert.Contains("3", result.LocalOutput);
    }

    [Fact]
    public async Task ForEach_List()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t9",
            "set total to 0\nfor each x in [1, 2, 3]\nset total to total + x\nend for\nshow total\n");

        Assert.True(result.Success);
        Assert.Contains("6", result.LocalOutput);
    }

    [Fact]
    public async Task ForEach_MapKeys()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t10",
            "set m to {a: 1, b: 2}\nfor each k in m\nshow k\nend for\n");

        Assert.True(result.Success);
        Assert.Equal(2, result.LocalOutput.Count);
        Assert.Contains("a", result.LocalOutput);
        Assert.Contains("b", result.LocalOutput);
    }

    [Fact]
    public async Task Stop_BreaksLoop()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t11",
            "set i to 0\nwhile yes\nset i to i + 1\nif i is 3 then\nstop\nend if\nend while\nshow i\n");

        Assert.True(result.Success);
        Assert.Contains("3", result.LocalOutput);
    }

    [Fact]
    public async Task Skip_ContinuesLoop()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t12",
            "set total to 0\nfor each x in [1, 2, 3, 4]\nif x is 2 then\nskip\nend if\nset total to total + x\nend for\nshow total\n");

        Assert.True(result.Success);
        Assert.Contains("8", result.LocalOutput);
    }

    [Fact]
    public async Task BreakContinue_Aliases_Work()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult r1 = await RunCleanAsync(engine, "t13a",
            "set i to 0\nwhile yes\nset i to i + 1\nif i is 2 then\nbreak\nend if\nend while\nshow i\n");
        Assert.True(r1.Success);
        Assert.Contains("2", r1.LocalOutput);

        var (engine2, _, _) = NewEngine();
        BeaconRunResult r2 = await RunCleanAsync(engine2, "t13b",
            "set total to 0\nfor each x in [1, 2, 3]\nif x is 2 then\ncontinue\nend if\nset total to total + x\nend for\nshow total\n");
        Assert.True(r2.Success);
        Assert.Contains("4", r2.LocalOutput);
    }

    [Fact]
    public async Task Wait_Yields_With100msFloor()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(9), new FuelBudget());
        engine.LoadSource("w", "w.bcn", "# beacon 1\nwait 0 seconds\nshow \"done\"\n");
        Assert.DoesNotContain(engine.Lint("w"), d => d.Severity == BeaconSeverity.Error);

        Task<BeaconRunResult> task = engine.RunTopLevelAsync("w");
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(20);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        BeaconRunResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Contains("done", result.LocalOutput);
    }

    [Fact]
    public async Task TryCatch_ErrMessage_Continues()
    {
        var (engine, host, _) = NewEngine();
        host.MccHandler = _ => throw new InvalidOperationException("kaboom");
        BeaconRunResult result = await RunCleanAsync(engine, "t14",
            "try\nset x to mcc \"/bad\"\ncatch err\nshow err.message\nend try\nshow \"after\"\n");

        Assert.True(result.Success);
        Assert.Equal(2, result.LocalOutput.Count);
        Assert.Contains("kaboom", result.LocalOutput[0]);
        Assert.Equal("after", result.LocalOutput[1]);
    }

    [Fact]
    public async Task Return_Value_EarlyExit()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "t15",
            "function f(x)\nif x is 1 then\nreturn \"one\"\nelse\nreturn \"other\"\nend if\nend function\nset a to f(1)\nset b to f(2)\nshow \"{a} {b}\"\n");

        Assert.True(result.Success);
        Assert.Contains("one other", result.LocalOutput);
    }

    [Fact]
    public async Task WelcomeBot_Core_ExecutesOnFakes()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "on join:\nsay \"Welcome to the server, {player}!\"\nend on\n" +
            "on chat when message contains \"!rules\":\nwhisper player \"1. Be kind. 2. No griefing.\"\nend on\n";
        engine.LoadSource("welcome", "welcome.bcn", "# beacon 1\n# needs: chat.send\n" + body);
        Assert.DoesNotContain(engine.Lint("welcome"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult top = await engine.RunTopLevelAsync("welcome");
        Assert.True(top.Success);

        BeaconRunResult join = await engine.InvokeHandlerAsync(
            "welcome", "join", new Dictionary<string, BeaconValue> { ["player"] = BeaconValue.Text("Steve") });
        Assert.True(join.Success);
        Assert.Single(host.Says);
        Assert.Equal("Welcome to the server, Steve!", host.Says[0]);

        BeaconRunResult chat = await engine.InvokeHandlerAsync(
            "welcome", "chat", new Dictionary<string, BeaconValue>
            {
                ["player"] = BeaconValue.Text("Alex"),
                ["message"] = BeaconValue.Text("hey !rules please"),
            });
        Assert.True(chat.Success);
        Assert.Single(host.Whispers);
        Assert.Equal("Alex", host.Whispers[0].Player);
    }

    [Fact]
    public async Task Proposal_Greeter_RemembersAcrossDispatches()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "on join:\nset seen to saved(\"seen\") or {}\nif seen[player] is set\nsay \"Welcome back, {player}!\"\nset seen[player] to seen[player] + 1\nelse\nsay \"Welcome for the first time, {player}!\"\nset seen[player] to 1\nend if\nsave \"seen\" to seen\nend on\n";
        engine.LoadSource("greeter", "greeter.bcn", "# beacon 1\n" + body);
        Assert.DoesNotContain(engine.Lint("greeter"), d => d.Severity == BeaconSeverity.Error);
        Assert.True((await engine.RunTopLevelAsync("greeter")).Success);

        var fields = new Dictionary<string, BeaconValue> { ["player"] = BeaconValue.Text("Steve") };
        BeaconRunResult first = await engine.InvokeHandlerAsync("greeter", "join", fields);
        Assert.True(first.Success);
        Assert.Contains("first time", host.Says[^1]);

        BeaconRunResult second = await engine.InvokeHandlerAsync("greeter", "join", fields);
        Assert.True(second.Success);
        Assert.Contains("back", host.Says[^1]);
    }

    [Fact]
    public async Task Proposal_Quiz_StateMachine()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "set quiz to {running: no, q: \"\", a: \"\", wins: {}}\n" +
            "on chat when message is \"!quiz\":\nif quiz.running is yes\nwhisper player \"running\"\nstop event\nend if\nset quiz.running to yes\nset quiz.q to \"What mob explodes?\"\nset quiz.a to \"creeper\"\nsay \"Quiz! {quiz.q}\"\nend on\n";
        engine.LoadSource("quiz", "quiz.bcn", "# beacon 1\n" + body);
        Assert.DoesNotContain(engine.Lint("quiz"), d => d.Severity == BeaconSeverity.Error);
        Assert.True((await engine.RunTopLevelAsync("quiz")).Success);

        BeaconRunResult start = await engine.InvokeHandlerAsync("quiz", "chat", new Dictionary<string, BeaconValue>
        {
            ["player"] = BeaconValue.Text("Alex"),
            ["message"] = BeaconValue.Text("!quiz"),
        });
        Assert.True(start.Success);
        Assert.Contains(host.Says, s => s.Contains("Quiz!"));
    }

    [Fact]
    public async Task NestedDecl_NeverClean_ThrowsAtEvaluation()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("probe.bcn", "# beacon 1\nset x to 1\nif x is 1\non join\nsay \"hi\"\nend on\nend if\n");
        BeaconParseResult parsed = BeaconParser.Parse("probe.bcn", lexed.Tokens);
        Assert.NotNull(parsed.Script);
        // Lint fails first.
        var probe = new BeaconEngine(new RecordingHost());
        probe.LoadSource("probe", "probe.bcn", "# beacon 1\nset x to 1\nif x is 1\non join\nsay \"hi\"\nend on\nend if\n");
        Assert.Contains(probe.Lint("probe"), d => d.Severity == BeaconSeverity.Error);

        var interp = new BeaconInterpreter("probe", "probe.bcn", new RecordingHost());
        BeaconRunResult result = await interp.RunTopLevelAsync(parsed.Script!);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.Parse, result.Error!.Code);
    }

    [Fact]
    public async Task Comparison_All16Ops_Evaluate()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "cmp",
            "show (1 is 1)\nshow (1 is not 2)\nshow (1 == 1)\nshow (1 != 2)\n" +
            "show (1 < 2)\nshow (2 > 1)\nshow (1 <= 1)\nshow (2 >= 2)\n" +
            "show (\"abc\" contains \"b\")\nshow (\"abc\" starts with \"a\")\nshow (\"abc\" ends with \"c\")\n" +
            "show ([] is empty)\nshow ([1] is not empty)\nshow (none is not set)\nshow (1 is set)\n");

        Assert.True(result.Success);
        Assert.Equal(15, result.LocalOutput.Count);
        Assert.All(result.LocalOutput, line => Assert.Equal("yes", line));
    }

    [Fact]
    public async Task MatchesOp_EvaluatesRegex()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "rx",
            "on server_message when text matches /a+/\nsay \"hit\"\nend on\nshow (\"aaab\" matches /a+/)\n");

        Assert.True(result.Success);
        Assert.Contains("yes", result.LocalOutput);
    }

    [Fact]
    public async Task MccExpr_PostfixArg_WorksLikeCall()
    {
        var (engine, host, _) = NewEngine();
        host.MccHandler = cmd => "out:" + cmd;
        BeaconRunResult result = await RunCleanAsync(engine, "mccx",
            "set a to mcc \"/list\"\nset b to mcc(\"/list\")\nshow \"{a}|{b}\"\n");

        Assert.True(result.Success);
        Assert.Contains("out:/list|out:/list", result.LocalOutput);
        Assert.Equal(2, host.Mccs.Count);
    }

    [Fact]
    public async Task ParenExpr_Transparent()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "paren",
            "set warns to {}\nset warns[\"p\"] to 5\nif (warns[\"p\"] or 0) > 2 then\nshow \"big\"\nend if\n");

        Assert.True(result.Success);
        Assert.Contains("big", result.LocalOutput);
    }

    [Fact]
    public async Task OrAnd_ReturnOperandsUncoerced()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "orand",
            "set x to no or 5\nshow x\nset y to 0 or 5\nshow y\nset z to 5 and \"hi\"\nshow z\nset w to no and \"hi\"\nshow w\n");

        Assert.True(result.Success);
        Assert.Equal(["5", "0", "hi", "no"], result.LocalOutput);
    }

    [Fact]
    public async Task DesugaredSpans_OriginIsUserText()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("probe.bcn", "# beacon 1\nif a == 1\nsay \"x\"\nend if\n");
        BeaconParseResult parsed = BeaconParser.Parse("probe.bcn", lexed.Tokens);
        Assert.NotNull(parsed.Script);
        BeaconScript desugared = BeaconDesugar.Desugar(parsed.Script!);
        Assert.Equal("probe.bcn", desugared.Span.Origin.File);

        var (engine, _, _) = NewEngine();
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\nshow \"health: \" + 5\n");
        BeaconRunResult result = await engine.RunTopLevelAsync("probe");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("probe.bcn", result.Error!.Span.Origin.File);
    }

    [Fact]
    public void WaitEveryCooldown_NumberLiteralWhenClean()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("probe.bcn", "# beacon 1\nwait 2 seconds\n");
        BeaconParseResult parsed = BeaconParser.Parse("probe.bcn", lexed.Tokens);
        Assert.NotNull(parsed.Script);
        var top = Assert.IsType<TopStatement>(Assert.Single(parsed.Script!.Decls));
        Assert.IsType<NumberLiteral>(Assert.IsType<WaitStmt>(top.Statement).Count);
    }

    [Fact]
    public async Task Repeat_CountsNotStaticallyChecked_ValidateAtRuntime()
    {
        var (engine, _, _) = NewEngine();
        engine.LoadSource("rep", "rep.bcn", "# beacon 1\nrepeat \"three\" times\nsay \"hi\"\nend repeat\n");
        // No static numeric check for repeat: Lint stays clean.
        Assert.DoesNotContain(engine.Lint("rep"), d => d.Severity == BeaconSeverity.Error);

        BeaconRunResult result = await engine.RunTopLevelAsync("rep");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("repeat 3 times", result.Error!.Suggestion ?? string.Empty);
    }
}

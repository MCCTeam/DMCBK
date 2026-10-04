using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Budget tests: 100k ops and 5 s wall clock.
/// Wait-less-loop aborts carry the <c>every</c> suggestion plus handler, line, locals, stack, and seed context.
/// Deep-recursion bounds, fuel accounting at calls, loop edges, waits, and host calls, wall-clock enforcement over the virtual clock, catchable aborts, seeded replay, and scheduler task wiring.
/// </summary>
public sealed class BudgetTests
{
    private sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public Func<string, string> MccHandler { get; set; } = _ => string.Empty;

        public Task SayAsync(string text, CancellationToken ct = default)
        {
            Says.Add(text);
            return Task.CompletedTask;
        }

        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(MccHandler(commandLine));
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static (BeaconEngine Engine, RecordingHost Host, VirtualClock Clock) NewEngine(
        int seed = 42, long fuelLimit = 100_000)
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget(fuelLimit));
        return (engine, host, clock);
    }

    private static BeaconInterpreter NewInterpreter(
        RecordingHost host, VirtualClock clock, long fuelLimit = 100_000,
        TimeSpan? wallClock = null, int seed = 42)
    {
        return new BeaconInterpreter(
            "probe", "probe.bcn", host, clock, new SeededRng(seed), new FuelBudget(fuelLimit),
            budgetWallClock: wallClock);
    }

    private static BeaconScript MustParse(string fileName, string source)
    {
        BeaconLexResult lexed = BeaconLexer.Lex(fileName, source);
        BeaconParseResult parsed = BeaconParser.Parse(fileName, lexed.Tokens);
        Assert.NotNull(parsed.Script);
        return BeaconDesugar.Desugar(parsed.Script!);
    }

    private static async Task<BeaconRunResult> RunTopLevelAsync(
        BeaconEngine engine, string scriptId, string body)
    {
        engine.LoadSource(scriptId, scriptId + ".bcn", "# beacon 1\n" + body);
        IReadOnlyList<BeaconDiagnostic> errors = engine.Lint(scriptId)
            .Where(d => d.Severity == BeaconSeverity.Error).ToList();
        Assert.Empty(errors);
        return await engine.RunTopLevelAsync(scriptId);
    }

    [Fact]
    public void ProposalNumbers_Ship100kOpsAnd5sWallClock()
    {
        Assert.Equal(100_000, FuelBudget.DefaultLimit);
        Assert.Equal(100_000, BeaconBudgetLimits.MaxOperations);
        Assert.Equal(TimeSpan.FromSeconds(5), BeaconBudgetLimits.MaxWallClock);
    }

    [Fact]
    public void DefaultMaxCallDepth_IsBounded()
    {
        Assert.Equal(256, BeaconBudgetLimits.MaxCallDepth);
    }

    [Fact]
    public async Task WaitLessInfiniteLoop_AbortsWithEverySuggestionAndContextDump()
    {
        var (engine, _, _) = NewEngine(fuelLimit: 300);
        BeaconRunResult result = await RunTopLevelAsync(
            engine, "spin", "while yes\nshow \"spin\"\nend while\nshow \"never\"\n");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, result.Error!.Code);
        Assert.Contains("every", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("fuel", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("top-level", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("seed", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("locals", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("stack", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never", result.LocalOutput);
    }

    [Fact]
    public async Task WaitLessLoop_InHandler_NamesHandlerAndLine()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(9), new FuelBudget(300));
        engine.LoadSource("loop", "loop.bcn", "# beacon 1\non chat\nwhile yes\nshow \"x\"\nend while\nend on\n");
        Assert.DoesNotContain(engine.Lint("loop"), d => d.Severity == BeaconSeverity.Error);
        Assert.True((await engine.RunTopLevelAsync("loop")).Success);

        BeaconRunResult result = await engine.InvokeHandlerAsync(
            "loop", "chat", new Dictionary<string, BeaconValue>
            {
                ["player"] = BeaconValue.Text("Alex"),
                ["message"] = BeaconValue.Text("hi"),
            });

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, result.Error!.Code);
        Assert.Contains("on chat", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("every", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepRecursion_AbortsBeforeHostSuffers()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunTopLevelAsync(engine, "rec",
            "function rec(n)\nreturn rec(n + 1)\nend function\nset x to rec(0)\nshow x\n");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, result.Error!.Code);
        Assert.Contains("rec", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(BeaconBudgetLimits.MaxCallDepth.ToString(), result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursionDepthGuard_FiresEvenWithHugeFuel()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var interp = NewInterpreter(host, clock, fuelLimit: 10_000_000);
        interp.Budget.MaxCallDepth = 32;

        BeaconScript script = MustParse("probe.bcn",
            "function rec(n)\nreturn rec(n + 1)\nend function\nset x to rec(0)\nshow x\n");

        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 7)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, result.Error!.Code);
        Assert.Contains("32", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FuelAccounting_ChargesCallsLoopsWaitsAndHostCalls()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(3), new FuelBudget());
        engine.LoadSource("acct", "acct.bcn",
            "# beacon 1\nsay \"a\"\nrepeat 2 times\nshow \"x\"\nend repeat\n" +
            "function f()\nreturn 1\nend function\nset y to f()\n" +
            "wait 1 second\nset z to mcc \"/list\"\nshow \"{y} {z}\"\n");
        Assert.DoesNotContain(engine.Lint("acct"), d => d.Severity == BeaconSeverity.Error);

        Task<BeaconRunResult> pending = engine.RunTopLevelAsync("acct");
        await Task.Delay(25);
        clock.Advance(TimeSpan.FromSeconds(1));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        // say(1) + repeat back-edges(2) + call f(1) + wait(1) + mcc(1) = 6. set/show cost nothing.
        Assert.Equal(6, engine.Fuel.Used);
    }

    [Fact]
    public void WallClock_AbortsComputeWithoutYield()
    {
        var clock = new VirtualClock();
        var budget = new BeaconDispatchBudget(
            new FuelBudget(), clock, wallClockLimit: TimeSpan.FromMilliseconds(100));
        budget.Reset(7, "top-level");
        var span = new SourceSpan("t.bcn", 1, 1, 0);

        budget.Spend(null, span);
        clock.Advance(TimeSpan.FromMilliseconds(101));

        BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(() => budget.Spend(null, span));
        Assert.Equal(BeaconDiagnosticCodes.BudgetExhausted, ex.Code);
        Assert.True(ex.IsCatchable);
        Assert.Contains("wall", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("top-level", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitsRearmWallClock_LongWaitedDispatchSurvives()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var interp = NewInterpreter(host, clock, wallClock: TimeSpan.FromSeconds(5));
        BeaconScript script = MustParse("probe.bcn",
            "wait 1 second\nwait 1 second\nwait 1 second\nwait 1 second\n" +
            "wait 1 second\nwait 1 second\nwait 1 second\nshow \"still alive\"\n");

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 11);
        for (int i = 0; i < 7; i++)
        {
            await Task.Delay(25);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Contains("still alive", result.LocalOutput);
    }

    [Fact]
    public async Task BudgetAbort_IsCatchableWithCodeAndLine()
    {
        var (engine, _, _) = NewEngine(fuelLimit: 80);
        BeaconRunResult result = await RunTopLevelAsync(engine, "caught",
            "try\nwhile yes\nshow \"spin\"\nend while\ncatch err\nshow err.message\nshow err.code\nshow err.line\nend try\nshow \"after\"\n");

        Assert.True(result.Success);
        Assert.Contains("after", result.LocalOutput);
        Assert.Contains(BeaconDiagnosticCodes.BudgetExhausted, result.LocalOutput);
        Assert.Contains("3", result.LocalOutput);
    }

    [Fact]
    public async Task SeededReplay_ReproducesDispatchBitForBit()
    {
        const string source =
            "show random(1000)\nshow pick([1, 2, 3, 4, 5])\nshow chance(0.5)\nshow random(1000)\n";
        BeaconScript firstParsed = MustParse("r.bcn", source);
        BeaconScript secondParsed = MustParse("r.bcn", source);

        var hostA = new RecordingHost();
        var interpA = NewInterpreter(hostA, new VirtualClock());
        BeaconRunResult a = await interpA.RunTopLevelAsync(firstParsed, null, CancellationToken.None, seed: 42);

        var hostB = new RecordingHost();
        var interpB = NewInterpreter(hostB, new VirtualClock());
        BeaconRunResult b = await interpB.RunTopLevelAsync(secondParsed, null, CancellationToken.None, seed: 42);

        Assert.True(a.Success);
        Assert.True(b.Success);
        Assert.Equal(a.LocalOutput, b.LocalOutput);
        Assert.Equal(4, a.LocalOutput.Count);
    }

    [Fact]
    public async Task BudgetAbort_RecordsSeedForReplay()
    {
        var host = new RecordingHost();
        var interp = NewInterpreter(host, new VirtualClock(), fuelLimit: 200);
        BeaconScript script = MustParse("probe.bcn", "while yes\nshow \"x\"\nend while\n");

        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 4242);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("4242", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitZero_FloorsAt100ms()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var interp = NewInterpreter(host, clock);
        BeaconScript script = MustParse("probe.bcn", "wait 0 seconds\nshow \"done\"\n");

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 5);
        await Task.Delay(25);
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(25);
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains("done", result.LocalOutput);
    }

    [Fact]
    public async Task SchedulerBoundWait_EnforcesSleepCap()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var fuel = new FuelBudget();
        var scheduler = new BeaconScheduler("w", clock, fuel);
        var interp = NewInterpreter(host, clock);
        interp.Scheduler = scheduler;
        BeaconScript script = MustParse("probe.bcn", "wait 1 second\nshow \"done\"\n");

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 5);
        await Task.Delay(50);
        Assert.Equal(1, scheduler.PendingSleepCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains("done", result.LocalOutput);
        Assert.Equal(0, scheduler.PendingSleepCount);
    }

    [Fact]
    public async Task StartAwaitCancel_TaskLifecycle()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var scheduler = new BeaconScheduler("t", clock, new FuelBudget());
        var interp = NewInterpreter(host, clock);
        interp.Scheduler = scheduler;
        BeaconScript script = MustParse("probe.bcn",
            "function patrol()\nreturn 7\nend function\n" +
            "start patrol()\nset ts to tasks()\nset id to ts[0].id\nawait id\n" +
            "set done to tasks()\nshow done[0].status\nshow done[0].result\n");

        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 5)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains("completed", result.LocalOutput);
        Assert.Contains("7", result.LocalOutput);
    }

    [Fact]
    public async Task CancelTask_MakesAwaitRaiseCatchably()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var scheduler = new BeaconScheduler("t", clock, new FuelBudget());
        var interp = NewInterpreter(host, clock);
        interp.Scheduler = scheduler;
        BeaconScript script = MustParse("probe.bcn",
            "function sleeper()\nwait 60 seconds\nreturn 1\nend function\n" +
            "start sleeper()\nset ts to tasks()\nset id to ts[0].id\ncancel task id\n" +
            "try\nawait id\ncatch err\nshow err.code\nend try\nshow \"after\"\n");

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 5);
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains(BeaconTaskErrorCodes.TaskCancelled, result.LocalOutput);
        Assert.Contains("after", result.LocalOutput);
    }

    [Fact]
    public async Task StartWithoutScheduler_KeepsLegacyRefusal()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult result = await RunTopLevelAsync(engine, "nostart", "start patrol()\n");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("scheduler", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Throttles: the TPS-guard scenario (fires once, quiet for 300 s, refires after), shared-name windows, per-script isolation, RAM-only reset semantics, and cooldown resolution.
/// </summary>
public sealed class ThrottleTests
{
    private sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
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
            => Task.FromResult(string.Empty);

        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public string? SelfName => SelfNameValue;
        public IReadOnlyList<string> OnlinePlayers(int limit) => OnlinePlayersValue.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static async Task<(BeaconInterpreter Interpreter, List<OnBlock> Blocks)> LoadAsync(
        string scriptId, string body, RecordingHost host, VirtualClock clock)
    {
        BeaconLexResult lexed = BeaconLexer.Lex(scriptId + ".mcc", "# beacon 1\n" + body);
        BeaconParseResult parsed = BeaconParser.Parse(scriptId + ".mcc", lexed.Tokens, 1);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.NotNull(parsed.Script);
        BeaconScript script = BeaconDesugar.Desugar(parsed.Script!);
        var interpreter = new BeaconInterpreter(scriptId, scriptId + ".mcc", host, clock, new SeededRng(7), new FuelBudget());
        BeaconRunResult top = await interpreter.RunTopLevelAsync(script, lexed.Comments);
        Assert.True(top.Success);
        return (interpreter, script.Decls.OfType<OnBlock>().ToList());
    }

    private static BeaconEventBus NewBus(
        VirtualClock clock, IReadOnlyDictionary<string, BeaconInterpreter> interpreters)
    {
        BeaconEventInvoker invoker = (sid, block, snap, ct) => interpreters[sid].InvokeHandlerAsync(block, snap, ct);
        return new BeaconEventBus(invoker, clock);
    }

    [Fact]
    public void ResolveWindow_SecondsMinutesHours()
    {
        var span = new SourceSpan("test.mcc", 1, 1, 0);
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
                span, new NumberLiteral(span, 300, "300"), "second", "seconds", span, "tps-warn", span)));
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
                span, new NumberLiteral(span, 2, "2"), "minute", "minutes", span, "m", span)));
        Assert.Equal(
            TimeSpan.FromHours(1),
            BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
                span, new NumberLiteral(span, 1, "1"), "hour", "hour", span, "h", span)));
    }

    [Fact]
    public void ResolveWindow_Null_WhenAbsentOrUnresolvable()
    {
        var span = new SourceSpan("test.mcc", 1, 1, 0);
        Assert.Null(BeaconThrottleRegistry.ResolveWindow(null));
        Assert.Null(BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
            span, new NumberLiteral(span, 1, "1"), string.Empty, string.Empty, span, "x", span)));
        Assert.Null(BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
            span, new NumberLiteral(span, -5, "-5"), "second", "seconds", span, "x", span)));
        Assert.Null(BeaconThrottleRegistry.ResolveWindow(new BeaconCooldown(
            span, new IdentExpr(span, "n"), "second", "seconds", span, "x", span)));
    }

    [Fact]
    public async Task TpsGuard_FiresOnce_Quiet300s_RefiresAfter()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string body =
            "on tps cooldown 300 seconds named \"tps-warn\" when tps < 15\nsay \"lag!\"\nend on\n";
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync("tps-guard", body, host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["tps-guard"] = interpreter });
        bus.RegisterScriptHandlers("tps-guard", blocks, out IReadOnlyList<BeaconDiagnostic> warnings);
        Assert.Empty(warnings);

        BeaconFireResult first = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(first.Handlers[0].Throttled);
        Assert.Single(host.Says);

        BeaconFireResult second = await bus.FireEventAsync("tps", BeaconEventFields.Tps(9, 95));
        Assert.True(second.Handlers[0].Throttled);
        Assert.Single(host.Says);
        Assert.Single(second.DebugLog);
        Assert.Contains("tps-warn", second.DebugLog[0], StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(299));
        BeaconFireResult third = await bus.FireEventAsync("tps", BeaconEventFields.Tps(8, 99));
        Assert.True(third.Handlers[0].Throttled);
        Assert.Single(host.Says);

        clock.Advance(TimeSpan.FromSeconds(1));
        BeaconFireResult fourth = await bus.FireEventAsync("tps", BeaconEventFields.Tps(7, 100));
        Assert.False(fourth.Handlers[0].Throttled);
        Assert.Equal(2, host.Says.Count);
    }

    [Fact]
    public async Task SharedName_SharesWindow()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string body =
            "on tps cooldown 60 seconds named \"shared\"\nshow \"first\"\nend on\n" +
            "on tps cooldown 60 seconds named \"shared\"\nshow \"second\"\nend on\n";
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync("shared", body, host, clock);
        Assert.Equal(2, blocks.Count);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["shared"] = interpreter });
        bus.RegisterScriptHandlers("shared", blocks, out _);

        BeaconFireResult fire = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(fire.Handlers[0].Throttled);
        Assert.True(fire.Handlers[1].Throttled);
        Assert.Equal(["first"], fire.Handlers[0].Result!.LocalOutput);

        clock.Advance(TimeSpan.FromSeconds(60));
        BeaconFireResult refire = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(refire.Handlers[0].Throttled);
        Assert.True(refire.Handlers[1].Throttled);
    }

    [Fact]
    public async Task DistinctNames_DoNotShare()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string body =
            "on tps cooldown 60 seconds named \"a\"\nshow \"first\"\nend on\n" +
            "on tps cooldown 60 seconds named \"b\"\nshow \"second\"\nend on\n";
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync("distinct", body, host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["distinct"] = interpreter });
        bus.RegisterScriptHandlers("distinct", blocks, out _);

        BeaconFireResult fire = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.All(fire.Handlers, h => Assert.False(h.Throttled));
        Assert.Equal(["first"], fire.Handlers[0].Result!.LocalOutput);
        Assert.Equal(["second"], fire.Handlers[1].Result!.LocalOutput);
    }

    [Fact]
    public async Task Windows_ArePerScript_AndResetOnReload()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string bodyA = "on tps cooldown 60 seconds named \"w\"\nshow \"A\"\nend on\n";
        const string bodyB = "on tps cooldown 60 seconds named \"w\"\nshow \"B\"\nend on\n";
        (BeaconInterpreter interpA, List<OnBlock> blocksA) = await LoadAsync("ra", bodyA, host, clock);
        (BeaconInterpreter interpB, List<OnBlock> blocksB) = await LoadAsync("rb", bodyB, host, clock);
        var interpreters = new Dictionary<string, BeaconInterpreter> { ["ra"] = interpA, ["rb"] = interpB };
        var bus = NewBus(clock, interpreters);
        bus.RegisterScriptHandlers("ra", blocksA, out _);
        bus.RegisterScriptHandlers("rb", blocksB, out _);

        // Same throttle name in two scripts: both fire (windows are per script).
        BeaconFireResult first = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.All(first.Handlers, h => Assert.False(h.Throttled));

        // Immediate refire: both throttled.
        BeaconFireResult throttled = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.All(throttled.Handlers, h => Assert.True(h.Throttled));

        // Reload of one script resets only its windows (RAM-only).
        bus.ResetThrottle("ra");
        BeaconFireResult afterReset = await bus.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        BeaconHandlerFire fireA = Assert.Single(afterReset.Handlers, h => string.Equals(h.ScriptId, "ra", StringComparison.Ordinal));
        BeaconHandlerFire fireB = Assert.Single(afterReset.Handlers, h => string.Equals(h.ScriptId, "rb", StringComparison.Ordinal));
        Assert.False(fireA.Throttled);
        Assert.True(fireB.Throttled);
    }

    [Fact]
    public void Registry_IsRamOnly_NewInstanceStartsEmpty()
    {
        var clock = new VirtualClock();
        var first = new BeaconThrottleRegistry(clock);
        Assert.True(first.TryAcquire("s", "n", TimeSpan.FromSeconds(60), out _));
        Assert.False(first.TryAcquire("s", "n", TimeSpan.FromSeconds(60), out TimeSpan remaining));
        Assert.True(remaining > TimeSpan.Zero);

        var second = new BeaconThrottleRegistry(clock);
        Assert.True(second.TryAcquire("s", "n", TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void Registry_IsThrottled_PeeksWithoutMarking()
    {
        var clock = new VirtualClock();
        var registry = new BeaconThrottleRegistry(clock);
        Assert.False(registry.IsThrottled("s", "n", TimeSpan.FromSeconds(60), out _));
        // A peek marks nothing: still unthrottled afterwards.
        Assert.False(registry.IsThrottled("s", "n", TimeSpan.FromSeconds(60), out _));

        Assert.True(registry.TryAcquire("s", "n", TimeSpan.FromSeconds(60), out _));
        Assert.True(registry.IsThrottled("s", "n", TimeSpan.FromSeconds(60), out TimeSpan remaining));
        Assert.True(remaining > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(registry.IsThrottled("s", "n", TimeSpan.FromSeconds(60), out _));
    }
}

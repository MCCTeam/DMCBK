using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Chat-bucket tests: burst 8 per 10 s, global across scripts.
/// Overflow queues then warns with script attribution plus a <c>wait</c> suggestion.
/// Delivery never drops, per-script buckets stay dead under N-script flood, the <c>wait 0</c> spin guard holds, and mute gags transport.
/// </summary>
public sealed class ChatBucketTests
{
    private sealed class LockingHost : IBeaconHostServices
    {
        private readonly object _gate = new();

        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
        public List<string> Servers { get; } = [];

        public Task SayAsync(string text, CancellationToken ct = default)
        {
            lock (_gate)
                Says.Add(text);

            return Task.CompletedTask;
        }

        public Task WhisperAsync(string player, string text, CancellationToken ct = default)
        {
            lock (_gate)
                Whispers.Add((player, text));

            return Task.CompletedTask;
        }

        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
        {
            lock (_gate)
                Servers.Add(commandLine);

            return Task.FromResult(string.Empty);
        }

        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public int SayCount
        {
            get
            {
                lock (_gate)
                    return Says.Count;
            }
        }
    }

    private static string SaysBody(int count, string verb = "say")
    {
        var lines = new List<string>();
        for (int i = 1; i <= count; i++)
            lines.Add(verb == "say" ? $"say \"m{i}\"" : $"whisper \"Steve\" \"m{i}\"");

        return string.Join("\n", lines) + "\n";
    }

    private static BeaconScript MustParse(string source)
    {
        BeaconLexResult lexed = BeaconLexer.Lex("flood.bcn", source);
        BeaconParseResult parsed = BeaconParser.Parse("flood.bcn", lexed.Tokens);
        Assert.NotNull(parsed.Script);
        return BeaconDesugar.Desugar(parsed.Script!);
    }

    [Fact]
    public void ProposalNumbers_ShipBurst8Per10s()
    {
        Assert.Equal(8, BeaconChatBucket.BurstCapacity);
        Assert.Equal(TimeSpan.FromSeconds(10), BeaconChatBucket.RefillInterval);
    }

    [Fact]
    public void Burst_Allows8_Throttles9thWithRetryAfter()
    {
        var bucket = new BeaconChatBucket(new VirtualClock());

        for (int i = 0; i < BeaconChatBucket.BurstCapacity; i++)
        {
            Assert.True(bucket.TryAcquire("a", out TimeSpan none));
            Assert.Equal(TimeSpan.Zero, none);
        }

        Assert.False(bucket.TryAcquire("a", out TimeSpan retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Refill_After10s_AllowsAgain()
    {
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        for (int i = 0; i < BeaconChatBucket.BurstCapacity; i++)
            Assert.True(bucket.TryAcquire("a", out _));

        Assert.False(bucket.TryAcquire("a", out _));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(bucket.TryAcquire("a", out _));
    }

    [Fact]
    public async Task Overflow_QueuesThenWarns_NamingScriptSuggestingWait()
    {
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        var interp = new BeaconInterpreter("flood", "flood.bcn", host, clock, new SeededRng(1), new FuelBudget());
        interp.ChatBucket = bucket;
        BeaconScript script = MustParse(SaysBody(10));

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 3);
        await Task.Delay(100);

        // Burst sent immediately; the rest queue instead of dropping: still pending, nothing lost.
        Assert.False(pending.IsCompleted);
        Assert.Equal(8, host.SayCount);

        clock.Advance(TimeSpan.FromSeconds(10));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Equal(10, host.SayCount);
        BeaconDiagnostic warning = Assert.Single(
            result.Diagnostics, d => string.Equals(d.Code, BeaconDiagnosticCodes.ChatThrottled, StringComparison.Ordinal));
        Assert.Equal(BeaconSeverity.Warning, warning.Severity);
        Assert.Contains("flood", warning.Message, StringComparison.Ordinal);
        Assert.Contains("wait", warning.Message + warning.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Flood_DeliversInOrder_NeverDrops()
    {
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        var interp = new BeaconInterpreter("order", "order.bcn", host, clock, new SeededRng(2), new FuelBudget());
        interp.ChatBucket = bucket;
        BeaconScript script = MustParse(SaysBody(9));

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 4);
        await Task.Delay(50);
        Assert.Equal(8, host.SayCount);
        clock.Advance(TimeSpan.FromSeconds(10));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        List<string> says = host.Says.ToList();

        Assert.Equal(9, says.Count);
        for (int i = 0; i < 9; i++)
            Assert.Equal($"m{i + 1}", says[i]);
    }

    [Fact]
    public async Task NScriptFlood_ThrottledGlobally_PerScriptBypassDead()
    {
        const int scripts = 4;
        const int perScript = 5;
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        BeaconScript script = MustParse(SaysBody(perScript));

        var interps = new List<BeaconInterpreter>();
        for (int i = 0; i < scripts; i++)
        {
            var interp = new BeaconInterpreter(
                $"s{i}", $"s{i}.bcn", host, clock, new SeededRng(100 + i), new FuelBudget());
            interp.ChatBucket = bucket;
            interps.Add(interp);
        }

        List<Task<BeaconRunResult>> pendings = interps
            .Select((interp, i) => interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: i))
            .ToList();
        await Task.Delay(150);

        // One global bucket: only the first burst of 8 (of 20 attempted) passes without waiting.
        // Per-script buckets would have passed all 20.
        Assert.Equal(8, host.SayCount);

        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(10));
        IReadOnlyList<BeaconRunResult> results = await Task.WhenAll(pendings).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(scripts * perScript, host.SayCount);

        // At least one script queued and warned: 20 sends through one burst-8 bucket.
        List<BeaconDiagnostic> throttleWarnings = results
            .SelectMany(r => r.Diagnostics)
            .Where(d => string.Equals(d.Code, BeaconDiagnosticCodes.ChatThrottled, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(throttleWarnings);
        Assert.All(throttleWarnings, w =>
        {
            Assert.Equal(BeaconSeverity.Warning, w.Severity);
            Assert.Matches(@"s\d", w.Message);
            Assert.Contains("wait", w.Message + w.Suggestion, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ThrottledSend_CannotSpinWithoutClockAdvance()
    {
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        for (int i = 0; i < BeaconChatBucket.BurstCapacity; i++)
            Assert.True(bucket.TryAcquire("spin", out _));

        var warnings = new List<BeaconDiagnostic>();
        Task pending = bucket.WaitForSlotAsync(
            "spin", new SourceSpan("spin.bcn", 1, 1, 0), "say", warnings, CancellationToken.None);
        await Task.Delay(50);

        // No clock advance, no slot: a wait-0 style spin cannot burn through the bucket.
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task Mute_GagsTransportButKeepsAudit()
    {
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock) { Muted = true };
        var interp = new BeaconInterpreter("quiet", "quiet.bcn", host, clock, new SeededRng(5), new FuelBudget());
        interp.ChatBucket = bucket;
        BeaconScript script = MustParse("say \"hello\"\n");

        BeaconRunResult gagged = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(gagged.Success);
        Assert.Empty(host.Says);
        Assert.Contains(gagged.PassthroughLog, e => e.Detail.Contains("muted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gagged.LocalEcho, e => e.Contains("muted", StringComparison.OrdinalIgnoreCase));

        bucket.Muted = false;
        BeaconRunResult live = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 2)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(live.Success);
        Assert.Single(host.Says);
    }

    [Fact]
    public async Task Whisper_SharesSameGlobalBucket()
    {
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        var interp = new BeaconInterpreter("mix", "mix.bcn", host, clock, new SeededRng(6), new FuelBudget());
        interp.ChatBucket = bucket;
        BeaconScript script = MustParse(SaysBody(8) + "whisper \"Steve\" \"queued\"\n");

        Task<BeaconRunResult> pending = interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1);
        await Task.Delay(100);
        Assert.Equal(8, host.SayCount);
        Assert.Empty(host.Whispers);

        clock.Advance(TimeSpan.FromSeconds(10));
        BeaconRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Single(host.Whispers);
    }

    [Fact]
    public async Task ServerVerb_BypassesChatBucket_DocumentedScope()
    {
        var host = new LockingHost();
        var clock = new VirtualClock();
        var bucket = new BeaconChatBucket(clock);
        for (int i = 0; i < BeaconChatBucket.BurstCapacity; i++)
            Assert.True(bucket.TryAcquire("drain", out _));

        var interp = new BeaconInterpreter("cmd", "cmd.bcn", host, clock, new SeededRng(7), new FuelBudget());
        interp.ChatBucket = bucket;
        BeaconScript script = MustParse("server \"/home\"\n");

        // The chat bucket throttles say/whisper only; server commands take the command path.
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Single(host.Servers);
    }
}

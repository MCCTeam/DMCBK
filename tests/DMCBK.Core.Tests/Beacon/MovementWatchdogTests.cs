using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Movement watchdog tests: a stalled steering call must always settle terminally and catchably instead of parking the awaiting handler forever.
/// A stalled handler-inline move_goto(399, 71, 250) with the bot 0.6 blocks out waited 40+ minutes with no continuation, no error, and empty tasks().
/// A handler-inline await is not a scheduler task, so the empty listing is expected even while parked.
/// </summary>
public sealed class MovementWatchdogTests
{
    private static readonly TimeSpan ShortWatchdog = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(5);

    private static Task<BeaconMoveArrival> NeverCompletes(
        BeaconMoveTarget target, CancellationToken ct)
    {
        _ = target;
        _ = ct;
        return new TaskCompletionSource<BeaconMoveArrival>().Task;
    }

    private static async Task<BeaconMoveArrival> HonorsCancel(
        BeaconMoveTarget target, CancellationToken ct)
    {
        _ = target;
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return new BeaconMoveArrival(true, "arrived");
    }

    private static BeaconMovementRunner StalledRunner(
        Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>> executor)
        => new(new BeaconFreeMovementScope(), BeaconGameplayGates.AllOn, executor, ShortWatchdog);

    [Fact]
    public async Task Goto_StalledExecutor_TimesOutCatchably()
    {
        var runner = StalledRunner(NeverCompletes);

        Task<BeaconValue> move = runner.GotoAsync(399, 71, 250, "handler");
        BeaconRuntimeException timedOut = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => move.WaitAsync(SettleBound));

        Assert.Equal("B4002", timedOut.Code);
        Assert.True(timedOut.IsCatchable);
        Assert.Contains("timed out", timedOut.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("handler", timedOut.Message, StringComparison.Ordinal);
        Assert.Null(runner.ActiveOwner);
        Assert.Contains(runner.LogLines, l => l.Contains("release owner=handler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Goto_StalledExecutorThatHonorsCancel_ReportsTimeout()
    {
        var runner = StalledRunner(HonorsCancel);

        Task<BeaconValue> move = runner.GotoAsync(399, 71, 250, "handler");
        BeaconRuntimeException timedOut = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => move.WaitAsync(SettleBound));

        Assert.Equal("B4002", timedOut.Code);
        Assert.True(timedOut.IsCatchable);
        Assert.Contains("timed out", timedOut.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.ActiveOwner);
    }

    [Fact]
    public async Task Goto_CompletingInsideWatchdog_Arrives()
    {
        static async Task<BeaconMoveArrival> Trip(
            BeaconMoveTarget target, CancellationToken ct)
        {
            _ = target;
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            return new BeaconMoveArrival(true, "arrived");
        }

        var runner = new BeaconMovementRunner(
            new BeaconFreeMovementScope(), BeaconGameplayGates.AllOn, Trip, TimeSpan.FromSeconds(30));

        BeaconValue result = await runner
            .GotoAsync(399, 71, 250, "handler")
            .WaitAsync(SettleBound);
        var map = Assert.IsType<BeaconMapValue>(result);
        Assert.Equal("yes", BeaconInterpreter.ToDisplayText(map.Entries["reached"]));
        Assert.Null(runner.ActiveOwner);
    }

    [Fact]
    public async Task BackToBack_StalledFirst_SupersededThenWinnerTimesOut()
    {
        var runner = StalledRunner(NeverCompletes);

        Task<BeaconValue> loser = runner.GotoAsync(0, 70, 0, "first");
        await Task.Delay(25);
        Task<BeaconValue> winner = runner.GotoAsync(399, 71, 250, "second");

        BeaconRuntimeException superseded = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => loser.WaitAsync(SettleBound));
        Assert.Equal(BeaconTaskErrorCodes.Superseded, superseded.Code);
        Assert.Contains("second", superseded.Message, StringComparison.Ordinal);

        BeaconRuntimeException timedOut = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => winner.WaitAsync(SettleBound));
        Assert.Equal("B4002", timedOut.Code);
        Assert.Contains("timed out", timedOut.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.ActiveOwner);
    }

    [Fact]
    public async Task Follow_StalledExecutor_StaysPendingUntilStopped()
    {
        var runner = StalledRunner(HonorsCancel);

        Task<BeaconValue> follow = runner.FollowAsync("Alex", "greeter");
        await Assert.ThrowsAsync<TimeoutException>(() => follow.WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("greeter", runner.ActiveOwner);

        runner.StopMoving("test end");
        BeaconRuntimeException stopped = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => follow.WaitAsync(SettleBound));
        Assert.Equal(BeaconTaskErrorCodes.TaskCancelled, stopped.Code);
    }

    [Fact]
    public async Task Script_StalledMoveInHandler_ContinuationRunsViaCatch()
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock();
        var interpreter = new BeaconInterpreter("m", "m.mcc", host, clock, new SeededRng(1), new FuelBudget())
        {
            Movement = StalledRunner(NeverCompletes),
        };

        const string body =
            "try\n" +
            "set r to move_goto(399, 71, 250)\n" +
            "show \"arrived\"\n" +
            "catch err\n" +
            "show \"caught\"\n" +
            "end try\n" +
            "show \"continued\"\n";
        BeaconLexResult lexed = BeaconLexer.Lex("m.mcc", "# beacon 1\n" + body);
        BeaconHeaderResult header = BeaconHeader.Parse("m.mcc", lexed.NormalizedSource, 2, lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse("m.mcc", lexed.Tokens, header.Major);
        Assert.NotNull(parsed.Script);
        interpreter.BeginDispatch("top-level", seed: 1);

        BeaconRunResult run = await interpreter
            .RunTopLevelAsync(BeaconDesugar.Desugar(parsed.Script), [], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.DoesNotContain("arrived", run.LocalOutput);
        Assert.Contains("caught", run.LocalOutput);
        Assert.Contains("continued", run.LocalOutput);
    }
}

using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Movement-task tests: newest-wins preemption across handlers, superseded attribution, stop/cancel paths, arrival, gameplay gates, the bedtime-farmer scenario, and lease release on session loss.
/// All virtual-time with a fake lease scope.
/// </summary>
public sealed class MovementPreemptionTests
{
    private static Task PumpAsync(int ms = 25) => Task.Delay(ms);

    private sealed class FakeMovementScope : IBeaconMovementScope
    {
        private readonly object _gate = new();
        public string? Owner { get; private set; }
        public int AcquireAttempts { get; private set; }
        public List<FakeLease> Leases { get; } = [];

        public string? MovementOwner
        {
            get
            {
                lock (_gate)
                    return Owner;
            }
        }

        public IBeaconMovementLease? TryAcquireMovement(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            lock (_gate)
            {
                AcquireAttempts++;
                if (Owner is not null)
                    return null;

                Owner = reason;
                string captured = reason;
                var lease = new FakeLease(captured, () =>
                {
                    lock (_gate)
                    {
                        if (Owner == captured)
                            Owner = null;
                    }
                });
                Leases.Add(lease);
                return lease;
            }
        }

        internal sealed class FakeLease(string owner, Action onDispose) : IBeaconMovementLease
        {
            public string Owner => owner;
            public bool Disposed { get; private set; }

            public void Dispose()
            {
                if (!Disposed)
                {
                    Disposed = true;
                    onDispose();
                }
            }
        }
    }

    private static Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>> PendingForever()
        => async (target, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new BeaconMoveArrival(true, "arrived");
        };

    private static Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>> VirtualTrip(
        VirtualClock clock, TimeSpan trip, string detail = "arrived")
        => async (target, ct) =>
        {
            await clock.Delay(trip, ct);
            return new BeaconMoveArrival(true, detail);
        };

    [Fact]
    public async Task EngineCrossScript_NewestWinsAcrossScripts()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        static async Task<BeaconMoveArrival> Split(
            BeaconMoveTarget target, CancellationToken ct)
        {
            // Script A's goal never arrives on its own; anything else does.
            if (target.X == 1 && target.Y == 1 && target.Z == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);

            return new BeaconMoveArrival(true, "arrived");
        }

        engine.MovementBinder = _ => new BeaconMovementBinding(new BeaconFreeMovementScope(), Split, null);
        Task<BeaconRunResult> loser = engine.RunScriptAsync(
            "a", ScriptTestHelpers.WithHeader("move_goto(1, 1, 1)\n"));
        for (int i = 0; i < 200 && engine.SharedMovement.ActiveOwner != "a"; i++)
            await PumpAsync();

        Assert.Equal("a", engine.SharedMovement.ActiveOwner);

        BeaconRunResult winner = await ScriptTestHelpers.RunAsync(engine, "b", "move_goto(2, 2, 2)\n");
        Assert.True(winner.Success, winner.Error?.Message ?? "winner failed");

        BeaconRunResult lost = await loser.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lost.Success);
        Assert.Equal(BeaconTaskErrorCodes.Superseded, lost.Error?.Code);
        Assert.Contains("b", lost.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreemptionDuel_LoserGetsSupersededWithAttribution()
    {
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(scope, BeaconGameplayGates.AllOn, PendingForever());

        Task<BeaconValue> loser = runner.GotoAsync(120, 64, -40, "handler-a");
        await PumpAsync();
        Assert.Equal("handler-a", runner.ActiveOwner);

        Task<BeaconValue> winner = runner.GotoAsync(0, 70, 0, "handler-b");
        await PumpAsync();

        BeaconRuntimeException lost = await Assert.ThrowsAsync<BeaconRuntimeException>(() => loser);
        Assert.Equal(BeaconTaskErrorCodes.Superseded, lost.Code);
        Assert.True(lost.IsCatchable);
        Assert.Contains("handler-b", lost.Message, StringComparison.Ordinal);
        Assert.Contains("handler-a", lost.Message, StringComparison.Ordinal);
        Assert.True(lost.ToErrorValue() is BeaconMapValue map && map.Entries.ContainsKey("message"));

        Assert.Equal("handler-b", runner.ActiveOwner);
        runner.StopMoving("test end");
        await Assert.ThrowsAsync<BeaconRuntimeException>(() => winner);
    }

    [Fact]
    public async Task SerialGotos_NoSupersedeWhenFirstArrives()
    {
        var clock = new VirtualClock();
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(
            scope, BeaconGameplayGates.AllOn, VirtualTrip(clock, TimeSpan.FromSeconds(2)));

        Task<BeaconValue> first = runner.GotoAsync(10, 64, 10, "first");
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        BeaconValue firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("yes", BeaconInterpreter.ToDisplayText(((BeaconMapValue)firstResult).Entries["reached"]));

        Task<BeaconValue> second = runner.GotoAsync(20, 64, 20, "second");
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        BeaconValue secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("yes", BeaconInterpreter.ToDisplayText(((BeaconMapValue)secondResult).Entries["reached"]));
        Assert.Null(runner.ActiveOwner);
        Assert.Equal(2, scope.AcquireAttempts);
    }

    [Fact]
    public async Task StopMoving_CancelsCurrentWithCatchableError()
    {
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(scope, BeaconGameplayGates.AllOn, PendingForever());

        Task<BeaconValue> moving = runner.GotoAsync(5, 64, 5, "patrol");
        await PumpAsync();
        Assert.NotNull(runner.ActiveOwner);

        runner.StopMoving();
        BeaconRuntimeException stopped = await Assert.ThrowsAsync<BeaconRuntimeException>(() => moving);

        Assert.Equal(BeaconTaskErrorCodes.TaskCancelled, stopped.Code);
        Assert.True(stopped.IsCatchable);
        Assert.Null(runner.ActiveOwner);
        Assert.Null(scope.MovementOwner);

        runner.StopMoving();
        await PumpAsync();
    }

    [Fact]
    public async Task Goto_CompletesOnArrival_ReturnsReachedMap()
    {
        var clock = new VirtualClock();
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(
            scope, BeaconGameplayGates.AllOn, VirtualTrip(clock, TimeSpan.FromSeconds(1), "at patrol point"));

        Task<BeaconValue> gotoTask = runner.GotoAsync(120, 64, -40, "patrol");
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        BeaconValue result = await gotoTask.WaitAsync(TimeSpan.FromSeconds(5));

        var map = Assert.IsType<BeaconMapValue>(result);
        Assert.Equal("yes", BeaconInterpreter.ToDisplayText(map.Entries["reached"]));
        Assert.Contains("patrol point", BeaconInterpreter.ToDisplayText(map.Entries["detail"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follow_TracksUntilSupersededByGoto()
    {
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(scope, BeaconGameplayGates.AllOn, PendingForever());

        Task<BeaconValue> follow = runner.FollowAsync("Alex", "greeter");
        await PumpAsync();
        Assert.Equal("greeter", runner.ActiveOwner);

        Task<BeaconValue> gotoTask = runner.GotoAsync(0, 64, 0, "patrol");
        await PumpAsync();

        BeaconRuntimeException lost = await Assert.ThrowsAsync<BeaconRuntimeException>(() => follow);
        Assert.Equal(BeaconTaskErrorCodes.Superseded, lost.Code);
        Assert.Contains("patrol", lost.Message, StringComparison.Ordinal);

        runner.StopMoving("test end");
        await Assert.ThrowsAsync<BeaconRuntimeException>(() => gotoTask);
    }

    [Fact]
    public async Task GameplayGate_PathfindingOff_RefusesWithoutTouchingLease()
    {
        var scope = new FakeMovementScope();
        var gates = BeaconGameplayGates.AllOn with { Pathfinding = false };
        var runner = new BeaconMovementRunner(scope, gates, PendingForever());

        BeaconRuntimeException refused = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => runner.GotoAsync(1, 64, 1, "patrol"));

        Assert.Equal(BeaconTaskErrorCodes.MovementGated, refused.Code);
        Assert.True(refused.IsCatchable);
        Assert.Contains("Pathfinding", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, scope.AcquireAttempts);
        Assert.Null(runner.ActiveOwner);
    }

    [Fact]
    public async Task GameplayGate_FollowNeedsEntity()
    {
        var scope = new FakeMovementScope();
        var gates = BeaconGameplayGates.AllOn with { Entity = false };
        var runner = new BeaconMovementRunner(scope, gates, PendingForever());

        BeaconRuntimeException refused = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => runner.FollowAsync("Alex", "greeter"));

        Assert.Equal(BeaconTaskErrorCodes.MovementGated, refused.Code);
        Assert.Contains("Entity", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, scope.AcquireAttempts);
    }

    [Fact]
    public void RequiredFeatures_MirrorMoveCommand()
    {
        Assert.Equal(
            CommandFeature.Terrain | CommandFeature.Physics | CommandFeature.Pathfinding,
            BeaconMovementRunner.RequiredGotoFeatures);
        Assert.True(BeaconMovementRunner.RequiredFollowFeatures.HasFlag(CommandFeature.Entity));
    }

    [Fact]
    public async Task BedtimeFarmer_Every60s_StartsGoSleep()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("bedtime", clock, new FuelBudget());
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(
            scope, BeaconGameplayGates.AllOn, VirtualTrip(clock, TimeSpan.FromSeconds(5), "at bed"));
        var lifecycle = new BeaconLifecycle(clock);

        lifecycle.RegisterEvery("bedtime", TimeSpan.FromSeconds(60));

        async Task RunBedtimeTickAsync()
        {
            foreach (BeaconEveryFire fire in lifecycle.EveryTick())
            {
                sched.Log($"every {fire.Name} catchup={fire.WasCatchUp}");
                BeaconTask sleep = sched.StartTask("go_sleep", async ctx =>
                {
                    ctx.Log("go_sleep begin");
                    BeaconValue arrival = await runner.GotoAsync(120, 65, -40, "go_sleep", ctx.CancellationToken);
                    var map = (BeaconMapValue)arrival;
                    ctx.Log($"go_sleep {BeaconInterpreter.ToDisplayText(map.Entries["reached"])}");
                    return null;
                });
                await sleep.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(60));
        await PumpAsync();
        Task tick = RunBedtimeTickAsync();
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await tick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            sched.LogLines, l => l.Contains("go_sleep begin", StringComparison.Ordinal));
        Assert.Contains(
            sched.LogLines, l => l.Contains("every bedtime", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromSeconds(300));
        await PumpAsync();
        int before = sched.LogLines.Count(l => l.Contains("every bedtime", StringComparison.Ordinal));
        Task tick2 = RunBedtimeTickAsync();
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await tick2.WaitAsync(TimeSpan.FromSeconds(5));
        await PumpAsync();
        int after = sched.LogLines.Count(l => l.Contains("every bedtime", StringComparison.Ordinal));
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task SessionLost_ReleasesLeaseAndCancelsMovement()
    {
        var scope = new FakeMovementScope();
        var runner = new BeaconMovementRunner(scope, BeaconGameplayGates.AllOn, PendingForever());

        Task<BeaconValue> moving = runner.GotoAsync(9, 64, 9, "patrol");
        await PumpAsync();
        Assert.NotNull(scope.MovementOwner);

        runner.HandleSessionLost("disconnect");
        BeaconRuntimeException lost = await Assert.ThrowsAsync<BeaconRuntimeException>(() => moving);

        Assert.Equal(BeaconTaskErrorCodes.TaskCancelled, lost.Code);
        Assert.Null(runner.ActiveOwner);
        Assert.Null(scope.MovementOwner);
        Assert.All(scope.Leases, lease => Assert.True(lease.Disposed));
    }

    [Fact]
    public async Task ExternalLeaseHolder_RefusesReadably()
    {
        var scope = new FakeMovementScope();

        using IBeaconMovementLease? external = scope.TryAcquireMovement("Farmer");
        Assert.NotNull(external);

        var beacon = new BeaconMovementRunner(scope, BeaconGameplayGates.AllOn, PendingForever());
        BeaconRuntimeException refused = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => beacon.GotoAsync(1, 64, 1, "patrol"));

        Assert.Equal(BeaconTaskErrorCodes.Superseded, refused.Code);
        Assert.Contains("Farmer", refused.Message, StringComparison.Ordinal);
        Assert.Null(beacon.ActiveOwner);
    }
}

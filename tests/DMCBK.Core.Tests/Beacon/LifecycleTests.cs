using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Lifecycle tests: start/login/logout/disconnect/reconnect hooks, every-block cadence, the max-one catch-up bound (no greeting storms), and refire after reconnect.
/// </summary>
public sealed class LifecycleTests
{
    private static Task PumpAsync(int ms = 25) => Task.Delay(ms);

    [Fact]
    public void OnceTick_FiresDueTimerExactlyOnceAcrossTicks()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);
        lifecycle.RegisterOnce("in-0", TimeSpan.FromSeconds(7));

        Assert.Empty(lifecycle.OnceTick());
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Single(lifecycle.OnceTick());
        // Settled timers leave the registry: later ticks never refire.
        Assert.Empty(lifecycle.OnceTick());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(lifecycle.OnceTick());
        Assert.Equal(0, lifecycle.PendingOnceCount);
    }

    [Fact]
    public async Task Hooks_FireInRegistrationOrder()
    {
        var lifecycle = new BeaconLifecycle(new VirtualClock());
        var order = new List<string>();

        lifecycle.OnHook(BeaconLifecycleEvent.Login, "first", _ => { order.Add("first"); return Task.CompletedTask; });
        lifecycle.OnHook(BeaconLifecycleEvent.Login, "second", _ => { order.Add("second"); return Task.CompletedTask; });

        await lifecycle.FireAsync(BeaconLifecycleEvent.Login);

        Assert.Equal(["first", "second"], order);
        Assert.Equal([BeaconLifecycleEvent.Login], lifecycle.Fired);
    }

    [Fact]
    public async Task StartHook_RunsOnLoad_BeforeLogin()
    {
        var lifecycle = new BeaconLifecycle(new VirtualClock());
        var seen = new List<string>();

        lifecycle.OnHook(BeaconLifecycleEvent.Start, "load", _ => { seen.Add("start"); return Task.CompletedTask; });
        lifecycle.OnHook(BeaconLifecycleEvent.Login, "hello", _ => { seen.Add("login"); return Task.CompletedTask; });

        await lifecycle.FireAsync(BeaconLifecycleEvent.Start);
        await lifecycle.FireAsync(BeaconLifecycleEvent.Login);

        Assert.Equal(["start", "login"], seen);
    }

    [Fact]
    public async Task LoginAndReconnect_RefireAfterReconnect()
    {
        var lifecycle = new BeaconLifecycle(new VirtualClock());
        var greetings = new List<string>();

        lifecycle.OnHook(BeaconLifecycleEvent.Login, "hello", _ =>
        {
            greetings.Add("Back online and ready.");
            return Task.CompletedTask;
        });
        lifecycle.OnHook(BeaconLifecycleEvent.Disconnect, "bye", _ =>
        {
            greetings.Add("Lost connection, tasks parked.");
            return Task.CompletedTask;
        });
        lifecycle.OnHook(BeaconLifecycleEvent.Reconnect, "back", _ =>
        {
            greetings.Add("Back online and ready.");
            return Task.CompletedTask;
        });

        await lifecycle.FireAsync(BeaconLifecycleEvent.Login);
        await lifecycle.FireAsync(BeaconLifecycleEvent.Disconnect);
        await lifecycle.FireAsync(BeaconLifecycleEvent.Reconnect);

        Assert.Equal(
            [
                "Back online and ready.",
                "Lost connection, tasks parked.",
                "Back online and ready.",
            ],
            greetings);
        Assert.Equal(
            [
                BeaconLifecycleEvent.Login,
                BeaconLifecycleEvent.Disconnect,
                BeaconLifecycleEvent.Reconnect,
            ],
            lifecycle.Fired);
    }

    [Fact]
    public async Task LogoutHook_Fires()
    {
        var lifecycle = new BeaconLifecycle(new VirtualClock());
        bool fired = false;

        lifecycle.OnHook(BeaconLifecycleEvent.Logout, "bye", _ =>
        {
            fired = true;
            return Task.CompletedTask;
        });

        await lifecycle.FireAsync(BeaconLifecycleEvent.Logout);

        Assert.True(fired);
        await PumpAsync();
    }

    [Fact]
    public void Every_FiresOnCadence_NotEarly()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);

        lifecycle.RegisterEvery("tip", TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(lifecycle.EveryTick());

        clock.Advance(TimeSpan.FromSeconds(30));
        IReadOnlyList<BeaconEveryFire> fires = lifecycle.EveryTick();
        BeaconEveryFire only = Assert.Single(fires);
        Assert.Equal("tip", only.Name);
        Assert.False(only.WasCatchUp);
        Assert.Equal(0, only.MissedIntervals);

        Assert.Empty(lifecycle.EveryTick());

        clock.Advance(TimeSpan.FromSeconds(60));
        fires = lifecycle.EveryTick();
        only = Assert.Single(fires);
        Assert.False(only.WasCatchUp);
    }

    [Fact]
    public void Every_CatchUp_AtMostOneThenNormalCadence()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);

        lifecycle.RegisterEvery("tip", TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(600));
        IReadOnlyList<BeaconEveryFire> fires = lifecycle.EveryTick();

        BeaconEveryFire only = Assert.Single(fires);
        Assert.True(only.WasCatchUp);
        Assert.Equal(9, only.MissedIntervals);

        Assert.Empty(lifecycle.EveryTick());

        clock.Advance(TimeSpan.FromSeconds(60));
        fires = lifecycle.EveryTick();
        only = Assert.Single(fires);
        Assert.False(only.WasCatchUp);
        Assert.Equal(0, only.MissedIntervals);
    }

    [Fact]
    public async Task GreetingStorm_Regression_OneGreetingAfterLongOutage()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);
        int greetings = 0;

        lifecycle.RegisterEvery("greeter", TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(3600));
        await PumpAsync();

        foreach (BeaconEveryFire fire in lifecycle.EveryTick())
        {
            greetings++;
            Assert.Equal("greeter", fire.Name);
        }

        Assert.Equal(1, greetings);
    }

    [Fact]
    public void Every_IntervalBelowOneSecond_Clamped()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);

        BeaconEveryTimer timer = lifecycle.RegisterEvery("fast", TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromSeconds(1), timer.Interval);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Empty(lifecycle.EveryTick());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Single(lifecycle.EveryTick());
    }

    [Fact]
    public void EveryInterval_ParsesSecondsMinutesHours()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), BeaconLifecycle.EveryInterval(60, "seconds"));
        Assert.Equal(TimeSpan.FromSeconds(1), BeaconLifecycle.EveryInterval(1, "second"));
        Assert.Equal(TimeSpan.FromMinutes(5), BeaconLifecycle.EveryInterval(5, "minutes"));
        Assert.Equal(TimeSpan.FromMinutes(1), BeaconLifecycle.EveryInterval(1, "minute"));
        Assert.Equal(TimeSpan.FromHours(2), BeaconLifecycle.EveryInterval(2, "hours"));
        Assert.Equal(TimeSpan.FromHours(1), BeaconLifecycle.EveryInterval(1, "hour"));

        BeaconRuntimeException bad = Assert.Throws<BeaconRuntimeException>(
            () => BeaconLifecycle.EveryInterval(1, "fortnights"));
        Assert.Equal(BeaconDiagnosticCodes.UnknownUnit, bad.Code);
        Assert.True(bad.IsCatchable);
    }

    [Fact]
    public void Reload_ClearsHooksAndTimers()
    {
        var lifecycle = new BeaconLifecycle(new VirtualClock());

        lifecycle.OnHook(BeaconLifecycleEvent.Login, "h", _ => Task.CompletedTask);
        lifecycle.RegisterEvery("tip", TimeSpan.FromSeconds(60));

        lifecycle.HandleReload();

        Assert.Empty(lifecycle.Fired);
    }
}

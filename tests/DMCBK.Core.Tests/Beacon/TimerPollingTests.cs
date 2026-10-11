using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

public sealed class TimerPollingTests
{
    private const string Timers = "# beacon 1\nevery 1 seconds\nshow \"every\"\nend every\nin 2 seconds do\nshow \"once\"\nend in\n";

    [Fact]
    public void IdleLifecyclePolling_DoesNotAllocateOrChangeDeadlines()
    {
        var clock = new VirtualClock();
        var lifecycle = new BeaconLifecycle(clock);
        BeaconEveryTimer timer = lifecycle.RegisterEvery("every", TimeSpan.FromMinutes(1));
        lifecycle.RegisterOnce("once", TimeSpan.FromMinutes(2));
        DateTimeOffset deadline = timer.NextFire;
        for (int i = 0; i < 100; i++) { lifecycle.EveryTick(); lifecycle.OnceTick(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { lifecycle.EveryTick(); lifecycle.OnceTick(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(deadline, timer.NextFire);
        Assert.Equal(1, lifecycle.PendingOnceCount);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(lifecycle.EveryTick());
        Assert.Single(lifecycle.OnceTick());
    }

    [Fact]
    public async Task IdleEnginePolling_AllocationDoesNotScaleWithScriptCount()
    {
        var engine = new BeaconEngine(BeaconOfflineHost.Shared, new VirtualClock());
        for (int i = 0; i < 32; i++)
            Assert.True((await engine.RunScriptAsync($"script-{i}", Timers)).Success);
        for (int i = 0; i < 100; i++) { await engine.TickEveryAsync(); await engine.TickOnceAsync(); }

        // With no due body, both dispatches complete synchronously on this thread.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { await engine.TickEveryAsync(); await engine.TickOnceAsync(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 100 * 1024, $"Idle timer polling allocated {allocated} bytes.");
    }

    [Fact]
    public async Task TimerPlans_RefreshAfterAddReloadRemoveAndHostReplacement()
    {
        var clock = new VirtualClock();
        var engine = new BeaconEngine(BeaconOfflineHost.Shared, clock);
        Assert.True((await engine.RunScriptAsync("z", Timers)).Success);
        await engine.TickEveryAsync();
        await engine.TickOnceAsync();
        Assert.True((await engine.RunScriptAsync("a", Timers)).Success);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(["a", "z"], (await engine.TickEveryAsync()).Select(r => r.ScriptId));
        Assert.Equal(["a", "z"], (await engine.TickOnceAsync()).Select(r => r.ScriptId));

        Assert.True(engine.RemoveScript("a"));
        Assert.True((await engine.RunScriptAsync("z", Timers.Replace("\"every\"", "\"replacement\""), hostServices: new InertHost())).Success);
        clock.Advance(TimeSpan.FromSeconds(2));
        BeaconEveryRun every = Assert.Single(await engine.TickEveryAsync());
        Assert.Equal("z", every.ScriptId);
        Assert.Equal(["replacement"], every.Result.LocalOutput);
        Assert.Single(await engine.TickOnceAsync());

        engine.HandleReload("z");
        Assert.Empty(await engine.TickEveryAsync());
        Assert.Empty(await engine.TickOnceAsync());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(await engine.TickEveryAsync());
        Assert.Single(await engine.TickOnceAsync());
        Assert.True(engine.RemoveScript("z"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await engine.TickEveryAsync());
        Assert.Empty(await engine.TickOnceAsync());
    }

    [Fact]
    public async Task TimerPolling_ChecksLaterDeadlinesAfterEarlierBodyFinishes()
    {
        var clock = new VirtualClock();
        var host = new PausingHost();
        var engine = new BeaconEngine(host, clock);
        Assert.True((await engine.RunScriptAsync("a", "# beacon 1\nevery 1 seconds\nsay \"pause\"\nend every\n")).Success);
        Assert.True((await engine.RunScriptAsync("b", "# beacon 1\nevery 2 seconds\nshow \"later\"\nend every\n")).Success);
        await engine.TickEveryAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<IReadOnlyList<BeaconEveryRun>> tick = engine.TickEveryAsync();
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        host.Resume.SetResult();
        IReadOnlyList<BeaconEveryRun> fires = await tick;
        Assert.Equal(["a", "b"], fires.Select(r => r.ScriptId));
        Assert.Equal(["later"], fires[1].Result.LocalOutput);
    }

    [Fact]
    public async Task RemovedScript_IsNotRetainedByTimerPlan()
    {
        var engine = new BeaconEngine(BeaconOfflineHost.Shared, new VirtualClock());
        WeakReference lifecycle = await PopulatePlanAsync(engine);
        Assert.True(engine.RemoveScript("removed"));
        // Diagnostic collections verify ownership; acceptance timings never force a GC.
        for (int i = 0; i < 3 && lifecycle.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(lifecycle.IsAlive);
        GC.KeepAlive(engine);
    }

    private static async Task<WeakReference> PopulatePlanAsync(BeaconEngine engine)
    {
        Assert.True((await engine.RunScriptAsync("removed", Timers)).Success);
        await engine.TickEveryAsync();
        await engine.TickOnceAsync();
        return new WeakReference(engine.GetLifecycle("removed"));
    }

    private class InertHost : IBeaconHostServices
    {
        public virtual Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Test";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => 20;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class PausingHost : InertHost
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task SayAsync(string text, CancellationToken ct = default)
        {
            Started.TrySetResult();
            return Resume.Task.WaitAsync(ct);
        }
    }
}

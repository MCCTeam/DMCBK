using DMCBK.Core.Beacon;
using DMCBK.Core.Tests.Fakes;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Scheduler tests: background tasks, FIFO dispatch, wait quantum, sleep cap, immutable snapshots, reconnect cancel, dead-session drops, determinism, fuel hooks.
/// All virtual-time; no real sleeps except tiny continuation pumps.
/// </summary>
public sealed class SchedulerTests
{
    private static Task PumpAsync(int ms = 25) => Task.Delay(ms);

    private static async Task PollAsync(Func<bool> ready, int tries = 40, int ms = 10)
    {
        for (int i = 0; i < tries && !ready(); i++)
            await Task.Delay(ms);
    }

    private static void AssertInOrder(IReadOnlyList<string> log, params string[] fragments)
    {
        int cursor = 0;
        foreach (string fragment in fragments)
        {
            int found = -1;
            for (int i = cursor; i < log.Count; i++)
            {
                if (log[i].Contains(fragment, StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }

            Assert.True(found >= 0, $"Log is missing '{fragment}' after index {cursor}:\n{string.Join("\n", log)}");
            cursor = found + 1;
        }
    }

    [Fact]
    public async Task PatrolPlusChat_InterleaveInOrder()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("patrol", clock, new FuelBudget());

        BeaconTask patrol = sched.StartTask("patrol", async ctx =>
        {
            for (int i = 1; i <= 3; i++)
            {
                ctx.Log($"patrol step {i}");
                await ctx.WaitAsync(TimeSpan.FromSeconds(3));
            }

            return null;
        });

        await PollAsync(() => sched.PendingSleepCount == 1);

        BeaconValue? chatResult = await sched.DispatchAsync(
            "chat",
            new Dictionary<string, BeaconValue>
            {
                ["player"] = BeaconValue.Text("Alex"),
                ["message"] = BeaconValue.Text("hi"),
            },
            (ctx, fields) =>
            {
                var player = (BeaconTextValue)fields["player"];
                ctx.Log($"chat from {player.Value}");
                return Task.FromResult<BeaconValue?>(null);
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(chatResult);

        clock.Advance(TimeSpan.FromSeconds(3));
        await PollAsync(() => sched.LogLines.Any(l => l.Contains("patrol step 2", StringComparison.Ordinal)));
        clock.Advance(TimeSpan.FromSeconds(3));
        await PollAsync(() => sched.LogLines.Any(l => l.Contains("patrol step 3", StringComparison.Ordinal)));
        clock.Advance(TimeSpan.FromSeconds(3));
        await patrol.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BeaconTaskStatus.Completed, patrol.Status);
        AssertInOrder(
            sched.LogLines,
            "patrol step 1",
            "chat from Alex",
            "patrol step 2",
            "patrol step 3");
    }

    [Fact]
    public void Start_ReturnsIncrementalIds_TasksListsThem()
    {
        var sched = new BeaconScheduler("ids", new VirtualClock(), new FuelBudget());

        BeaconTask a = sched.StartTask("a", _ => Task.FromResult<BeaconValue?>(null));
        BeaconTask b = sched.StartTask("b", _ => Task.FromResult<BeaconValue?>(null));

        Assert.Equal(a.Id + 1, b.Id);

        IReadOnlyList<BeaconTaskInfo> listed = sched.ListTasks();
        Assert.Contains(listed, t => t.Id == a.Id && t.Name == "a" && t.ScriptId == "ids");
        Assert.Contains(listed, t => t.Id == b.Id && t.Name == "b");
    }

    [Fact]
    public async Task Await_ReturnsTaskResult()
    {
        var sched = new BeaconScheduler("await", new VirtualClock(), new FuelBudget());

        BeaconTask task = sched.StartTask("answer", _ => Task.FromResult<BeaconValue?>(BeaconValue.Number(7)));
        BeaconValue? result = await sched.AwaitTaskAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal("7", BeaconInterpreter.ToDisplayText(result!));
        Assert.Equal(BeaconTaskStatus.Completed, task.Status);
        Assert.NotNull(task.Result);
    }

    [Fact]
    public async Task Await_UnknownId_ThrowsCatchable()
    {
        var sched = new BeaconScheduler("await", new VirtualClock(), new FuelBudget());

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => sched.AwaitTaskAsync(999));

        Assert.True(ex.IsCatchable);
        Assert.Equal(BeaconDiagnosticCodes.UnknownName, ex.Code);
        Assert.True(ex.ToErrorValue() is BeaconMapValue map && map.Entries.ContainsKey("message"));
    }

    [Fact]
    public async Task Cancel_UnblocksSleeperPromptly_WithoutAdvancingClock()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("cancel", clock, new FuelBudget());

        BeaconTask sleeper = sched.StartTask("sleeper", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromSeconds(5));
            ctx.Log("sleeper woke (must not happen)");
            return null;
        });

        await PollAsync(() => sched.PendingSleepCount == 1);
        Assert.True(sched.CancelTask(sleeper.Id));

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => sched.AwaitTaskAsync(sleeper.Id).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(BeaconTaskErrorCodes.TaskCancelled, ex.Code);
        Assert.True(ex.IsCatchable);
        Assert.Equal(BeaconTaskStatus.Cancelled, sleeper.Status);
        Assert.DoesNotContain(sched.LogLines, l => l.Contains("must not happen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_UnknownId_ReturnsFalse()
    {
        var sched = new BeaconScheduler("cancel", new VirtualClock(), new FuelBudget());
        Assert.False(sched.CancelTask(4242));
        await PumpAsync();
    }

    [Fact]
    public async Task Wait_ZeroCannotSpin_FloorsAt100ms()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("wait", clock, new FuelBudget());

        Task wait = sched.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        await PumpAsync();
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(99));
        await PumpAsync();
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wait_Over32PendingSleeps_RefusesCatchably()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("cap", clock, new FuelBudget());
        var tasks = new List<BeaconTask>();

        for (int i = 0; i < BeaconScheduler.MaxPendingSleeps; i++)
        {
            tasks.Add(sched.StartTask($"s{i}", async ctx =>
            {
                await ctx.WaitAsync(TimeSpan.FromMinutes(10));
                return null;
            }));
        }

        await PollAsync(() => sched.PendingSleepCount == BeaconScheduler.MaxPendingSleeps);
        Assert.Equal(32, sched.PendingSleepCount);

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => sched.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(BeaconTaskErrorCodes.TooManySleeps, ex.Code);
        Assert.True(ex.IsCatchable);

        foreach (BeaconTask task in tasks)
            sched.CancelTask(task.Id);

        await PumpAsync();
    }

    [Fact]
    public async Task Dispatch_SnapshotSurvivesMidHandlerMutation()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("snap", clock, new FuelBudget());

        var inner = new Dictionary<string, BeaconValue> { ["n"] = BeaconValue.Number(1) };
        var fields = new Dictionary<string, BeaconValue>
        {
            ["quiz"] = BeaconValue.Map(inner),
            ["player"] = BeaconValue.Text("Alex"),
        };

        string? seenBefore = null;
        string? seenAfter = null;
        string? seenPlayer = null;

        Task<BeaconValue?> dispatch = sched.DispatchAsync(
            "chat", fields, async (ctx, snap) =>
            {
                var quiz = (BeaconMapValue)snap["quiz"];
                seenBefore = BeaconInterpreter.ToDisplayText(quiz.Entries["n"]);
                seenPlayer = BeaconInterpreter.ToDisplayText(snap["player"]);
                await ctx.WaitAsync(TimeSpan.FromSeconds(1));
                var quizLater = (BeaconMapValue)snap["quiz"];
                seenAfter = BeaconInterpreter.ToDisplayText(quizLater.Entries["n"]);
                return null;
            });

        await PollAsync(() => sched.PendingSleepCount == 1);

        inner["n"] = BeaconValue.Number(99);
        fields["player"] = BeaconValue.Text("Mallory");

        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("1", seenBefore);
        Assert.Equal("1", seenAfter);
        Assert.Equal("Alex", seenPlayer);
    }

    [Fact]
    public void CopyEventSnapshot_DeepCopiesListsAndMaps()
    {
        var source = new Dictionary<string, BeaconValue>
        {
            ["text"] = BeaconValue.Text("hi"),
            ["num"] = BeaconValue.Number(3),
            ["flag"] = BeaconValue.YesNo(true),
            ["nothing"] = BeaconValue.None,
            ["list"] = BeaconValue.List([BeaconValue.Number(1), BeaconValue.Text("x")]),
            ["map"] = BeaconValue.Map(new Dictionary<string, BeaconValue>
            {
                ["deep"] = BeaconValue.List([BeaconValue.Number(9)]),
            }),
        };

        IReadOnlyDictionary<string, BeaconValue> copy = BeaconScheduler.CopyEventSnapshot(source);

        Assert.True(BeaconInterpreter.EqualsDeep(BeaconValue.Map(source), BeaconValue.Map(copy)));

        var sourceMap = (BeaconMapValue)source["map"];
        var sourceList = (BeaconListValue)source["list"];
        var copyMap = (BeaconMapValue)copy["map"];
        var copyList = (BeaconListValue)copy["list"];

        Assert.NotSame(sourceMap, copyMap);
        Assert.NotSame(sourceList, copyList);
        Assert.NotSame(
            (BeaconListValue)sourceMap.Entries["deep"],
            (BeaconListValue)copyMap.Entries["deep"]);
    }

    [Fact]
    public async Task Reconnect_CancelsPendingSleeps_NothingFiresAfter()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("recon", clock, new FuelBudget());

        BeaconTask sleeper = sched.StartTask("sleeper", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromSeconds(5));
            ctx.Log("sleeper woke (must not happen)");
            return null;
        });

        await PollAsync(() => sched.PendingSleepCount == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await PumpAsync();

        sched.HandleReconnect();

        await sleeper.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BeaconTaskStatus.Cancelled, sleeper.Status);

        clock.Advance(TimeSpan.FromSeconds(10));
        await PumpAsync();
        Assert.DoesNotContain(sched.LogLines, l => l.Contains("must not happen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatch_IntoDeadSession_DropsHandler()
    {
        var sched = new BeaconScheduler("dead", new VirtualClock(), new FuelBudget());

        sched.HandleDisconnect("test disconnect");
        Assert.False(sched.IsConnected);

        bool ran = false;
        BeaconValue? dropped = await sched.DispatchAsync(
            "chat",
            new Dictionary<string, BeaconValue> { ["message"] = BeaconValue.Text("hi") },
            (ctx, fields) =>
            {
                ran = true;
                return Task.FromResult<BeaconValue?>(null);
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(dropped);
        Assert.False(ran);

        sched.HandleReconnect();
        Assert.True(sched.IsConnected);

        BeaconValue? result = await sched.DispatchAsync(
            "chat",
            new Dictionary<string, BeaconValue> { ["message"] = BeaconValue.Text("hi") },
            (ctx, fields) => Task.FromResult<BeaconValue?>(BeaconValue.Text("live"))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal("live", BeaconInterpreter.ToDisplayText(result!));
    }

    [Fact]
    public async Task VirtualTime_Determinism_SameSeedSameLog()
    {
        List<string> first = await RunScenarioAsync(seed: 2026);
        List<string> second = await RunScenarioAsync(seed: 2026);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        static async Task<List<string>> RunScenarioAsync(int seed)
        {
            var clock = new VirtualClock();
            var sched = new BeaconScheduler("det", clock, new FuelBudget(), new SeededRng(seed));
            sched.Log($"roll {sched.Rng.Next(1000)}");

            BeaconTask worker = sched.StartTask("worker", async ctx =>
            {
                ctx.Log("work begin");
                await ctx.WaitAsync(TimeSpan.FromSeconds(2));
                ctx.Log("work end");
                return BeaconValue.Number(7);
            });

            await PumpAsync();
            await sched.DispatchAsync(
                "chat",
                new Dictionary<string, BeaconValue> { ["player"] = BeaconValue.Text("Alex") },
                (ctx, fields) =>
                {
                    ctx.Log($"chat {BeaconInterpreter.ToDisplayText(fields["player"])}");
                    return Task.FromResult<BeaconValue?>(null);
                }).WaitAsync(TimeSpan.FromSeconds(5));

            clock.Advance(TimeSpan.FromSeconds(2));
            BeaconValue? result = await sched.AwaitTaskAsync(worker.Id).WaitAsync(TimeSpan.FromSeconds(5));
            sched.Log($"result {BeaconInterpreter.ToDisplayText(result!)}");
            return sched.LogLines.ToList();
        }
    }

    [Fact]
    public async Task Fuel_SpendHooks_WaitDispatchStartBackEdge()
    {
        var clock = new VirtualClock();
        var fuel = new FuelBudget();
        var sched = new BeaconScheduler("fuel", clock, fuel);

        BeaconTask task = sched.StartTask("a", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromSeconds(1));
            ctx.SpendBackEdge();
            return null;
        });

        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await task.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await sched.DispatchAsync(
            "chat",
            new Dictionary<string, BeaconValue>(),
            (ctx, fields) => Task.FromResult<BeaconValue?>(null)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1 + 1 + 1 + 1, fuel.Used);
    }

    [Fact]
    public async Task Reload_ResetsTaskState_KeepsSchedulerUsable()
    {
        var clock = new VirtualClock();
        var sched = new BeaconScheduler("reload", clock, new FuelBudget());

        BeaconTask old = sched.StartTask("old", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromMinutes(1));
            return null;
        });

        await PollAsync(() => sched.PendingSleepCount == 1);
        sched.HandleReload();

        await old.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BeaconTaskStatus.Cancelled, old.Status);
        Assert.Empty(sched.ListTasks());

        BeaconTask fresh = sched.StartTask("fresh", _ => Task.FromResult<BeaconValue?>(BeaconValue.Number(1)));
        BeaconValue? result = await sched.AwaitTaskAsync(fresh.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("1", BeaconInterpreter.ToDisplayText(result!));
    }

    [Fact]
    public async Task FakeServer_DisconnectMidSleep_CancelsAndRefusesDispatch()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);
        Assert.Equal(ClientStatus.Playing, fixture.Client.Status);

        var sched = new BeaconScheduler("live", fixture.Clock, new FuelBudget());
        Task wait = sched.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await PumpAsync();
        Assert.Equal(1, sched.PendingSleepCount);

        await fixture.Client.StopAsync();
        sched.HandleDisconnect("session stopped");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(5)));

        bool ran = false;
        BeaconValue? dropped = await sched.DispatchAsync(
            "chat",
            new Dictionary<string, BeaconValue> { ["message"] = BeaconValue.Text("hi") },
            (ctx, fields) =>
            {
                ran = true;
                return Task.FromResult<BeaconValue?>(null);
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(dropped);
        Assert.False(ran);
    }
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Engine integration: scheduler, events, and state wired into <see cref="BeaconEngine"/> plus parser and catalog gaps.
/// All deterministic on <see cref="VirtualClock"/> with a seeded <see cref="SeededRng"/>.
/// </summary>
public sealed class EngineIntegrationTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
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

        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => new[] { "Alice", "Bob" }.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static (BeaconEngine Engine, RecordingHost Host, VirtualClock Clock) NewEngine(int seed = 1234)
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var rng = new SeededRng(seed);
        var engine = new BeaconEngine(host, clock, rng, new FuelBudget());
        return (engine, host, clock);
    }

    private string NewConfigurationsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-eng-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "configurations");
        Directory.CreateDirectory(folder);
        _roots.Add(root);
        return folder;
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    #region A: welcome-bot end to end

    [Fact]
    public async Task WelcomeBot_LoadFireJoin_SayAttributed()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "on join:\nsay \"Welcome to the server, {player}!\"\nend on\n" +
            "on chat when message contains \"!rules\":\nwhisper player \"1. Be kind. 2. No griefing.\"\nend on\n";

        BeaconRunResult run = await engine.RunScriptAsync("welcome", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.False(fire.Suppressed);
        BeaconHandlerFire handler = Assert.Single(fire.Handlers);
        Assert.Equal("welcome", handler.ScriptId);
        Assert.False(handler.Throttled);
        Assert.NotNull(handler.Result);
        Assert.True(handler.Result!.Success);

        Assert.Single(host.Says);
        Assert.Equal("Welcome to the server, Steve!", host.Says[0]);
        Assert.DoesNotContain("[welcome]", host.Says[0]);

        BeaconFireResult chat = await engine.FireEventAsync(
            "chat", BeaconEventFields.Chat("Alex", "hey !rules please"));
        Assert.Single(host.Whispers);
        Assert.Equal("Alex", host.Whispers[0].Player);
        _ = chat;
    }

    [Fact]
    public async Task RunScript_LintFailure_NoRunNoRegistration()
    {
        var (engine, host, _) = NewEngine();
        BeaconRunResult bad = await engine.RunScriptAsync("bad", "# beacon 1\non chat when\nsay \"hi\"\nend on\n");
        Assert.False(bad.Success);
        Assert.NotNull(bad.Error);
        Assert.Empty(host.Says);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "hi"));
        Assert.Empty(fire.Handlers);
    }

    [Fact]
    public async Task RemoveScript_UnregistersSchedulerLifecycleAndBus()
    {
        var (engine, host, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "temp", WithHeader("on join\nsay \"hi {player}\"\nend on\n"));
        Assert.True(run.Success);
        Assert.NotNull(engine.GetScheduler("temp"));
        Assert.NotNull(engine.GetLifecycle("temp"));
        Assert.Contains("temp", engine.EventBus.ScriptIds);

        Assert.True(engine.RemoveScript("temp"));
        Assert.Null(engine.GetScheduler("temp"));
        Assert.Null(engine.GetLifecycle("temp"));
        Assert.DoesNotContain("temp", engine.EventBus.ScriptIds);

        BeaconFireResult fire = await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.Empty(fire.Handlers);
        Assert.Empty(host.Says);
        Assert.False(engine.RemoveScript("temp"));
    }

    #endregion
    #region A: every-block single catch-up

    [Fact]
    public async Task EveryBlock_SingleCatchUp_ViaVirtualClock()
    {
        var (engine, host, clock) = NewEngine();
        host.ServerTpsValue = 20.0;
        const string body = "every 60 seconds\nsay \"tip\"\nend every\n";

        BeaconRunResult run = await engine.RunScriptAsync("tips", WithHeader(body));
        Assert.True(run.Success);
        Assert.NotNull(engine.GetLifecycle("tips"));

        clock.Advance(TimeSpan.FromSeconds(30));
        IReadOnlyList<BeaconEveryRun> early = await engine.TickEveryAsync();
        Assert.Empty(early);
        Assert.Empty(host.Says);

        clock.Advance(TimeSpan.FromSeconds(30));
        IReadOnlyList<BeaconEveryRun> first = await engine.TickEveryAsync();
        BeaconEveryRun only = Assert.Single(first);
        Assert.Equal("tips", only.ScriptId);
        Assert.False(only.Fire.WasCatchUp);
        Assert.Equal(0, only.Fire.MissedIntervals);
        Assert.True(only.Result.Success);
        Assert.Single(host.Says);

        IReadOnlyList<BeaconEveryRun> quiet = await engine.TickEveryAsync();
        Assert.Empty(quiet);

        clock.Advance(TimeSpan.FromSeconds(600));
        IReadOnlyList<BeaconEveryRun> catchUp = await engine.TickEveryAsync();
        BeaconEveryRun single = Assert.Single(catchUp);
        Assert.True(single.Fire.WasCatchUp);
        Assert.Equal(9, single.Fire.MissedIntervals);
        int saysAfterCatchUp = host.Says.Count;

        IReadOnlyList<BeaconEveryRun> after = await engine.TickEveryAsync();
        Assert.Empty(after);
        Assert.Equal(saysAfterCatchUp, host.Says.Count);

        clock.Advance(TimeSpan.FromSeconds(60));
        IReadOnlyList<BeaconEveryRun> normal = await engine.TickEveryAsync();
        Assert.Single(normal);
        Assert.False(normal[0].Fire.WasCatchUp);
    }

    #endregion
    #region A: reconnect cancels + refires

    [Fact]
    public async Task Reconnect_CancelsPendingTask_AndRefiresLoginReconnect()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "on login\nsay \"back online\"\nend on\n" +
            "on reconnect\nsay \"reconnected\"\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("sess", WithHeader(body));
        Assert.True(run.Success);

        BeaconScheduler? scheduler = engine.GetScheduler("sess");
        Assert.NotNull(scheduler);
        BeaconTask sleeper = scheduler!.StartTask("sleeper", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromSeconds(5));
            return null;
        });

        await Task.Delay(50);
        Assert.Equal(1, scheduler.PendingSleepCount);

        BeaconFireResult refire = await engine.HandleReconnectAsync();
        await sleeper.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BeaconTaskStatus.Cancelled, sleeper.Status);

        Assert.Contains(host.Says, s => s.Contains("back online", StringComparison.Ordinal));
        Assert.Contains(host.Says, s => s.Contains("reconnected", StringComparison.Ordinal));
        Assert.Contains(refire.Handlers, h => string.Equals(h.ScriptId, "sess", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisconnectDropsDispatch_ReconnectRearms()
    {
        var (engine, host, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "drop", WithHeader("on chat\nsay \"heard {message}\"\nend on\n"));
        Assert.True(run.Success);

        engine.HandleDisconnect("test drop");
        BeaconFireResult dropped = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "hi"));
        Assert.Empty(dropped.Handlers.SelectMany(h => h.Result?.LocalEcho ?? []));
        Assert.Empty(host.Says);

        await engine.HandleReconnectAsync();
        BeaconFireResult live = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "hi again"));
        Assert.NotEmpty(live.Handlers);
        Assert.Single(host.Says);
    }

    #endregion
    #region A: saved round-trip across reload with tasks reset

    [Fact]
    public async Task Saved_RoundTripAcrossReload_TasksReset()
    {
        var (engine, host, _) = NewEngine();
        string folder = NewConfigurationsFolder();
        const string body =
            "on join\nset seen to saved(\"seen\") or {}\nset seen[player] to (seen[player] or 0) + 1\nsave \"seen\" to seen\nsay \"visits {seen[player]}\"\nend on\n";

        BeaconRunResult run = await engine.RunScriptAsync("greeter", WithHeader(body), configFolder: folder);
        Assert.True(run.Success);
        Assert.NotNull(engine.SavedState);

        BeaconFireResult first = await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.True(first.Handlers[0].Result!.Success);
        Assert.Contains("visits 1", host.Says[^1]);

        BeaconScheduler? scheduler = engine.GetScheduler("greeter");
        Assert.NotNull(scheduler);
        BeaconTask bg = scheduler!.StartTask("bg", async ctx =>
        {
            await ctx.WaitAsync(TimeSpan.FromMinutes(1));
            return null;
        });
        await Task.Delay(50);
        Assert.Equal(1, scheduler.PendingSleepCount);

        engine.HandleReload("greeter");
        await bg.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BeaconTaskStatus.Cancelled, bg.Status);
        Assert.Empty(scheduler.ListTasks());

        IReadOnlyDictionary<string, BeaconValue> saved = engine.GetSavedSnapshot("greeter");
        Assert.True(saved.TryGetValue("seen", out BeaconValue? seen));
        var map = Assert.IsType<BeaconMapValue>(seen);
        Assert.Equal(1, ((BeaconNumberValue)map.Entries["Steve"]).Value);

        BeaconFireResult second = await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.True(second.Handlers[0].Result!.Success);
        Assert.Contains("visits 2", host.Says[^1]);

        var reloaded = new BeaconSavedState(folder);
        reloaded.Load("greeter");
        Assert.True(reloaded.TryGet("greeter", "seen", out BeaconValue? onDisk));
        var diskMap = Assert.IsType<BeaconMapValue>(onDisk);
        Assert.Equal(2, ((BeaconNumberValue)diskMap.Entries["Steve"]).Value);
    }

    #endregion
    #region A: per-block throttle via two on-blocks same event

    [Fact]
    public async Task TwoOnBlocks_SameEvent_SharedThrottleName_SecondThrottled()
    {
        var (engine, _, clock) = NewEngine();
        const string body =
            "on tps cooldown 60 seconds named \"shared\"\nshow \"first\"\nend on\n" +
            "on tps cooldown 60 seconds named \"shared\"\nshow \"second\"\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("thr", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.Equal(2, fire.Handlers.Count);
        Assert.False(fire.Handlers[0].Throttled);
        Assert.True(fire.Handlers[1].Throttled);
        Assert.Equal(["first"], fire.Handlers[0].Result!.LocalOutput);
        Assert.Single(fire.DebugLog);

        clock.Advance(TimeSpan.FromSeconds(60));
        BeaconFireResult refire = await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(refire.Handlers[0].Throttled);
        Assert.True(refire.Handlers[1].Throttled);
    }

    [Fact]
    public async Task Throttle_ResetsOnReload()
    {
        var (engine, _, _) = NewEngine();
        const string body = "on tps cooldown 60 seconds named \"w\"\nshow \"hit\"\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("thr2", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult first = await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(first.Handlers[0].Throttled);
        BeaconFireResult throttled = await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.True(throttled.Handlers[0].Throttled);

        engine.HandleReload("thr2");
        BeaconFireResult after = await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.False(after.Handlers[0].Throttled);
    }

    #endregion
    #region B: parser addendum (full event.message always works)

    [Fact]
    public async Task ParserAdmits_BareEventMessage_InWhenFilter()
    {
        var (engine, _, _) = NewEngine();
        const string body = "on chat when event.message contains \"!help\"\nshow \"helped\"\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("ev1", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult hit = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "someone said !help here"));
        Assert.Equal(["helped"], hit.Handlers[0].Result!.LocalOutput);
        BeaconFireResult miss = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "just chatting"));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task ParserAdmits_ParenthesizedEventField()
    {
        var (engine, _, _) = NewEngine();
        const string body = "on chat\nshow (event.message contains \"x\")\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("evp", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult hit = await engine.FireEventAsync("chat", BeaconEventFields.Chat("A", "x marks"));
        Assert.Equal(["yes"], hit.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task EvaluatorResolves_ShowEventPlayer()
    {
        var (engine, _, _) = NewEngine();
        const string body = "on join\nshow event.player\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("ev2", WithHeader(body));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("join", BeaconEventFields.Join("Notch"));
        Assert.Equal(["Notch"], fire.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task EvaluatorResolves_BareServerTps()
    {
        var (engine, host, _) = NewEngine();
        host.ServerTpsValue = 19.5;
        BeaconRunResult run = await engine.RunScriptAsync("srv", WithHeader("show server.tps\n"));
        Assert.True(run.Success);
        Assert.Equal(["19.5"], run.LocalOutput);
    }

    [Fact]
    public async Task SharedBracket_ReadsLocalTable()
    {
        var (engine, _, _) = NewEngine();
        const string body = "set shared[\"k\"] to 7\nshow shared[\"k\"]\n";
        BeaconRunResult run = await engine.RunScriptAsync("shr", WithHeader(body));
        Assert.True(run.Success);
        Assert.Equal(["7"], run.LocalOutput);
    }

    #endregion
    #region C: catalog addendum (start + logout)

    [Fact]
    public void Catalog_KnowsStartAndLogout_NoB2001()
    {
        Assert.True(BeaconHookCatalog.IsKnown("start"));
        Assert.True(BeaconHookCatalog.IsKnown("logout"));
        Assert.Contains("start", BeaconHookCatalog.HookListText, StringComparison.Ordinal);
        Assert.Contains("logout", BeaconHookCatalog.HookListText, StringComparison.Ordinal);
        Assert.True(BeaconHookCatalog.TryGetSchema("start", out BeaconHookSchema? start));
        Assert.NotNull(start);
        Assert.Empty(start!.Fields);
        Assert.True(BeaconHookCatalog.TryGetSchema("logout", out BeaconHookSchema? logout));
        Assert.NotNull(logout);
        Assert.Empty(logout!.Fields);
        Assert.Null(BeaconHookCatalog.ValidateHook("start", new SourceSpan("t.bcn", 1, 1, 0)));
        Assert.Null(BeaconHookCatalog.ValidateHook("logout", new SourceSpan("t.bcn", 1, 1, 0)));
    }

    [Fact]
    public async Task OnStart_And_OnLogout_FireViaEngine()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "on start\nsay \"loaded\"\nend on\n" +
            "on logout\nsay \"bye\"\nend on\n";
        BeaconRunResult run = await engine.RunScriptAsync("life", WithHeader(body));
        Assert.True(run.Success);
        Assert.DoesNotContain(run.Diagnostics, d => d.Code == BeaconDiagnosticCodes.UnknownEvent);

        Assert.Contains(host.Says, s => s.Contains("loaded", StringComparison.Ordinal));

        BeaconFireResult logout = await engine.FireEventAsync("logout", BeaconEventFields.Logout());
        Assert.Single(logout.Handlers);
        Assert.Contains(host.Says, s => s.Contains("bye", StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_StartAndLogout_AreNotUnknownHookWarnings()
    {
        var (engine, _, _) = NewEngine();
        engine.LoadSource("probe", "probe.bcn", WithHeader("on start\nshow \"x\"\nend on\n"));
        Assert.DoesNotContain(
            engine.Lint("probe"),
            d => string.Equals(d.Code, BeaconDiagnosticCodes.UnknownEvent, StringComparison.Ordinal));
        engine.LoadSource("probe2", "probe2.bcn", WithHeader("on logout\nshow \"x\"\nend on\n"));
        Assert.DoesNotContain(
            engine.Lint("probe2"),
            d => string.Equals(d.Code, BeaconDiagnosticCodes.UnknownEvent, StringComparison.Ordinal));
    }

    #endregion
    #region snapshots via scheduler copy

    [Fact]
    public async Task FireEvent_SnapshotSurvivesCallerMutation()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "snap", WithHeader("on chat\nshow message\nend on\n"));
        Assert.True(run.Success);

        var fields = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text("A"),
            ["message"] = BeaconValue.Text("before"),
        };
        Task<BeaconFireResult> pending = engine.FireEventAsync("chat", fields);
        fields["message"] = BeaconValue.Text("after");
        BeaconFireResult fire = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["before"], fire.Handlers[0].Result!.LocalOutput);
    }
    #endregion
}

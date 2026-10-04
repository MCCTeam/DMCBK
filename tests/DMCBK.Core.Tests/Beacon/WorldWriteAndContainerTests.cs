using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// World writes and container access: dig/place/raycast behind world.write with gameplay gates and bucket rate limits, container reads first with take/put behind inventory.write.
/// </summary>
public sealed class WorldWriteAndContainerTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private sealed class WritesHost : IBeaconHostServices
    {
        public List<string> Digs { get; } = [];
        public List<string> Places { get; } = [];
        public BeaconRaycastHit? RayHit { get; set; }
        public BeaconContainerInfo? ContainerValue { get; set; }
        public List<int> Takes { get; } = [];
        public List<int> Puts { get; } = [];

        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<BeaconDigResult> DigAsync(int x, int y, int z, string face, CancellationToken ct = default)
        {
            Digs.Add($"{x},{y},{z}:{face}");
            return Task.FromResult(new BeaconDigResult(true, "broken (observed air afterwards)"));
        }

        public Task<bool> PlaceAsync(int x, int y, int z, string face, CancellationToken ct = default)
        {
            Places.Add($"{x},{y},{z}:{face}");
            return Task.FromResult(true);
        }

        public BeaconRaycastHit? Raycast(double maxDistance) => RayHit;
        public BeaconContainerInfo? OpenContainer => ContainerValue;

        public Task<bool> TakeFromContainerAsync(int slot, int count, CancellationToken ct = default)
        {
            Takes.Add(slot);
            return Task.FromResult(true);
        }

        public Task<bool> PutIntoContainerAsync(int slot, CancellationToken ct = default)
        {
            Puts.Add(slot);
            return Task.FromResult(true);
        }
    }

    private static (BeaconEngine Engine, WritesHost Host, VirtualClock Clock) NewEngine(int seed = 19)
    {
        var host = new WritesHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host, clock);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    private static async Task<string> ShowAsync(BeaconEngine engine, string scriptId, string expr)
    {
        BeaconRunResult run = await engine.RunScriptAsync(scriptId, WithHeader($"show {expr}\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        return Assert.Single(run.LocalOutput);
    }

    [Fact]
    public async Task Dig_ReportsBrokenMap()
    {
        var (engine, host, _) = NewEngine();
        string shown = await ShowAsync(engine, "d", "world.dig(100, 63, -30)");
        Assert.Contains("broken: yes", shown, StringComparison.Ordinal);
        Assert.Equal("100,63,-30:up", Assert.Single(host.Digs));
    }

    [Fact]
    public async Task Dig_BadFace_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader("show world.dig(100, 63, -30, \"sideways\")\n"));
        Assert.False(run.Success);
        Assert.Contains("face", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Place_SendsAndReturnsYes()
    {
        var (engine, host, _) = NewEngine();
        Assert.Equal("yes", await ShowAsync(engine, "p", "world.place(100, 63, -30, \"north\")"));
        Assert.Equal("100,63,-30:north", Assert.Single(host.Places));
    }

    [Fact]
    public async Task LookingAt_HitAndMiss()
    {
        var (engine, host, _) = NewEngine();
        host.RayHit = new BeaconRaycastHit(101, 64, -30, "minecraft:stone", 3.5);
        Assert.Equal("minecraft:stone", await ShowAsync(engine, "l", "world.looking_at().name"));
        host.RayHit = null;
        Assert.Equal("none", await ShowAsync(engine, "l2", "world.looking_at()"));
    }

    [Fact]
    public async Task WriteGate_NinthOpRefusesCatchably()
    {
        var (engine, _, _) = NewEngine();
        var script = new System.Text.StringBuilder(WithHeader(string.Empty));
        for (int i = 0; i <= BeaconWorldWriteGate.MaxOperations; i++)
            script.Append("show world.place(100, 63, -30)\n");

        BeaconRunResult run = await engine.RunScriptAsync("g", script.ToString());
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.WorldWriteGate, run.Error?.Code);
    }

    [Fact]
    public async Task WriteGate_SameInterpreter_RefusesPastAllowance()
    {
        var host = new WritesHost();
        var clock = new VirtualClock();
        var interpreter = new BeaconInterpreter("g", "g.mcc", host, clock, new SeededRng(1), new FuelBudget());
        for (int i = 0; i < BeaconWorldWriteGate.MaxOperations; i++)
            interpreter.WriteGate.Acquire(new SourceSpan("g.mcc", 1, 1, 0), "world.place");

        BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(() =>
            interpreter.WriteGate.Acquire(new SourceSpan("g.mcc", 1, 1, 0), "world.place"));
        Assert.Equal(BeaconDiagnosticCodes.WorldWriteGate, ex.Code);
        clock.Advance(TimeSpan.FromSeconds(11));
        interpreter.WriteGate.Acquire(new SourceSpan("g.mcc", 1, 1, 0), "world.place");
    }

    [Fact]
    public async Task WorldWrites_HonorGameplayGates()
    {
        var host = new WritesHost();
        var clock = new VirtualClock();
        var interpreter = new BeaconInterpreter("d", "d.mcc", host, clock, new SeededRng(1), new FuelBudget())
        {
            Gates = new BeaconGameplayGates(Terrain: false),
        };
        BeaconLexResult lexed = BeaconLexer.Lex("d.mcc", WithHeader("show world.dig(1, 2, 3)\n"));
        BeaconHeaderResult header = BeaconHeader.Parse("d.mcc", lexed.NormalizedSource, 2, lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse("d.mcc", lexed.Tokens, header.Major);
        Assert.NotNull(parsed.Script);
        interpreter.BeginDispatch("top-level", seed: 1);
        BeaconRunResult run = await interpreter.RunTopLevelAsync(
            BeaconDesugar.Desugar(parsed.Script), [], CancellationToken.None);
        Assert.False(run.Success);
        Assert.Contains("Gameplay.Terrain", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(host.Digs);
    }

    [Fact]
    public async Task Container_ReadFirst()
    {
        var (engine, host, _) = NewEngine();
        host.ContainerValue = new BeaconContainerInfo(
            3, "Chest", "minecraft:chest",
            [new BeaconInvSlot(0, "minecraft:diamond", "Diamond", 2, null)]);
        Assert.Equal("Chest", await ShowAsync(engine, "c", "inv.container().title"));
        Assert.Equal("minecraft:diamond", await ShowAsync(engine, "c2", "inv.container().slots[0].type"));
    }

    [Fact]
    public async Task Container_NoWindow_SurfacesNone()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("none", await ShowAsync(engine, "c", "inv.container()"));
    }

    [Fact]
    public async Task TakePut_ReachHost()
    {
        var (engine, host, _) = NewEngine();
        Assert.Equal("yes", await ShowAsync(engine, "t", "inv.take(3)"));
        Assert.Equal(3, Assert.Single(host.Takes));
        Assert.Equal("yes", await ShowAsync(engine, "p", "inv.put(10)"));
        Assert.Equal(10, Assert.Single(host.Puts));
    }

    [Fact]
    public async Task WorldWrite_InfersCapability()
    {
        BeaconLexResult lexed = BeaconLexer.Lex(
            "w.mcc", WithHeader("show world.dig(1, 2, 3)\nshow world.place(1, 2, 3)\nshow world.looking_at()\n"));
        BeaconHeaderResult header = BeaconHeader.Parse("w.mcc", lexed.NormalizedSource, 2, lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse("w.mcc", lexed.Tokens, header.Major);
        Assert.NotNull(parsed.Script);
        Assert.Contains(BeaconCapabilities.WorldWrite, BeaconCapabilityInference.Infer(parsed.Script));
    }

    [Fact]
    public async Task PluginChannels_SendStaysInCSharpPlugins()
    {
        // Channel sends live on the C# plugin session scope (ISessionScope.SendPluginMessageAsync), but scripts run on the client host with no script-suitable send API, so no server.send_plugin builtin exists.
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync(
            "p", WithHeader("show server.send_plugin(\"mcc:demo\", \"hi\")\n"));
        Assert.False(run.Success);
        Assert.Contains("send_plugin", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }
}

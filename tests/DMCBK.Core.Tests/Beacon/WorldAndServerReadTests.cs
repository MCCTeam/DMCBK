using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Reads: block search, scoreboard, protocol exposure, and boss bars, headless on a fake.
/// </summary>
public sealed class WorldAndServerReadTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private sealed class ReadsHost : IBeaconHostServices
    {
        public int? ProtocolValue { get; set; }
        public List<BeaconBlockPos> BlocksValue { get; set; } = [];
        public BeaconScoreboard BoardValue { get; set; } = BeaconScoreboard.Empty;
        public List<BeaconBossBarInfo> BarsValue { get; set; } = [];

        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int? GameProtocol => ProtocolValue;

        public Task<IReadOnlyList<BeaconBlockPos>> FindBlocksAsync(
            string nameOrId, int radius, int maxResults, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BeaconBlockPos>>(BlocksValue.Take(maxResults).ToList());

        public BeaconScoreboard Scoreboard => BoardValue;
        public IReadOnlyList<BeaconBossBarInfo> BossBars => BarsValue;
    }

    private static (BeaconEngine Engine, ReadsHost Host, VirtualClock Clock) NewEngine(int seed = 11)
    {
        var host = new ReadsHost();
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
    public async Task FindBlocks_ReturnsPositionsAsMaps()
    {
        var (engine, host, _) = NewEngine();
        host.BlocksValue = [new BeaconBlockPos(10, 64, -3), new BeaconBlockPos(12, 64, -3)];
        string shown = await ShowAsync(engine, "f", "world.find_blocks(\"chest\", 16, 10)");
        Assert.Contains("x: 10", shown, StringComparison.Ordinal);
        Assert.Contains("z: -3", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindBlocks_EmptyMatcher_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader("show world.find_blocks(\"\", 16, 10)\n"));
        Assert.False(run.Success);
        Assert.Contains("block id", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindBlocks_NegativeRadius_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader("show world.find_blocks(\"chest\", -1, 10)\n"));
        Assert.False(run.Success);
        Assert.Contains("non-negative radius", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindBlocks_InfersWorldSearchCapability()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("f.bcn", WithHeader("show world.find_blocks(\"chest\", 16, 10)\n"));
        BeaconHeaderResult header = BeaconHeader.Parse("f.bcn", lexed.NormalizedSource, 2, lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse("f.bcn", lexed.Tokens, header.Major);
        Assert.NotNull(parsed.Script);
        Assert.Contains(BeaconCapabilities.WorldSearch, BeaconCapabilityInference.Infer(parsed.Script));
        Assert.Contains(BeaconCapabilities.WorldSearch, BeaconCapabilities.KnownProviders);
        Assert.Contains(BeaconCapabilities.WorldWrite, BeaconCapabilities.KnownProviders);
    }

    [Fact]
    public async Task Scoreboard_ObjectivesAndTeamsAsMaps()
    {
        var (engine, host, _) = NewEngine();
        host.BoardValue = new BeaconScoreboard(
            [new BeaconObjectiveInfo("kills", "Kills", new Dictionary<string, int> { ["Alice"] = 3 })],
            [new BeaconTeamInfo("red", "Red", ["Alice"])]);
        Assert.Equal("3", await ShowAsync(engine, "s", "server.scoreboard().objectives.kills.scores.Alice"));
        Assert.Equal("Red", await ShowAsync(engine, "s2", "server.scoreboard().teams.red.display"));
    }

    [Fact]
    public async Task Scoreboard_EmptyBoard_EmptyMaps()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("{}", await ShowAsync(engine, "s", "server.scoreboard().objectives"));
    }

    [Fact]
    public async Task Score_Entry_ReturnsNumber()
    {
        var (engine, host, _) = NewEngine();
        host.BoardValue = new BeaconScoreboard(
            [new BeaconObjectiveInfo("kills", "Kills", new Dictionary<string, int> { ["Alice"] = 3 })],
            []);
        Assert.Equal("3", await ShowAsync(engine, "s", "server.score(\"kills\", \"Alice\")"));
    }

    [Fact]
    public async Task Score_MissingEntryOrObjective_ReturnsNone()
    {
        var (engine, host, _) = NewEngine();
        host.BoardValue = new BeaconScoreboard(
            [new BeaconObjectiveInfo("kills", "Kills", new Dictionary<string, int> { ["Alice"] = 3 })],
            []);
        Assert.Equal("none", await ShowAsync(engine, "s", "server.score(\"kills\", \"Bob\")"));
        Assert.Equal("none", await ShowAsync(engine, "s2", "server.score(\"bogus\", \"Alice\")"));
        Assert.Equal("none", await ShowAsync(engine, "s3", "server.score(\"bogus\")"));
    }

    [Fact]
    public async Task Score_WholeObjective_ReturnsScoresMap()
    {
        var (engine, host, _) = NewEngine();
        host.BoardValue = new BeaconScoreboard(
            [new BeaconObjectiveInfo("kills", "Kills", new Dictionary<string, int> { ["Alice"] = 3 })],
            []);
        Assert.Equal("3", await ShowAsync(engine, "s", "server.score(\"kills\").Alice"));
    }

    [Fact]
    public async Task Score_BracketIndex_ReadsPunctuatedNames()
    {
        var (engine, host, _) = NewEngine();
        host.BoardValue = new BeaconScoreboard(
            [new BeaconObjectiveInfo("my obj", "My Obj", new Dictionary<string, int> { ["Alice"] = 3 })],
            []);
        Assert.Equal("3", await ShowAsync(engine, "s", "server.scoreboard().objectives[\"my obj\"].scores[\"Alice\"]"));
    }

    [Fact]
    public async Task Score_WrongArity_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("show server.score()\n"));
        Assert.False(run.Success);
        Assert.Contains("1 or 2", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_NonTextArgs_RefuseReadably()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("show server.score(\"kills\", 4)\n"));
        Assert.False(run.Success);
        Assert.Contains("text", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_SurfacesGameProtocol()
    {
        var (engine, host, _) = NewEngine();
        host.ProtocolValue = 769;
        Assert.Equal("769", await ShowAsync(engine, "p", "server.protocol"));
        Assert.Equal("769", await ShowAsync(engine, "p2", "game.protocol"));
    }

    [Fact]
    public async Task Protocol_Unknown_SurfacesNone()
    {
        var (engine, _, _) = NewEngine();
        Assert.Equal("none", await ShowAsync(engine, "p", "server.protocol"));
    }

    [Fact]
    public async Task Bossbars_ListTitles()
    {
        var (engine, host, _) = NewEngine();
        host.BarsValue = [new BeaconBossBarInfo("Ender Dragon", 0.5, "pink")];
        Assert.Equal("Ender Dragon", await ShowAsync(engine, "b", "server.bossbars()[0].title"));
    }

    [Fact]
    public async Task PresentationReads_TitleActionbar_Unsurfaced_Reported()
    {
        // ChatApi carries chat only: titles, subtitles, and action bars never reach it, so no last_title/actionbar builtin exists.
        // Boss bars flow through PlayerApi instead.
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("p", WithHeader("show last_title()\n"));
        Assert.False(run.Success);
        Assert.Contains("last_title", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }
}

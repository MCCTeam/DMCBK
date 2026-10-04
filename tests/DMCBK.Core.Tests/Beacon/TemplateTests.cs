using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Template tests: every scaffold lints clean and runs on fakes, firing its trigger event and observing the scripted effect through a recording host.
/// </summary>
public sealed class TemplateTests : IDisposable
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
        public List<string> Shows { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];

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
        public double? ServerTps => 20.0;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private static (BeaconEngine Engine, RecordingHost Host) NewEngine(int seed = 1234)
    {
        var host = new RecordingHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(seed), new FuelBudget());
        return (engine, host);
    }

    public static TheoryData<string> TemplateNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in BeaconTemplates.Names)
            data.Add(name);

        return data;
    }

    [Fact]
    public void Templates_CoverTheDocumentedSet()
    {
        Assert.Equal(["empty", "guard", "shop", "welcome"], BeaconTemplates.Names);
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void EveryScaffold_LintsClean(string name)
    {
        string source = BeaconTemplates.Get(name);
        BeaconLintReport report = BeaconLint.LintSource(name + ".mcc", source);
        Assert.Empty(report.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public async Task EveryScaffold_RunsOnFakes(string name)
    {
        var (engine, _) = NewEngine();
        string source = BeaconTemplates.Get(name);
        BeaconRunResult run = await engine.RunScriptAsync(name, source);
        Assert.True(run.Success);
    }

    [Fact]
    public async Task Welcome_GreetsJoiners()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("welcome", BeaconTemplates.Get("welcome"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.True(fire.Handlers.Count > 0);
        Assert.Single(host.Says);
        Assert.Contains("Steve", host.Says[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_WarnsOnLowHealth()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("guard", BeaconTemplates.Get("guard"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("health", BeaconEventFields.Health(5, 20, -2));
        Assert.True(fire.Handlers.Count > 0);
        Assert.Single(host.Says);
    }

    [Fact]
    public async Task Shop_AnswersPriceQuestions()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shop", BeaconTemplates.Get("shop"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync(
            "chat", BeaconEventFields.Chat("Alex", "!price bread"));
        Assert.True(fire.Handlers.Count > 0);
        Assert.Single(host.Says);
        Assert.Contains("bread", host.Says[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_AnnouncesLoadLocally()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("empty", BeaconTemplates.Get("empty"));
        Assert.True(run.Success);
        Assert.Contains(run.LocalOutput, line => line.Contains("Empty script loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void New_WritesScaffold_RefusesOverwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-tmpl-" + Guid.NewGuid().ToString("N"));
        string scripts = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scripts);
        _roots.Add(root);

        Assert.True(BeaconTemplates.TryWriteNew(scripts, "welcome", "mybot", out string path, out _));
        Assert.Equal(Path.Combine(scripts, "mybot.mcc"), path);
        Assert.True(File.Exists(path));

        Assert.False(BeaconTemplates.TryWriteNew(scripts, "welcome", "mybot", out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void New_UnknownTemplate_Fails()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        Assert.False(BeaconTemplates.TryWriteNew(root, "nope", "x", out _, out string error));
        Assert.Contains("welcome", error, StringComparison.Ordinal);
    }
}

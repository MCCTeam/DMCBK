using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Chest flow headless runs plus capability goldens: the Storage-sign find, open, and take path degrades gracefully offline (none and empty route to fallback branches), and lint infers the three verbs to their caps fail-closed.
/// </summary>
public sealed class SignUseFlowTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private sealed class ChestHost : IBeaconHostServices
    {
        public List<BeaconSignInfo> SignsValue { get; set; } = [];
        public string? SignTextValue { get; set; }
        public BeaconContainerInfo? ContainerValue { get; set; }
        public List<int> Takes { get; } = [];

        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public string? GetSignText(int x, int y, int z) => SignTextValue;

        public Task<IReadOnlyList<BeaconSignInfo>> FindSignsAsync(
            string needle, int radius, int maxResults, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BeaconSignInfo>>(SignsValue.Take(maxResults).ToList());

        public Task<BeaconContainerInfo?> OpenContainerAtAsync(int x, int y, int z, CancellationToken ct = default)
            => Task.FromResult(ContainerValue);

        public BeaconContainerInfo? OpenContainer => ContainerValue;

        public Task<bool> TakeFromContainerAsync(int slot, int count, CancellationToken ct = default)
        {
            Takes.Add(slot);
            return Task.FromResult(true);
        }
    }

    private static (BeaconEngine Engine, ChestHost Host) NewEngine(int seed = 77)
    {
        var host = new ChestHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    private static async Task<string> ShowAsync(BeaconEngine engine, string scriptId, string expr)
    {
        BeaconRunResult run = await engine.RunScriptAsync(scriptId, WithHeader($"show {expr}\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        return Assert.Single(run.LocalOutput);
    }

    [Fact]
    public async Task SignText_HitAndMiss()
    {
        var (engine, host) = NewEngine();
        host.SignTextValue = "Storage\ndiamonds";
        Assert.Equal("Storage\ndiamonds", await ShowAsync(engine, "s", "world.sign_text(10, 64, -3)"));
        host.SignTextValue = null;
        Assert.Equal("none", await ShowAsync(engine, "s2", "world.sign_text(10, 64, -3)"));
    }

    [Fact]
    public async Task FindSigns_ReturnsPositionsWithText()
    {
        var (engine, host) = NewEngine();
        host.SignsValue = [new BeaconSignInfo(10, 64, -3, "Storage")];
        string shown = await ShowAsync(engine, "f", "world.find_signs(\"Storage\", 32, 5)");
        Assert.Contains("x: 10", shown, StringComparison.Ordinal);
        Assert.Contains("Storage", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_HitAndMiss()
    {
        var (engine, host) = NewEngine();
        host.ContainerValue = new BeaconContainerInfo(
            3, "Chest", "minecraft:chest",
            [new BeaconInvSlot(0, "minecraft:diamond", "Diamond", 5, null)]);
        Assert.Equal("Chest", await ShowAsync(engine, "u", "world.use(10, 63, -3).title"));
        host.ContainerValue = null;
        Assert.Equal("none", await ShowAsync(engine, "u2", "world.use(10, 63, -3)"));
    }

    [Fact]
    public async Task ChestFlow_OfflineDegradesToFallbacks()
    {
        var engine = new BeaconEngine(BeaconOfflineHost.Shared, new VirtualClock(), new SeededRng(7), new FuelBudget());
        engine.Variables = new VariableStore();
        const string body =
            "set signs to world.find_signs(\"Storage\", 32, 5)\n" +
            "if signs is empty\n" +
            "  show \"no signs\"\n" +
            "end if\n" +
            "set t to world.sign_text(10, 64, -3)\n" +
            "if t is none\n" +
            "  show \"no text\"\n" +
            "end if\n" +
            "set c to world.use(10, 63, -3)\n" +
            "if c is none\n" +
            "  show \"no container\"\n" +
            "else\n" +
            "  show inv.take(0, 2)\n" +
            "end if\n";
        BeaconRunResult run = await engine.RunScriptAsync("chest", WithHeader(body));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Contains("no signs", run.LocalOutput, StringComparer.Ordinal);
        Assert.Contains("no text", run.LocalOutput, StringComparer.Ordinal);
        Assert.Contains("no container", run.LocalOutput, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ChestFlow_TakePath_ReachesHost()
    {
        var (engine, host) = NewEngine();
        host.SignsValue = [new BeaconSignInfo(10, 64, -3, "Storage")];
        host.SignTextValue = "Storage";
        host.ContainerValue = new BeaconContainerInfo(
            3, "Chest", "minecraft:chest",
            [new BeaconInvSlot(0, "minecraft:diamond", "Diamond", 5, null)]);
        const string body =
            "set signs to world.find_signs(\"Storage\", 32, 5)\n" +
            "set t to world.sign_text(signs[0].x, signs[0].y, signs[0].z)\n" +
            "set c to world.use(signs[0].x, 63, signs[0].z)\n" +
            "show inv.take(0, 2)\n";
        BeaconRunResult run = await engine.RunScriptAsync("chest", WithHeader(body));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(0, Assert.Single(host.Takes));
        Assert.Contains("yes", run.LocalOutput, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SignText_BadCoords_RefusesReadably()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("show world.sign_text(1.5, 64, -3)\n"));
        Assert.False(run.Success);
        Assert.Contains("whole number", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSigns_EmptyNeedle_RefusesReadably()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader("show world.find_signs(\"\", 32, 5)\n"));
        Assert.False(run.Success);
        Assert.Contains("needle", run.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindSigns_NegativeRadius_RefusesReadably()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("f", WithHeader("show world.find_signs(\"Storage\", -1, 5)\n"));
        Assert.False(run.Success);
        Assert.Contains("non-negative radius", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_BadCoords_RefusesReadably()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("u", WithHeader("show world.use(1, 2.5, 3)\n"));
        Assert.False(run.Success);
        Assert.Contains("whole number", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_SignText_InfersWorldRead()
    {
        BeaconLintReport report = BeaconLint.LintSource(
            "s.bcn", WithHeader("show world.sign_text(10, 64, -3)\n"));
        Assert.Contains(BeaconCapabilities.WorldRead, report.Permissions);
    }

    [Fact]
    public void Lint_FindSigns_InfersWorldSearch()
    {
        BeaconLintReport report = BeaconLint.LintSource(
            "f.bcn", WithHeader("show world.find_signs(\"Storage\", 32, 5)\n"));
        Assert.Contains(BeaconCapabilities.WorldSearch, report.Permissions);
    }

    [Fact]
    public void Lint_Use_InfersWorldWrite()
    {
        BeaconLintReport report = BeaconLint.LintSource(
            "u.bcn", WithHeader("show world.use(10, 63, -3)\n"));
        Assert.Contains(BeaconCapabilities.WorldWrite, report.Permissions);
    }

    [Fact]
    public void Lint_UseWithoutWrite_ManifestFailsClosed()
    {
        BeaconLintReport report = BeaconLint.LintSource(
            "u.bcn", "# beacon 1\n# needs: world.read\n\nshow world.use(10, 63, -3)\n");
        Assert.False(report.Ok);
        BeaconDiagnostic error = Assert.Single(
            report.Diagnostics, d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("world.write", error.Message, StringComparison.Ordinal);
        Assert.Contains("world.write", error.Suggestion ?? string.Empty, StringComparison.Ordinal);
    }
}

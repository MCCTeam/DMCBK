using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Plugin variable namespaces: registration, script reads, six-kind marshaling, capability inference, lint resolution, duplicate-claim errors, and unload withdrawal.
/// </summary>
public sealed class PluginVariableNamespaceTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private static (BeaconEngine Engine, ScriptTestHost Host, VirtualClock Clock) NewEngine(int seed = 17)
    {
        var host = new ScriptTestHost();
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
    public void Register_DuplicateClaim_NamesBothPlugins()
    {
        var (engine, _, _) = NewEngine();
        using var first = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ-a", "econ.read", "Economy balances.", _ => new Dictionary<string, object?> { ["balance"] = 5 }));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
                "coins", "econ-b", "econ.read", "Economy balances.", _ => new Dictionary<string, object?> { ["balance"] = 6 })));
        Assert.Contains("econ-a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("econ-b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ReturnsSnapshotMap()
    {
        var (engine, _, _) = NewEngine();
        using var _ = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ", "econ.read", "Economy balances.",
            _ => new Dictionary<string, object?> { ["balance"] = 5, ["rich"] = true }));
        Assert.Equal("5", await ShowAsync(engine, "s", "coins.balance"));
        Assert.Equal("yes", await ShowAsync(engine, "s2", "coins.rich"));
    }

    [Fact]
    public async Task Read_RefreshesPerRead()
    {
        var (engine, _, _) = NewEngine();
        int calls = 0;
        using var _ = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ", "econ.read", "Economy balances.",
            _ => new Dictionary<string, object?> { ["n"] = ++calls }));
        Assert.Equal("1", await ShowAsync(engine, "s", "coins.n"));
        Assert.Equal("2", await ShowAsync(engine, "s2", "coins.n"));
    }

    [Fact]
    public async Task Read_NonMap_FailsCatchably()
    {
        var (engine, _, _) = NewEngine();
        using var _ = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ", "econ.read", "Economy balances.", _ => 5));
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("show coins\n"));
        Assert.False(run.Success);
        Assert.Contains("map", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_RefusesReadably()
    {
        var (engine, _, _) = NewEngine();
        using var _ = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ", "econ.read", "Economy balances.",
            _ => new Dictionary<string, object?> { ["balance"] = 5 }));
        BeaconRunResult run = await engine.RunScriptAsync("s", WithHeader("set coins.balance to 9\n"));
        Assert.False(run.Success);
        Assert.Contains("read-only", run.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inference_MemberRead_ContributesCapability()
    {
        BeaconProviders.OfferVariable("econ", "coins", "econ.read");
        BeaconLexResult lexed = BeaconLexer.Lex("s.bcn", WithHeader("show coins.balance\n"));
        BeaconHeaderResult header = BeaconHeader.Parse("s.bcn", lexed.NormalizedSource, 2, lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse("s.bcn", lexed.Tokens, header.Major);
        Assert.NotNull(parsed.Script);
        Assert.Contains("econ.read", BeaconCapabilityInference.Infer(parsed.Script));
        await Task.CompletedTask;
    }

    [Fact]
    public void Lint_ResolvesVariableOfferingPlugin()
    {
        BeaconProviders.OfferVariable("econ", "coins", "econ.read");
        Assert.True(BeaconProviders.TryGetVariable("coins", out string plugin, out string capability));
        Assert.Equal("econ", plugin);
        Assert.Equal("econ.read", capability);
    }

    [Fact]
    public void Withdraw_RemovesNamespaceAndOffer()
    {
        var (engine, _, _) = NewEngine();
        IDisposable handle = engine.Bridge.RegisterVariable(new BeaconVariableRegistration(
            "coins", "econ", "econ.read", "Economy balances.",
            _ => new Dictionary<string, object?> { ["balance"] = 5 }));
        Assert.True(engine.Bridge.TryGetVariable("coins", out _));
        handle.Dispose();
        Assert.False(engine.Bridge.TryGetVariable("coins", out _));
        BeaconProviders.WithdrawPlugin("econ");
        Assert.False(BeaconProviders.TryGetVariable("coins", out _, out _));
    }
}

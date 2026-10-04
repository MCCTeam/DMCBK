using DMCBK.Core.Beacon;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Plugin event upgrade: suppressible flag, capability, pre-engine queue, and lint.
/// </summary>
public sealed class PluginEventUpgradeTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task RegisterEvent_SuppressibleFlag_Suppresses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        using IDisposable environmentScope = engine.Environment.Enter();
        using (engine.Bridge.RegisterEvent("shop", "shop_buy_upg", ["player"], "A sale.", suppressible: true))
        {
            Assert.True(BeaconHookCatalog.IsSuppressible("shop_buy_upg"));

            BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "clerk",
                "on shop_buy_upg\nstop event\nend on\n");
            Assert.True(run.Success);

            BeaconFireResult fire = await engine.Bridge.FireEventAsync(
                "shop_buy_upg",
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["player"] = BeaconValue.Text("Steve"),
                });
            Assert.True(fire.Suppressed);
        }

        Assert.False(BeaconHookCatalog.IsKnown("shop_buy_upg"));
    }

    [Fact]
    public async Task RegisterEvent_Default_StaysNonSuppressible()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        using (engine.Bridge.RegisterEvent("shop", "shop_sale_upg", ["player"], "A sale."))
        {
            Assert.False(BeaconHookCatalog.IsSuppressible("shop_sale_upg"));

            BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "clerk",
                "on shop_sale_upg\nstop event\nend on\n");
            Assert.True(run.Success);

            BeaconFireResult fire = await engine.Bridge.FireEventAsync(
                "shop_sale_upg",
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["player"] = BeaconValue.Text("Steve"),
                });
            Assert.False(fire.Suppressed);
            Assert.NotEmpty(fire.SuppressionNotes);
        }
    }

    [Fact]
    public async Task RegisterEvent_Capability_JoinsLintPermissions()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        using IDisposable environmentScope = engine.Environment.Enter();
        using (engine.Bridge.RegisterEvent("shop", "shop_deal_upg", ["player"], "A deal.", capability: "econ.read"))
        {
            Assert.True(BeaconProviders.TryGetEvent("shop_deal_upg", out string owner, out string? capability));
            Assert.Equal("shop", owner);
            Assert.Equal("econ.read", capability);

            BeaconLintReport report = BeaconLint.LintSource(
                "deal.mcc", "# beacon 1\n# needs: econ.read\non shop_deal_upg\nshow player\nend on\n");
            Assert.True(report.Ok);
            Assert.Contains("econ.read", report.Permissions);
        }
    }

    [Fact]
    public void DuplicateEventClaim_StillNamesBothPlugins()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        using (engine.Bridge.RegisterEvent("shop", "shop_clash_upg", [], "d."))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Bridge.RegisterEvent("market", "shop_clash_upg", [], "d."));
            Assert.Contains("shop_clash_upg", ex.Message);
            Assert.Contains("shop", ex.Message);
            Assert.Contains("market", ex.Message);
        }
    }

    [Fact]
    public async Task SdkHost_QueuesEventPreEngine_ThenFlushes()
    {
        await using var client = new DMCBK.Core.ClientBuilder()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new NullHostForEvents())
            .UseCommands().UseBeacon().Build();
        var beacon = new PluginBeaconHost("shop", client);
        try
        {
            using (beacon.RegisterEvent("shop_early_upg", ["player"], "Early sale.", suppressible: true, capability: "econ.read"))
            {
                var host = new ScriptTestHost();
                var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(5), new FuelBudget());
                using IDisposable environmentScope = engine.Environment.Enter();
                engine.Variables = new DMCBK.Core.Commands.VariableStore();
                BeaconEngineWiring.Bind(client, engine);

                BeaconRunResult run = await engine.RunScriptAsync("buyer", "# beacon 1\non shop_early_upg\nsay \"hi\"\nend on\n");
                Assert.True(run.Success, run.Error?.Message ?? "load failed");

                BeaconFireResult fire = await beacon.FireEventAsync(
                    "shop_early_upg",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["player"] = "Steve" });
                Assert.True(BeaconHookCatalog.IsSuppressible("shop_early_upg"));
                Assert.Single(fire.Handlers);
            }
        }
        finally
        {
            beacon.DisposeAll();
        }
    }

    private sealed class NullHostForEvents : DMCBK.Core.IHostInterface
    {
        public DMCBK.Core.IUserPrompt? Prompt => null;
        public Umpk.Auth.IAuthInteraction? AuthInteraction => null;
        public DMCBK.Core.Commands.ICommandOutput? CommandOutput => null;
        public DMCBK.Core.Commands.IHostUi? Ui => null;
    }
}

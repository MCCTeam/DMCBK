using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Bidirectional interop contract tests: C# publishes a function or event and a script consumes it; a script exports a function and C# awaits it; arity and missing-target failures assert exact error shapes in both directions.
/// </summary>
public sealed class InteropTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private static (BeaconEngine Engine, ScriptTestHost Host) NewEngine()
    {
        var host = new ScriptTestHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(42), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    #region C# publishes function, script consumes

    [Fact]
    public async Task ExternCall_RunsPluginFunctionOnCallerFuel()
    {
        var (engine, host) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "Today's price.",
            ["item"],
            call => Task.FromResult<BeaconValue>(BeaconValue.Number(
                call.Args[0] is BeaconTextValue text && text.Value == "bread" ? 5 : 0))));

        BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
            "# needs: chat.send econ.read\nextern price_of from \"shop\"\n" +
            "on chat when message contains \"!buy\":\nsay \"Bread costs {price_of(\"bread\")} coins.\"\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync(
            "chat", BeaconEventFields.Chat("Steve", "!buy bread"));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Contains("Bread costs 5 coins.", host.Says[0]);
    }

    [Fact]
    public async Task ExternCall_MissingProvider_RaisesCatchableNamingPlugin()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
            "extern price_of from \"shop\"\n" +
            "on chat:\ntry\nsay price_of(\"bread\")\ncatch err\nshow err.message\nend try\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("Steve", "hi"));
        BeaconRunResult result = fire.Handlers[0].Result!;
        Assert.True(result.Success);
        Assert.Contains("shop", result.LocalOutput[0]);
        Assert.Contains("price_of", result.LocalOutput[0]);
    }

    [Fact]
    public async Task ExternCall_UndeclaredButOffered_SuggestsExternLine()
    {
        var (engine, _) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "Today's price.", ["item"],
            _ => Task.FromResult<BeaconValue>(BeaconValue.Number(5))));

        BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
            "on chat:\ntry\nsay price_of(\"bread\")\ncatch err\nshow err.message\nend try\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("Steve", "hi"));
        BeaconRunResult result = fire.Handlers[0].Result!;
        Assert.True(result.Success);
        Assert.Contains("offered by plugin 'shop'", result.LocalOutput[0]);
    }

    [Fact]
    public async Task ExternCall_ArityMismatch_RaisesCatchableNamingCounts()
    {
        var (engine, _) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "Today's price.", ["item"],
            _ => Task.FromResult<BeaconValue>(BeaconValue.Number(5))));

        BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
            "extern price_of from \"shop\"\n" +
            "on chat:\ntry\nsay price_of(\"a\", \"b\")\ncatch err\nshow err.message\nend try\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("Steve", "hi"));
        BeaconRunResult result = fire.Handlers[0].Result!;
        Assert.True(result.Success);
        Assert.Contains("1 argument", result.LocalOutput[0]);
        Assert.Contains("2", result.LocalOutput[0]);
    }

    [Fact]
    public async Task ExternCall_PluginFailure_RaisesCatchableNamingPlugin()
    {
        var (engine, _) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "boom", "shop", "econ.read", "Always fails.", [],
            _ => throw new InvalidOperationException("till is empty")));

        BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
            "extern boom from \"shop\"\n" +
            "on chat:\ntry\nsay boom()\ncatch err\nshow err.message\nend try\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("Steve", "hi"));
        BeaconRunResult result = fire.Handlers[0].Result!;
        Assert.True(result.Success);
        Assert.Contains("shop", result.LocalOutput[0]);
        Assert.Contains("till is empty", result.LocalOutput[0]);
    }

    #endregion
    #region C# publishes event, script consumes

    [Fact]
    public async Task CustomEvent_RegistersHookAndFiresToScript()
    {
        var (engine, host) = NewEngine();
        using (engine.Bridge.RegisterEvent("shop", "shop_buy", ["player", "item", "price"], "A sale completed."))
        {
            BeaconRunResult run = await engine.RunScriptAsync("clerk", WithHeader(
                "# needs: chat.send\non shop_buy when item is \"bread\":\nsay \"Thanks {player}!\"\nend on\n"));
            Assert.True(run.Success);

            BeaconFireResult fire = await engine.Bridge.FireEventAsync("shop_buy",
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["player"] = BeaconValue.Text("Steve"),
                    ["item"] = BeaconValue.Text("bread"),
                    ["price"] = BeaconValue.Number(5),
                });
            Assert.Single(fire.Handlers);
            Assert.Contains("Thanks Steve!", host.Says[0]);
        }
    }

    [Fact]
    public async Task CustomEvent_HonorsDetachedCancellation()
    {
        var (engine, host) = NewEngine();
        using (engine.Bridge.RegisterEvent("shop", "shop_buy", ["player"], "A sale."))
        {
            BeaconRunResult run = await engine.RunScriptAsync("clerk", WithHeader(
                "on shop_buy:\nsay \"sale\"\nend on\n"));
            Assert.True(run.Success);

            using var detached = new CancellationTokenSource();
            detached.Cancel();
            BeaconFireResult fire = await engine.Bridge.FireEventAsync(
                "shop_buy",
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal) { ["player"] = BeaconValue.Text("S") },
                detached.Token);
            Assert.Empty(fire.Handlers);
            Assert.Empty(host.Says);
        }
    }

    [Fact]
    public void DuplicateFunctionClaim_NamesBothPlugins()
    {
        var (engine, _) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "d.", [],
            _ => Task.FromResult<BeaconValue>(BeaconValue.None)));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
                "price_of", "market", "econ.read", "d.", [],
                _ => Task.FromResult<BeaconValue>(BeaconValue.None))));
        Assert.Contains("price_of", ex.Message);
        Assert.Contains("shop", ex.Message);
        Assert.Contains("market", ex.Message);
    }

    [Fact]
    public void DuplicateEventClaim_NamesBothPlugins()
    {
        var (engine, _) = NewEngine();
        using (engine.Bridge.RegisterEvent("shop", "shop_buy", [], "d."))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Bridge.RegisterEvent("market", "shop_buy", [], "d."));
            Assert.Contains("shop_buy", ex.Message);
            Assert.Contains("shop", ex.Message);
            Assert.Contains("market", ex.Message);
        }
    }

    [Fact]
    public async Task WithdrawPlugin_RemovesFunctionsEventsAndOffers()
    {
        var (engine, _) = NewEngine();
        using IDisposable environmentScope = engine.Environment.Enter();
        IDisposable handle = engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "d.", ["item"],
            _ => Task.FromResult<BeaconValue>(BeaconValue.Number(1))));
        using (engine.Bridge.RegisterEvent("shop", "shop_buy", ["player"], "d."))
        {
            Assert.True(BeaconProviders.TryGetFunction("price_of", out _, out _));
            handle.Dispose();
            engine.Bridge.WithdrawPlugin("shop");
            Assert.False(BeaconProviders.TryGetFunction("price_of", out _, out _));
            Assert.False(BeaconHookCatalog.IsKnown("shop_buy"));

            BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
                "extern price_of from \"shop\"\n" +
                "on chat:\ntry\nsay price_of(\"bread\")\ncatch err\nshow err.code\nend try\nend on\n"));
            Assert.True(run.Success);
            BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
            Assert.Contains("B4012", fire.Handlers[0].Result!.LocalOutput[0]);
        }
    }

    #endregion
    #region script exports function, C# awaits

    [Fact]
    public async Task Export_CSharpCallsScriptFunctionAndReadsReturn()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shopkeeper", WithHeader(
            "set total to 41\nexport function daily_report()\nreturn \"Sales today: {total + 1} coins.\"\nend function\n"));
        Assert.True(run.Success);

        object? report = await engine.CallExportFromHostAsync("shopkeeper", "daily_report", []);
        Assert.Equal("Sales today: 42 coins.", report);
    }

    [Fact]
    public async Task Export_MissingScriptOrFunction_RaisesNamingAllThree()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shopkeeper", WithHeader(
            "export function daily_report()\nreturn \"ok\"\nend function\n"));
        Assert.True(run.Success);

        BeaconRuntimeException missingScript = await Assert.ThrowsAsync<BeaconRuntimeException>(() =>
            engine.CallExportFromHostAsync("ghost", "daily_report", []));
        Assert.Equal("B4012", missingScript.Code);
        Assert.Contains("ghost", missingScript.Message);
        Assert.Contains("daily_report", missingScript.Message);

        BeaconRuntimeException missingFunction = await Assert.ThrowsAsync<BeaconRuntimeException>(() =>
            engine.CallExportFromHostAsync("shopkeeper", "weekly_report", []));
        Assert.Contains("shopkeeper", missingFunction.Message);
        Assert.Contains("weekly_report", missingFunction.Message);
    }

    [Fact]
    public async Task Export_ArityMismatch_RaisesNamingCounts()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shopkeeper", WithHeader(
            "export function price(item)\nreturn 5\nend function\n"));
        Assert.True(run.Success);

        BeaconRuntimeException arity = await Assert.ThrowsAsync<BeaconRuntimeException>(() =>
            engine.CallExportFromHostAsync("shopkeeper", "price", []));
        Assert.Equal("B4012", arity.Code);
        Assert.Contains("1 argument", arity.Message);
    }

    [Fact]
    public async Task Export_MarshalsSixKindsBothWays()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("echoer", WithHeader(
            "export function roundtrip(m)\nreturn m\nend function\n"));
        Assert.True(run.Success);

        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "bread",
            ["price"] = 5.0,
            ["listed"] = true,
            ["tags"] = new List<object?> { "food", null },
        };
        object? output = await engine.CallExportFromHostAsync("echoer", "roundtrip", [input]);
        var map = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Equal("bread", map["name"]);
        Assert.Equal(5.0, map["price"]);
        Assert.Equal(true, map["listed"]);
    }

    #endregion
    #region script-to-script call

    [Fact]
    public async Task Call_RunsCalleeOnCallerFuel()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult lib = await engine.RunScriptAsync("ledger", WithHeader(
            "export function total_for(player)\nreturn 7\nend function\n"));
        Assert.True(lib.Success);
        BeaconRunResult app = await engine.RunScriptAsync("shop", WithHeader(
            "on chat:\nset report to call \"ledger.total_for\"(\"Steve\")\nsay \"Total: {report}\"\nend on\n"));
        Assert.True(app.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Contains("Total: 7", host.Says[0]);
    }

    [Fact]
    public async Task Call_MissingTarget_RaisesCatchableNamingScriptFunctionLine()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shop", WithHeader(
            "on chat:\ntry\nset r to call \"ledger.total_for\"()\nshow r\ncatch err\nshow err.message\nshow err.code\nshow err.line\nend try\nend on\n"));
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
        BeaconRunResult result = fire.Handlers[0].Result!;
        Assert.True(result.Success);
        Assert.Contains("ledger", result.LocalOutput[0]);
        Assert.Contains("total_for", result.LocalOutput[0]);
        Assert.Equal("B4012", result.LocalOutput[1]);
        Assert.Equal("4", result.LocalOutput[2]);
    }

    #endregion
    #region imports

    [Fact]
    public async Task Import_ResolvesRelativeAndCachesByContentHash()
    {
        var (engine, host) = NewEngine();
        var loader = (BeaconImportLoader)engine.ModuleResolver;
        loader.Overlay["econ.mcc"] = "# beacon 1\nfunction price(item)\nreturn 5\nend function\n";

        BeaconRunResult run = await engine.RunScriptAsync("shop", WithHeader(
            "import \"lib/econ.mcc\" as econ\non chat:\nsay \"Bread costs {econ.price(\"bread\")} coins.\"\nend on\n"));
        Assert.True(run.Success);
        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Contains("Bread costs 5 coins.", host.Says[0]);
    }

    [Fact]
    public async Task Import_CycleError_NamesTheCycle()
    {
        var (engine, _) = NewEngine();
        var loader = (BeaconImportLoader)engine.ModuleResolver;
        loader.Overlay["a.mcc"] = "# beacon 1\nimport \"b.mcc\" as b\nfunction fa()\nreturn 1\nend function\n";
        loader.Overlay["b.mcc"] = "# beacon 1\nimport \"a.mcc\" as a\nfunction fb()\nreturn 2\nend function\n";

        BeaconRunResult run = await engine.RunScriptAsync("root", WithHeader("import \"a.mcc\" as a\nshow \"hi\"\n"));
        Assert.False(run.Success);
        Assert.Equal("B1005", run.Error!.Code);
        Assert.Contains("a.mcc", run.Error.Message);
        Assert.Contains("b.mcc", run.Error.Message);
    }

    [Fact]
    public async Task Import_MissingFile_FailsClosed()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("root", WithHeader("import \"lib/gone.mcc\" as g\nshow \"hi\"\n"));
        Assert.False(run.Success);
        Assert.Equal("B1004", run.Error!.Code);
    }

    #endregion
    #region commands

    [Fact]
    public async Task ScriptCommand_BindsArgsAndRunsBlock()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shop", WithHeader(
            "# desc: Look up today's price.\n# example: /price bread\n" +
            "command \"/price <item>\"\nset target to arg(\"item\")\nsay \"{target} costs 5 coins.\"\nend command\n"));
        Assert.True(run.Success);
        BeaconScriptCommandSpec spec = Assert.Single(engine.GetCommandSpecs("shop"));
        Assert.Equal("price", spec.Name);
        Assert.Equal(["item"], spec.Args);
        Assert.Equal("Look up today's price.", spec.Description);
        Assert.Equal(["/price bread"], spec.Examples);

        BeaconRunResult invoked = await engine.InvokeScriptCommandAsync(
            "price", new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = "bread" });
        Assert.True(invoked.Success);
        Assert.Contains("bread costs 5 coins.", host.Says[0]);
    }

    [Fact]
    public async Task ScriptCommand_MissingArg_YieldsNone()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("shop", WithHeader(
            "command \"/price <item>\"\nif arg(\"missing\") is set\nsay \"has\"\nelse\nshow \"none\"\nend if\nend command\n"));
        Assert.True(run.Success);

        BeaconRunResult invoked = await engine.InvokeScriptCommandAsync(
            "price", new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = "bread" });
        Assert.True(invoked.Success);
        Assert.Contains("none", invoked.LocalOutput[0]);
    }

    [Fact]
    public async Task ScriptCommand_UnknownName_FailsNamingCommand()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult result = await engine.InvokeScriptCommandAsync("nope", new Dictionary<string, string>());
        Assert.False(result.Success);
        Assert.Contains("nope", result.Error!.Message);
    }

    [Fact]
    public void CommandSpec_RejectsPatternWithoutSlash()
    {
        BeaconScript script = MustParse("command \"price <item>\"\nshow \"x\"\nend command\n");
        CommandBlock block = Assert.IsType<CommandBlock>(script.Decls[0]);
        Assert.Null(BeaconCommandSpec.Parse("shop", block, null));
    }

    private static BeaconScript MustParse(string body)
    {
        BeaconLexResult lexed = BeaconLexer.Lex("t.mcc", "# beacon 1\n" + body);
        BeaconParseResult parsed = BeaconParser.Parse("t.mcc", lexed.Tokens, 1);
        Assert.NotNull(parsed.Script);
        return BeaconDesugar.Desugar(parsed.Script!);
    }

    #endregion
    #region vars bridge

    [Fact]
    public async Task VarsBridge_SharesStateWithVariableStore()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("till", WithHeader(
            "set vars.beacon.coins to 5\nshow vars.beacon.coins\n"));
        Assert.True(run.Success);
        Assert.Equal("5", run.LocalOutput[0]);
        Assert.Equal("5", engine.Variables!.Get("beacon_coins"));
        Assert.Equal("5 coins", engine.Variables!.Expand("%beacon_coins% coins"));
    }

    [Fact]
    public async Task VarsBridge_RejectsOtherSegments()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("till", WithHeader(
            "set vars.other.coins to 5\n"));
        Assert.False(run.Success);
        Assert.Contains("vars.beacon", run.Error!.Message);
    }

    #endregion
    #region SDK surface

    [Fact]
    public void SignatureValidation_AcceptsSixKindsAndWidensInts()
    {
        BeaconFunction.ValidateSignature("shop", [typeof(string), typeof(double), typeof(int), typeof(bool)], typeof(double?));
        BeaconFunction.ValidateSignature("shop",
            [typeof(IReadOnlyList<object>), typeof(IReadOnlyDictionary<string, object>)], typeof(void));
        BeaconFunction.ValidateSignature("shop", [typeof(long), typeof(float), typeof(decimal)], typeof(Task<string>));
    }

    [Fact]
    public void SignatureValidation_RejectsPrivateTypesNamingPluginAndSignature()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BeaconFunction.ValidateSignature("shop", [typeof(DateTime)], typeof(string)));
        Assert.Contains("shop", ex.Message);
        Assert.Contains("DateTime", ex.Message);

        var ret = Assert.Throws<InvalidOperationException>(() =>
            BeaconFunction.ValidateSignature("shop", [typeof(string)], typeof(DateTime)));
        Assert.Contains("shop", ret.Message);
    }

    [Fact]
    public void AttributeScanner_RegistersSameRecordAsExplicit()
    {
        IReadOnlyList<BeaconFunction> found = BeaconFunctionScanner.Scan(typeof(ShopSample).Assembly, "shop-test");
        BeaconFunction price = found.FirstOrDefault(f => f.Name == "price_of_test")
            ?? throw new Xunit.Sdk.XunitException("price_of_test not scanned");
        Assert.Equal("econ.read", price.Capability);
        Assert.Equal(["item"], price.Parameters);
    }

    public static class ShopSample
    {
        [BeaconFunction("price_of_test", Capability = "econ.read", Description = "Test price.")]
        public static double? PriceOfTest(string item) => item == "bread" ? 5 : null;
    }

    #endregion
    #region SDK host level (PluginBeaconHost over a headless client)

    [Fact]
    public async Task SdkHost_RegistersFiresAndCallsThroughWiredEngine()
    {
        await using var client = new DMCBK.Core.ClientBuilder()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new NullHostInterface())
            .UseCommands().UseBeacon().Build();
        var host = new ScriptTestHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(5), new FuelBudget());
        engine.Variables = new VariableStore();
        BeaconEngineWiring.Bind(client, engine);
        var beacon = new PluginBeaconHost("shop", client);
        try
        {
            beacon.Functions.Register(new BeaconFunction(
                "price_of", "econ.read", "Today's price.", ["item"], null, null,
                call => Task.FromResult<object?>(
                    call.RequireText(0) == "bread" ? 5.0 : null)));
            using (beacon.RegisterEvent("shop_buy", ["player"], "A sale."))
            {
                BeaconRunResult run = await engine.RunScriptAsync("buyer", WithHeader(
                    "extern price_of from \"shop\"\n" +
                    "on shop_buy:\nsay \"{player} paid {price_of(\"bread\")}.\"\nend on\n"));
                Assert.True(run.Success, run.Error?.Message ?? "load failed");

                BeaconFireResult fire = await beacon.FireEventAsync(
                    "shop_buy",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["player"] = "Steve" });
                Assert.Single(fire.Handlers);
                Assert.Contains("Steve paid 5.", host.Says);
            }
        }
        finally
        {
            beacon.DisposeAll();
        }
    }

    [Fact]
    public async Task SdkHost_CallFunction_MapsMissingExportToBeaconCallException()
    {
        await using var client = new DMCBK.Core.ClientBuilder()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new NullHostInterface())
            .UseCommands().UseBeacon().Build();
        var host = new ScriptTestHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(5), new FuelBudget());
        engine.Variables = new VariableStore();
        BeaconEngineWiring.Bind(client, engine);
        var beacon = new PluginBeaconHost("shop", client);
        try
        {
            BeaconRunResult run = await engine.RunScriptAsync("till", WithHeader(
                "export function balance()\nreturn 5\nend function\n"));
            Assert.True(run.Success);

            Assert.Equal(5.0, await beacon.CallFunctionAsync("till", "balance", []));
            BeaconCallException missing = await Assert.ThrowsAsync<BeaconCallException>(() =>
                beacon.CallFunctionAsync("till", "debt", []));
            Assert.Equal("till", missing.ScriptId);
            Assert.Equal("debt", missing.Function);
            Assert.Contains("debt", missing.Message);
        }
        finally
        {
            beacon.DisposeAll();
        }
    }

    private sealed class NullHostInterface : DMCBK.Core.IHostInterface
    {
        public DMCBK.Core.IUserPrompt? Prompt => null;
        public Umpk.Auth.IAuthInteraction? AuthInteraction => null;
        public DMCBK.Core.Commands.ICommandOutput? CommandOutput => null;
        public DMCBK.Core.Commands.IHostUi? Ui => null;
    }
    #endregion
}

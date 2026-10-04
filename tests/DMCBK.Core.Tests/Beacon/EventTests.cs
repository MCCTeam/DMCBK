using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Events: all 15 hooks fire from fake doubles with documented fields, the filter matrix (contains/is/comparison over event+globals), <c>as</c>-alias binding, stop-event suppression (plus the non-suppressible no-op note), B2001 unknown-hook warnings with the full catalog, and snapshot immutability across <c>wait</c>.
/// </summary>
public sealed class EventTests
{
    private sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
        public string? SelfNameValue { get; set; } = "Tester";
        public List<string> OnlinePlayersValue { get; set; } = ["Alice", "Bob"];
        public double? ServerTpsValue { get; set; } = 20.0;
        public string? ThrowWhenSayContains { get; set; }

        public Task SayAsync(string text, CancellationToken ct = default)
        {
            if (ThrowWhenSayContains is not null
                && text.Contains(ThrowWhenSayContains, StringComparison.Ordinal))
                throw new InvalidOperationException("Live transport failed mid-handler.");

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

        public string? SelfName => SelfNameValue;
        public IReadOnlyList<string> OnlinePlayers(int limit) => OnlinePlayersValue.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static List<OnBlock> ParseOnBlocks(string scriptId, string body)
    {
        BeaconLexResult lexed = BeaconLexer.Lex(scriptId + ".bcn", "# beacon 1\n" + body);
        BeaconParseResult parsed = BeaconParser.Parse(scriptId + ".bcn", lexed.Tokens, 1);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.NotNull(parsed.Script);
        BeaconScript script = BeaconDesugar.Desugar(parsed.Script!);
        return script.Decls.OfType<OnBlock>().ToList();
    }

    private static async Task<(BeaconInterpreter Interpreter, List<OnBlock> Blocks)> LoadAsync(
        string scriptId, string body, RecordingHost host, VirtualClock clock)
    {
        BeaconLexResult lexed = BeaconLexer.Lex(scriptId + ".bcn", "# beacon 1\n" + body);
        BeaconParseResult parsed = BeaconParser.Parse(scriptId + ".bcn", lexed.Tokens, 1);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.NotNull(parsed.Script);
        BeaconScript script = BeaconDesugar.Desugar(parsed.Script!);
        var interpreter = new BeaconInterpreter(scriptId, scriptId + ".bcn", host, clock, new SeededRng(42), new FuelBudget());
        BeaconRunResult top = await interpreter.RunTopLevelAsync(script, lexed.Comments);
        Assert.True(top.Success);
        return (interpreter, script.Decls.OfType<OnBlock>().ToList());
    }

    private static BeaconEventBus NewBus(
        VirtualClock clock, IReadOnlyDictionary<string, BeaconInterpreter> interpreters)
    {
        BeaconEventInvoker invoker = (sid, block, snap, ct) => interpreters[sid].InvokeHandlerAsync(block, snap, ct);
        return new BeaconEventBus(invoker, clock);
    }

    private static async Task<IReadOnlyList<string>> FireShowAsync(
        string scriptId, string hook, string body, IReadOnlyDictionary<string, BeaconValue> fields)
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(scriptId, body, host, clock);
        Assert.All(blocks, b => Assert.Equal(hook, b.EventName));
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { [scriptId] = interpreter });
        bus.RegisterScriptHandlers(scriptId, blocks, out IReadOnlyList<BeaconDiagnostic> warnings);
        Assert.Empty(warnings);
        BeaconFireResult fire = await bus.FireEventAsync(hook, fields);
        Assert.False(fire.Handlers[0].Throttled);
        BeaconRunResult? result = fire.Handlers[0].Result;
        Assert.NotNull(result);
        Assert.True(result!.Success);
        return result.LocalOutput;
    }

    #region all 15 hooks fire with documented fields

    [Fact]
    public async Task Hook_Chat_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-chat", "chat",
            "on chat\nshow \"{player}|{message}|{raw}|{is_private}\"\nend on\n",
            BeaconEventFields.Chat("Steve", "hi", "hi", false));
        Assert.Equal(["Steve|hi|hi|no"], output);
    }

    [Fact]
    public async Task Hook_Whisper_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-whisper", "whisper",
            "on whisper\nshow \"{player}|{message}\"\nend on\n",
            BeaconEventFields.Whisper("Alex", "psst"));
        Assert.Equal(["Alex|psst"], output);
    }

    [Fact]
    public async Task Hook_ServerMessage_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-sys", "server_message",
            "on server_message\nshow \"{text}|{translation_key}\"\nend on\n",
            BeaconEventFields.ServerMessage("Server restarts soon", "multiplayer.disconnect.server_shutdown"));
        Assert.Equal(["Server restarts soon|multiplayer.disconnect.server_shutdown"], output);
    }

    [Fact]
    public async Task Hook_ServerMessage_PlainText_HasNoKey()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-sysplain", "server_message",
            "on server_message\nshow (translation_key is not set)\nend on\n",
            BeaconEventFields.ServerMessage("Just text"));
        Assert.Equal(["yes"], output);
    }

    [Fact]
    public async Task Hook_Join_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-join", "join",
            "on join\nshow \"welcome {player}\"\nend on\n",
            BeaconEventFields.Join("Notch"));
        Assert.Equal(["welcome Notch"], output);
    }

    [Fact]
    public async Task Hook_Leave_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-leave", "leave",
            "on leave\nshow \"bye {player}\"\nend on\n",
            BeaconEventFields.Leave("Notch"));
        Assert.Equal(["bye Notch"], output);
    }

    [Fact]
    public async Task Hook_Death_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-death", "death",
            "on death\nshow \"{player} died: {cause}\"\nend on\n",
            BeaconEventFields.Death("Steve", "fell from a high place"));
        Assert.Equal(["Steve died: fell from a high place"], output);
    }

    [Fact]
    public async Task Hook_Respawn_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-respawn", "respawn",
            "on respawn\nshow \"back {player}\"\nend on\n",
            BeaconEventFields.Respawn("Steve"));
        Assert.Equal(["back Steve"], output);
    }

    [Fact]
    public async Task Hook_Health_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-health", "health",
            "on health\nshow \"{health}/{max_health}/{change}\"\nend on\n",
            BeaconEventFields.Health(5, 20, -3));
        Assert.Equal(["5/20/-3"], output);
    }

    [Fact]
    public async Task Hook_Hunger_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-hunger", "hunger",
            "on hunger\nshow \"{food}/{saturation}/{change}\"\nend on\n",
            BeaconEventFields.Hunger(14, 5, -2));
        Assert.Equal(["14/5/-2"], output);
    }

    [Fact]
    public async Task Hook_Inventory_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-inv", "inventory",
            "on inventory\nshow len(slots_changed)\nend on\n",
            BeaconEventFields.Inventory([1, 5, 9]));
        Assert.Equal(["3"], output);
    }

    [Fact]
    public async Task Hook_Login_FiresWithNoFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-login", "login",
            "on login\nshow \"online\"\nend on\n",
            BeaconEventFields.Login());
        Assert.Equal(["online"], output);
    }

    [Fact]
    public async Task Hook_Disconnect_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-disc", "disconnect",
            "on disconnect\nshow \"lost: {reason}\"\nend on\n",
            BeaconEventFields.Disconnect("timed out"));
        Assert.Equal(["lost: timed out"], output);
    }

    [Fact]
    public async Task Hook_Reconnect_FiresWithNoFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-recon", "reconnect",
            "on reconnect\nshow \"back\"\nend on\n",
            BeaconEventFields.Reconnect());
        Assert.Equal(["back"], output);
    }

    [Fact]
    public async Task Hook_Kick_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-kick", "kick",
            "on kick\nshow \"kicked: {reason}\"\nend on\n",
            BeaconEventFields.Kick("griefing"));
        Assert.Equal(["kicked: griefing"], output);
    }

    [Fact]
    public async Task Hook_Tps_FiresWithDocumentedFields()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-tps", "tps",
            "on tps\nshow \"{tps}|{mspt}\"\nend on\n",
            BeaconEventFields.Tps(19.5, 42));
        Assert.Equal(["19.5|42"], output);
    }

    [Fact]
    public async Task Hook_Tps_NullMeansUnknown_AsNone()
    {
        IReadOnlyList<string> output = await FireShowAsync(
            "ev-tpsnull", "tps",
            "on tps\nshow (tps is not set)\nshow (mspt is not set)\nend on\n",
            BeaconEventFields.Tps(null, null));
        Assert.Equal(["yes", "yes"], output);
    }

    #endregion
    #region filter matrix: ordinary boolean expressions

    [Fact]
    public async Task Filter_Contains_FiresAndSkips()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-contains", "on chat when message contains \"!help\"\nshow \"helped\"\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-contains"] = interpreter });
        bus.RegisterScriptHandlers("f-contains", blocks, out _);

        BeaconFireResult hit = await bus.FireEventAsync("chat", BeaconEventFields.Chat("A", "someone said !help here"));
        Assert.Equal(["helped"], hit.Handlers[0].Result!.LocalOutput);

        BeaconFireResult miss = await bus.FireEventAsync("chat", BeaconEventFields.Chat("A", "just chatting"));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task Filter_Is_PlayerName()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-is", "on join when player is \"Notch\"\nshow \"notch!\"\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-is"] = interpreter });
        bus.RegisterScriptHandlers("f-is", blocks, out _);

        BeaconFireResult hit = await bus.FireEventAsync("join", BeaconEventFields.Join("Notch"));
        Assert.Equal(["notch!"], hit.Handlers[0].Result!.LocalOutput);

        BeaconFireResult miss = await bus.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task Filter_Comparison_Health()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-cmp", "on health when health < 6\nshow \"low\"\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-cmp"] = interpreter });
        bus.RegisterScriptHandlers("f-cmp", blocks, out _);

        BeaconFireResult hit = await bus.FireEventAsync("health", BeaconEventFields.Health(5, 20, -2));
        Assert.Equal(["low"], hit.Handlers[0].Result!.LocalOutput);

        BeaconFireResult miss = await bus.FireEventAsync("health", BeaconEventFields.Health(18, 20, 1));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task Filter_EventPlusGlobal()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-global", "set watch to \"Notch\"\non join when player is watch\nshow \"seen\"\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-global"] = interpreter });
        bus.RegisterScriptHandlers("f-global", blocks, out _);

        BeaconFireResult hit = await bus.FireEventAsync("join", BeaconEventFields.Join("Notch"));
        Assert.Equal(["seen"], hit.Handlers[0].Result!.LocalOutput);

        BeaconFireResult miss = await bus.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task Filter_BareFieldShadowsGlobal()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-shadow",
            "set message to \"hello from global\"\non chat when message contains \"hello\"\nshow message\nend on\n",
            host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-shadow"] = interpreter });
        bus.RegisterScriptHandlers("f-shadow", blocks, out _);

        // The global contains "hello" but the event does not: event fields win, so this skips.
        BeaconFireResult miss = await bus.FireEventAsync("chat", BeaconEventFields.Chat("A", "goodbye"));
        Assert.Empty(miss.Handlers[0].Result!.LocalOutput);

        // The event value (not the global) is what the body sees.
        BeaconFireResult hit = await bus.FireEventAsync("chat", BeaconEventFields.Chat("A", "say hello"));
        Assert.Equal(["say hello"], hit.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public async Task Filter_FullEventForm_AlwaysWorks()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        // NOTE: the `event` map is only reachable inside interpolation holes today.
        // A bare leading `event.message` is B0001 in the parser.
        // Holes call ParseExpr directly with no CanStartExpr gate, so this pins the one working `event.` path.
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "f-full",
            "on chat when \"{event.message}\" contains \"!help\"\nshow \"{event.player}\"\nend on\n",
            host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["f-full"] = interpreter });
        bus.RegisterScriptHandlers("f-full", blocks, out _);

        BeaconFireResult hit = await bus.FireEventAsync("chat", BeaconEventFields.Chat("Alex", "!help me"));
        Assert.Equal(["Alex"], hit.Handlers[0].Result!.LocalOutput);
    }

    [Fact]
    public void Lint_EmptyWhen_IsStaticError()
    {
        var engine = new BeaconEngine(new RecordingHost(), new VirtualClock(), new SeededRng(1), new FuelBudget());
        engine.LoadSource("probe", "probe.bcn", "# beacon 1\non chat when\nsay \"hi\"\nend on\n");
        IReadOnlyList<BeaconDiagnostic> errors = engine.Lint("probe")
            .Where(d => d.Severity == BeaconSeverity.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, d => string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal));
    }

    #endregion
    #region as-alias binding

    [Fact]
    public async Task Alias_AsEBindsEventMap()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "al-e", "on chat as e\nshow e.message\nshow \"{event.message}\"\nshow message\nend on\n", host, clock);
        Assert.Equal("e", blocks[0].Alias);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["al-e"] = interpreter });
        bus.RegisterScriptHandlers("al-e", blocks, out _);

        BeaconFireResult fire = await bus.FireEventAsync("chat", BeaconEventFields.Chat("A", "hi"));
        Assert.Equal(["hi", "hi", "hi"], fire.Handlers[0].Result!.LocalOutput);
    }

    #endregion
    #region cancellation: stop event

    [Fact]
    public async Task StopEvent_SuppressesChat_ModerationScenario()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string body =
            "on chat when message contains \"badword\"\nwhisper player \"Please keep chat friendly.\"\nstop event\nend on\n";
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync("mod", body, host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["mod"] = interpreter });
        bus.RegisterScriptHandlers("mod", blocks, out _);

        BeaconFireResult bad = await bus.FireEventAsync("chat", BeaconEventFields.Chat("Griefer", "this has a badword in it"));
        Assert.True(bad.Suppressed);
        Assert.True(bad.Handlers[0].Result!.EventSuppressed);
        Assert.Single(host.Whispers);
        Assert.Equal("Griefer", host.Whispers[0].Player);

        BeaconFireResult clean = await bus.FireEventAsync("chat", BeaconEventFields.Chat("Friend", "hello all"));
        Assert.False(clean.Suppressed);
        Assert.Single(host.Whispers);
    }

    [Fact]
    public async Task CancelEvent_ForgivenForm_SuppressesLikeStopEvent()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        const string body =
            "on chat when message contains \"badword\"\nwhisper player \"hey\"\ncancel event\nend on\n";
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync("cancelalias", body, host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["cancelalias"] = interpreter });
        bus.RegisterScriptHandlers("cancelalias", blocks, out _);

        BeaconFireResult bad = await bus.FireEventAsync("chat", BeaconEventFields.Chat("Griefer", "badword here"));
        Assert.True(bad.Suppressed);
        Assert.True(bad.Handlers[0].Result!.EventSuppressed);
        Assert.Single(host.Whispers);
    }

    [Fact]
    public async Task StopEvent_NonSuppressible_IsNoOpWithNote_NeverSilent()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "noop", "on health when health < 6\nshow \"ouch\"\nstop event\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["noop"] = interpreter });
        bus.RegisterScriptHandlers("noop", blocks, out _);

        BeaconFireResult fire = await bus.FireEventAsync("health", BeaconEventFields.Health(5, 20, -2));
        Assert.False(fire.Suppressed);
        Assert.Single(fire.SuppressionNotes);
        Assert.Contains("health", fire.SuppressionNotes[0], StringComparison.Ordinal);
        Assert.Contains("no-op", fire.SuppressionNotes[0], StringComparison.Ordinal);
        Assert.Equal(["ouch"], fire.Handlers[0].Result!.LocalOutput);
    }

    #endregion
    #region unknown hooks: B2001-as-warning with full catalog

    [Fact]
    public void UnknownHook_RegisterWarns_WithFullCatalogListing()
    {
        List<OnBlock> blocks = ParseOnBlocks("unk", "on frobnicate\nshow \"x\"\nend on\n");
        var bus = new BeaconEventBus(
            (sid, block, snap, ct) => Task.FromResult(new BeaconRunResult(true, null, [], [], [], [], null)),
            new VirtualClock());
        try
        {
            BeaconDiagnostic? warning = bus.RegisterHandler("unk", blocks[0]);
            Assert.NotNull(warning);
            Assert.Equal(BeaconDiagnosticCodes.UnknownEvent, warning!.Code);
            Assert.Equal(BeaconSeverity.Warning, warning.Severity);
            Assert.Contains("frobnicate", warning.Message, StringComparison.Ordinal);
            Assert.Contains(BeaconHookCatalog.HookListText, warning.Message, StringComparison.Ordinal);
            Assert.Contains("C# plugins", warning.Message, StringComparison.Ordinal);
            Assert.Equal(1, bus.HandlerCount);
        }
        finally
        {
            bus.Clear();
        }
    }

    [Fact]
    public async Task CustomHook_Extensible_ForPhase10()
    {
        BeaconHookCatalog.RegisterCustomHook("shop_buy");
        try
        {
            var host = new RecordingHost();
            var clock = new VirtualClock();
            (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
                "shop", "on shop_buy\nshow \"bought {item}\"\nend on\n", host, clock);
            var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["shop"] = interpreter });
            try
            {
                bus.RegisterScriptHandlers("shop", blocks, out IReadOnlyList<BeaconDiagnostic> warnings);
                Assert.Empty(warnings);
                var fields = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["item"] = BeaconValue.Text("bread"),
                };
                BeaconFireResult fire = await bus.FireEventAsync("shop_buy", fields);
                Assert.Equal(["bought bread"], fire.Handlers[0].Result!.LocalOutput);
            }
            finally
            {
                bus.Clear();
            }
        }
        finally
        {
            BeaconHookCatalog.UnregisterCustomHook("shop_buy");
        }
    }

    #endregion
    #region snapshot immutability

    [Fact]
    public async Task Snapshot_DefensiveCopy_SurvivesSourceMutation()
    {
        IReadOnlyDictionary<string, BeaconValue>? captured = null;
        var bus = new BeaconEventBus(
            (sid, block, snap, ct) =>
            {
                captured = snap;
                return Task.FromResult(new BeaconRunResult(true, null, [], [], [], [], null));
            },
            new VirtualClock());
        List<OnBlock> blocks = ParseOnBlocks("snap", "on inventory\nshow \"x\"\nend on\n");
        bus.RegisterScriptHandlers("snap", blocks, out _);
        try
        {
            var items = new List<BeaconValue> { BeaconValue.Number(1), BeaconValue.Number(2) };
            var meta = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["title"] = BeaconValue.Text("chest"),
            };
            var fields = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["slots_changed"] = BeaconValue.List(items),
                ["meta"] = BeaconValue.Map(meta),
                ["note"] = BeaconValue.Text("before"),
            };

            BeaconFireResult fire = await bus.FireEventAsync("inventory", fields);
            Assert.Single(fire.Handlers);

            // Mutate the source payload after dispatch: the handler snapshot must not move.
            items.Add(BeaconValue.Number(3));
            meta["title"] = BeaconValue.Text("furnace");
            meta["extra"] = BeaconValue.Text("new");
            fields["note"] = BeaconValue.Text("after");

            Assert.NotNull(captured);
            var snapList = Assert.IsType<BeaconListValue>(captured!["slots_changed"]);
            Assert.Equal(2, snapList.Items.Count);
            var snapMap = Assert.IsType<BeaconMapValue>(captured!["meta"]);
            Assert.Equal("chest", Assert.IsType<BeaconTextValue>(snapMap.Entries["title"]).Value);
            Assert.False(snapMap.Entries.ContainsKey("extra"));
            Assert.Equal("before", Assert.IsType<BeaconTextValue>(captured!["note"]).Value);
        }
        finally
        {
            bus.Clear();
        }
    }

    [Fact]
    public async Task Snapshot_Immutable_AcrossWait()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter interpreter, List<OnBlock> blocks) = await LoadAsync(
            "snap-wait", "on chat\nwait 1 second\nshow message\nend on\n", host, clock);
        var bus = NewBus(clock, new Dictionary<string, BeaconInterpreter> { ["snap-wait"] = interpreter });
        bus.RegisterScriptHandlers("snap-wait", blocks, out _);

        var fields = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text("A"),
            ["message"] = BeaconValue.Text("before"),
        };
        Task<BeaconFireResult> task = bus.FireEventAsync("chat", fields);

        // Mutate the world mid-handler while it waits: the dispatch snapshot must hold.
        fields["message"] = BeaconValue.Text("after");
        clock.Advance(TimeSpan.FromSeconds(1));
        BeaconFireResult fire = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["before"], fire.Handlers[0].Result!.LocalOutput);
    }

    #endregion
    #region multi-script attribution

    [Fact]
    public async Task TwoScripts_FiringAtOnce_AttributeByScriptId()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        (BeaconInterpreter first, List<OnBlock> firstBlocks) = await LoadAsync(
            "s1", "on join\nshow \"one saw {player}\"\nend on\n", host, clock);
        (BeaconInterpreter second, List<OnBlock> secondBlocks) = await LoadAsync(
            "s2", "on join\nshow \"two saw {player}\"\nend on\n", host, clock);
        var interpreters = new Dictionary<string, BeaconInterpreter> { ["s1"] = first, ["s2"] = second };
        var bus = NewBus(clock, interpreters);
        bus.RegisterScriptHandlers("s1", firstBlocks, out _);
        bus.RegisterScriptHandlers("s2", secondBlocks, out _);

        BeaconFireResult fire = await bus.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.Equal(2, fire.Handlers.Count);
        Assert.Equal(["s1", "s2"], fire.Handlers.Select(h => h.ScriptId).ToList());
        Assert.Equal(["one saw Steve"], fire.Handlers[0].Result!.LocalOutput);
        Assert.Equal(["two saw Steve"], fire.Handlers[1].Result!.LocalOutput);
    }

    [Fact]
    public async Task HandlerThrow_DoesNotBreakSiblingsOrSubscription()
    {
        var host = new RecordingHost { ThrowWhenSayContains = "boom" };
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(5), new FuelBudget());
        BeaconRunResult a = await engine.RunScriptAsync("a", "# beacon 1\non chat\nsay \"boom\"\nend on\n");
        Assert.True(a.Success, a.Error?.Message ?? "run failed");
        BeaconRunResult b = await engine.RunScriptAsync("b", "# beacon 1\non chat\nsay \"calm\"\nend on\n");
        Assert.True(b.Success, b.Error?.Message ?? "run failed");

        BeaconFireResult fire = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
        Assert.Equal(2, fire.Handlers.Count);
        BeaconRunResult? first = fire.Handlers[0].Result;
        Assert.NotNull(first);
        Assert.False(first!.Success);
        Assert.Contains(first.Diagnostics, d => string.Equals(d.Code, "B4002", StringComparison.Ordinal));
        BeaconRunResult? second = fire.Handlers[1].Result;
        Assert.NotNull(second);
        Assert.True(second!.Success);
        Assert.Contains("calm", host.Says);

        BeaconFireResult again = await engine.FireEventAsync("chat", BeaconEventFields.Chat("S", "hi"));
        Assert.Equal(2, again.Handlers.Count);
        Assert.True(again.Handlers[1].Result!.Success);
    }
    #endregion
}

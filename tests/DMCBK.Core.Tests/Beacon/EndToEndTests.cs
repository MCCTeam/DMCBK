using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Seven examples as executable tests: greeter, totem-guard, bedtime-farmer, shopkeeper (import plus extern plus /price plus ledger), TPS-guard, quiz-night, and auction-sniper against a fake allowlisted endpoint.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
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

    private static (BeaconEngine Engine, ScriptTestHost Host, VirtualClock Clock) NewEngine(
        DateTimeOffset? start = null, int seed = 11)
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock(start);
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host, clock);
    }

    private string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static async Task PollForAsync(Func<bool> done, string what)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Fail($"Timed out waiting for {what}.");

            await Task.Delay(20);
        }
    }

    #region 1. greeter that remembers

    [Fact]
    public async Task Greeter_RemembersSeenPlayers()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "# beacon 1\n# needs: chat.send\n\n" +
            "on join\n" +
            "  set seen to saved(\"seen\") or {}\n" +
            "  if seen[player] is set\n" +
            "    say \"Welcome back, {player}! Visit {seen[player]} times and counting.\"\n" +
            "    set seen[player] to seen[player] + 1\n" +
            "  else\n" +
            "    say \"Welcome for the first time, {player}! Type !rules to start.\"\n" +
            "    set seen[player] to 1\n" +
            "  end if\n" +
            "  save \"seen\" to seen\n" +
            "end on\n";
        BeaconRunResult run = await engine.RunScriptAsync("greeter", body);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        await engine.FireEventAsync("join", BeaconEventFields.Join("Steve"));
        Assert.Equal(2, host.Says.Count);
        Assert.Contains("first time", host.Says[0]);
        Assert.Contains("Welcome back, Steve!", host.Says[1]);
    }

    #endregion
    #region 2. totem guard

    [Fact]
    public async Task TotemGuard_EquipsTotemWhenHurt()
    {
        var (engine, host, _) = NewEngine();
        host.Slots =
        [
            new BeaconInvSlot(3, "minecraft:totem_of_undying", "Totem of Undying", 1, null),
            new BeaconInvSlot(4, "minecraft:cooked_beef", "Steak", 2, null),
        ];
        const string body =
            "# beacon 1\n# needs: chat.send inventory.read inventory.write\n\n" +
            "on health when health <= 6\n" +
            "  if inv.has(\"totem_of_undying\", 1)\n" +
            "    inv.move(inv.find(\"totem_of_undying\"), \"offhand\")\n" +
            "    say \"Totem equipped. That was close.\"\n" +
            "  else\n" +
            "    say \"Low health and no totem! Logging out would be smart.\"\n" +
            "  end if\n" +
            "end on\n" +
            "on hunger when food <= 6\n" +
            "  eat_best()\n" +
            "end on\n" +
            "function eat_best()\n" +
            "  if inv.has(\"cooked_beef\", 1)\n" +
            "    inv.select(inv.find(\"cooked_beef\"))\n" +
            "    use_in_hand()\n" +
            "  end if\n" +
            "end function\n";
        BeaconRunResult run = await engine.RunScriptAsync("totem", body);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        await engine.FireEventAsync("health", BeaconEventFields.Health(5, 20, -4));
        Assert.Contains("Totem equipped. That was close.", host.Says);
        Assert.Equal((3, "offhand"), Assert.Single(host.MoveCalls));

        await engine.FireEventAsync("hunger", BeaconEventFields.Hunger(5, 6, -1));
        Assert.Single(host.SelectCalls);
        Assert.Contains(host.Moves, m => m == "use");
    }

    [Fact]
    public async Task TotemGuard_WarnsWhenNoTotem()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "# beacon 1\n# needs: chat.send inventory.read inventory.write\n\n" +
            "on health when health <= 6\n" +
            "  if inv.has(\"totem_of_undying\", 1)\n" +
            "    say \"Totem equipped.\"\n" +
            "  else\n" +
            "    say \"Low health and no totem! Logging out would be smart.\"\n" +
            "  end if\n" +
            "end on\n";
        BeaconRunResult run = await engine.RunScriptAsync("totem", body);
        Assert.True(run.Success);

        await engine.FireEventAsync("health", BeaconEventFields.Health(4, 20, -2));
        Assert.Contains("Low health and no totem! Logging out would be smart.", host.Says);
    }

    #endregion
    #region 3. bedtime farmer

    [Fact]
    public async Task BedtimeFarmer_HeadsToBedAtNight()
    {
        DateTimeOffset night = new(2026, 9, 9, 23, 30, 0, TimeSpan.Zero);
        var (engine, host, clock) = NewEngine(start: night);
        host.PositionValue = new BeaconPosition(100, 55, -30, null, null);
        const string body =
            "# beacon 1\n# needs: chat.send movement\n\n" +
            "every 60 seconds\n" +
            "  if time.hour >= 22 or time.hour < 6\n" +
            "    if me.pos.y < 60\n" +
            "      say \"Night shift over, heading to bed. Back at sunrise.\"\n" +
            "      start go_sleep()\n" +
            "    end if\n" +
            "  end if\n" +
            "end every\n" +
            "function go_sleep()\n" +
            "  move_goto(120, 65, -40)\n" +
            "  say \"Goodnight.\"\n" +
            "end function\n";
        BeaconRunResult run = await engine.RunScriptAsync("farmer", body);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        clock.Advance(TimeSpan.FromSeconds(61));
        IReadOnlyList<BeaconEveryRun> runs = await engine.TickEveryAsync();
        Assert.Single(runs);
        Assert.True(runs[0].Result.Success, runs[0].Result.Error?.Message ?? "every failed");
        await PollForAsync(() => host.Says.Contains("Goodnight."), "the sleep task");
        Assert.Contains("Night shift over, heading to bed. Back at sunrise.", host.Says);
    }

    #endregion
    #region 4. shopkeeper flagship

    [Fact]
    public async Task Shopkeeper_ImportExternCommandAndLedger()
    {
        string root = NewTempRoot();
        string scripts = Path.Combine(root, "scripts");
        string lib = Path.Combine(scripts, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "econ.bcn"),
            "# beacon 1\n" +
            "function price(item)\n" +
            "  if item is \"bread\"\n" +
            "    return 5\n" +
            "  end if\n" +
            "  return none\n" +
            "end function\n" +
            "function credit(player, item)\n" +
            "  return \"{player} earned {price(item)} coins for {item}.\"\n" +
            "end function\n");
        string shopPath = Path.Combine(scripts, "shopkeeper.bcn");
        File.WriteAllText(shopPath,
            "# beacon 1\n# needs: chat.send econ.read\n" +
            "import \"lib/econ.bcn\" as econ\n" +
            "extern price_of from \"shop\"\n" +
            "command \"/price <item>\"\n" +
            "  set target to arg(\"item\")\n" +
            "  set p to econ.price(target)\n" +
            "  if p is set\n" +
            "    say \"{target} costs {p} coins today.\"\n" +
            "  else\n" +
            "    say \"Sorry, I do not buy {target}.\"\n" +
            "  end if\n" +
            "end command\n" +
            "export function daily_report()\n" +
            "  return \"Sales today: {saved(\"sales_total\") or 0} coins.\"\n" +
            "end function\n" +
            "on chat when message starts with \"!sell \"\n" +
            "  set item to trim(slice(message, 6))\n" +
            "  set p to econ.price(item)\n" +
            "  if p is set\n" +
            "    say \"Sold! {econ.credit(player, item)}\"\n" +
            "    set ledger to saved(\"ledger\") or {}\n" +
            "    set ledger[player] to (ledger[player] or 0) + p\n" +
            "    save \"ledger\" to ledger\n" +
            "  end if\n" +
            "end on\n");

        var (engine, host, _) = NewEngine();
        engine.Bridge.RegisterFunction(new BeaconExtensionFunction(
            "price_of", "shop", "econ.read", "Today's buy price.",
            ["item"],
            call => Task.FromResult<BeaconValue>(
                call.Args[0] is BeaconTextValue text && text.Value == "bread"
                    ? BeaconValue.Number(5)
                    : BeaconValue.None)));

        string source = await File.ReadAllTextAsync(shopPath);
        BeaconRunResult run = await engine.RunScriptAsync("shopkeeper", source, fileName: shopPath);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        BeaconRunResult priced = await engine.InvokeScriptCommandAsync(
            "price", new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = "bread" });
        Assert.True(priced.Success, priced.Error?.Message ?? "command failed");
        Assert.Contains("bread costs 5 coins today.", host.Says);

        BeaconFireResult fire = await engine.FireEventAsync(
            "chat", BeaconEventFields.Chat("Steve", "!sell bread"));
        Assert.True(fire.Handlers[0].Result!.Success);
        Assert.Contains("Sold! Steve earned 5 coins for bread.", host.Says);

        object? report = await engine.CallExportFromHostAsync("shopkeeper", "daily_report", []);
        Assert.Equal("Sales today: 0 coins.", report);

        IReadOnlyDictionary<string, BeaconValue> saved = engine.GetSavedSnapshot("shopkeeper");
        Assert.True(saved.TryGetValue("ledger", out BeaconValue? ledger) && ledger is BeaconMapValue map
            && map.Entries.TryGetValue("Steve", out BeaconValue? total)
            && total is BeaconNumberValue number && number.Value == 5);
    }

    #endregion
    #region 5. TPS guard

    [Fact]
    public async Task TpsGuard_WarnsOncePerCooldownWindow()
    {
        var (engine, host, clock) = NewEngine();
        const string body =
            "# beacon 1\n# needs: chat.send\n\n" +
            "on tps cooldown 300 seconds named \"tps-warn\" when tps < 15\n" +
            "  say \"Heads up: server TPS is {tps} ({server.mspt} ms/tick). Easy on the farms.\"\n" +
            "end on\n";
        BeaconRunResult run = await engine.RunScriptAsync("tpsguard", body);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        host.ServerMsptValue = 90.0;
        await engine.FireEventAsync("tps", BeaconEventFields.Tps(10, 90));
        Assert.Single(host.Says);

        await engine.FireEventAsync("tps", BeaconEventFields.Tps(9, 95));
        Assert.Single(host.Says);

        clock.Advance(TimeSpan.FromSeconds(301));
        await engine.FireEventAsync("tps", BeaconEventFields.Tps(8, 100));
        Assert.Equal(2, host.Says.Count);
    }

    #endregion
    #region 6. quiz night

    [Fact]
    public async Task QuizNight_RunsAFullRound()
    {
        var (engine, host, _) = NewEngine();
        const string body =
            "# beacon 1\n# needs: chat.send\n\n" +
            "set quiz to {running: no, q: \"\", a: \"\", wins: {}}\n" +
            "on chat when message is \"!quiz\"\n" +
            "  if quiz.running is yes\n" +
            "    whisper player \"A round is already running: {quiz.q}\"\n" +
            "    stop event\n" +
            "  end if\n" +
            "  set quiz.running to yes\n" +
            "  set quiz.q to \"What mob explodes when it gets close?\"\n" +
            "  set quiz.a to \"creeper\"\n" +
            "  say \"Quiz! {quiz.q} First correct whisper wins.\"\n" +
            "end on\n" +
            "on whisper when message contains quiz.a and quiz.running is yes\n" +
            "  set quiz.running to no\n" +
            "  set wins to quiz.wins\n" +
            "  set wins[player] to (wins[player] or 0) + 1\n" +
            "  set quiz.wins to wins\n" +
            "  say \"{player} got it! Wins total: {wins[player]}. Say !quiz for another round.\"\n" +
            "end on\n";
        BeaconRunResult run = await engine.RunScriptAsync("quiz", body);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        await engine.FireEventAsync("chat", BeaconEventFields.Chat("Steve", "!quiz"));
        Assert.Contains("Quiz! What mob explodes when it gets close? First correct whisper wins.", host.Says);

        await engine.FireEventAsync("chat", BeaconEventFields.Chat("Alex", "!quiz"));
        Assert.Single(host.Whispers);

        await engine.FireEventAsync("whisper", BeaconEventFields.Whisper("Alex", "creeper!"));
        Assert.Contains("Alex got it! Wins total: 1. Say !quiz for another round.", host.Says);
    }

    #endregion
    #region 7. auction sniper

    [Fact]
    public async Task AuctionSniper_LogsCheapDealsOverAllowlistedHttp()
    {
        string root = NewTempRoot();
        File.WriteAllText(Path.Combine(root, "beacon.toml"),
            "[Net]\nAllowedHosts = [\"hooks.example.com\"]\n");
        var posts = new List<(string Url, string Body)>();
        var (engine, host, _) = NewEngine();
        engine.NetGateFactory = config => new BeaconNetGate(
            config,
            (uri, method, body, ct) =>
            {
                posts.Add((uri.ToString(), body ?? string.Empty));
                return Task.FromResult(Encoding.UTF8.GetBytes("ok"));
            });

        const string body =
            "# beacon 1\n# needs: chat.send net.fetch\n\n" +
            "on server_message when text matches /bought (?<n>[0-9]+)x (?<item>[a-z_ ]+) for \\$(?<price>[0-9.]+)/\n" +
            "  set deal to match(text, /(?<n>[0-9]+)x (?<item>[a-z_ ]+) for \\$(?<price>[0-9.]+)/)\n" +
            "  if deal is set and number(deal.price) < 20\n" +
            "    say \"Saw cheap {deal.item} ({deal.n}x for ${deal.price}). Logging it.\"\n" +
            "    http_post(\"https://hooks.example.com/mcc-deals\", json_stringify(deal))\n" +
            "  end if\n" +
            "end on\n";
        BeaconRunResult run = await engine.RunScriptAsync("sniper", body, configFolder: root);
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        await engine.FireEventAsync("server_message",
            BeaconEventFields.ServerMessage("Alex bought 3x cooked beef for $12", null));
        Assert.Contains("Saw cheap cooked beef (3x for $12). Logging it.", host.Says);
        (string Url, string Body) post = Assert.Single(posts);
        Assert.Equal("https://hooks.example.com/mcc-deals", post.Url);
        Assert.Contains("cooked beef", post.Body);

        await engine.FireEventAsync("server_message",
            BeaconEventFields.ServerMessage("Alex bought 1x diamond for $500", null));
        Assert.Single(host.Says);
        Assert.Single(posts);
    }
    #endregion
}

using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Verb contracts against the host seam: say/whisper/server/mcc/show, slash rules, catchable mcc failures, wire silence, and passthrough logging.
/// </summary>
public sealed class VerbTests
{
    private sealed class RecordingHost : IBeaconHostServices
    {
        public List<string> Says { get; } = [];
        public List<(string Player, string Text)> Whispers { get; } = [];
        public List<string> Servers { get; } = [];
        public List<string> Mccs { get; } = [];
        public Func<string, string> MccHandler { get; set; } = _ => string.Empty;
        public string? SelfNameValue { get; set; } = "Tester";
        public List<string> OnlinePlayersValue { get; set; } = ["Alice"];
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
        {
            Servers.Add(commandLine);
            return Task.FromResult(string.Empty);
        }

        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
        {
            Mccs.Add(commandLine);
            return Task.FromResult(MccHandler(commandLine));
        }

        public string? SelfName => SelfNameValue;
        public IReadOnlyList<string> OnlinePlayers(int limit) => OnlinePlayersValue.Take(limit).ToList();
        public double? ServerTps => ServerTpsValue;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class MinimalHost : IBeaconHostServices
    {
        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult("minimal:" + commandLine);
        public string? SelfName => "Minimal";
        public IReadOnlyList<string> OnlinePlayers(int limit) => ["Zed"];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static (BeaconEngine Engine, RecordingHost Host) NewEngine(int seed = 21)
    {
        var host = new RecordingHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(seed), new FuelBudget());
        return (engine, host);
    }

    private static async Task<BeaconRunResult> RunCleanAsync(BeaconEngine engine, string scriptId, string body)
    {
        engine.LoadSource(scriptId, scriptId + ".mcc", "# beacon 1\n" + body);
        IReadOnlyList<BeaconDiagnostic> errors = engine.Lint(scriptId)
            .Where(d => d.Severity == BeaconSeverity.Error).ToList();
        Assert.Empty(errors);
        return await engine.RunTopLevelAsync(scriptId);
    }

    [Fact]
    public async Task Say_LeadingSlash_WarnsAndSendsNothing()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v1", "say \"/home\"\nshow \"after\"\n");

        Assert.True(result.Success);
        Assert.Empty(host.Says);
        BeaconDiagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == BeaconDiagnosticCodes.StrictLeadingSlash);
        Assert.Equal(BeaconSeverity.Warning, warning.Severity);
        Assert.Contains("server", warning.Message + warning.Suggestion, StringComparison.Ordinal);
        Assert.Contains("after", result.LocalOutput);
        // Refused say still debug-logged with script id and line.
        Assert.Contains(result.PassthroughLog, e => e.ScriptId == "v1" && e.Line == 2 && e.Verb == "say");
    }

    [Fact]
    public async Task Say_Normal_SendsUnprefixed_AndEchoStamped()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "hello", "say \"Hello everyone!\"\n");

        Assert.True(result.Success);
        Assert.Single(host.Says);
        Assert.Equal("Hello everyone!", host.Says[0]);
        Assert.Contains(result.LocalEcho, e => e.Contains("hello") && e.Contains("Hello everyone!"));
        Assert.Contains(result.PassthroughLog, e => e.ScriptId == "hello" && e.Verb == "say");
    }

    [Fact]
    public async Task Whisper_SendsPlayerAndText()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v2", "whisper \"Steve\" \"Meet at spawn?\"\n");

        Assert.True(result.Success);
        Assert.Single(host.Whispers);
        Assert.Equal("Steve", host.Whispers[0].Player);
        Assert.Equal("Meet at spawn?", host.Whispers[0].Text);
    }

    [Fact]
    public async Task Server_SlashRequired_SendsDownServerPath()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult ok = await RunCleanAsync(engine, "v3", "server \"/home\"\n");
        Assert.True(ok.Success);
        Assert.Equal(["/home"], host.Servers);

        var (engine2, host2) = NewEngine();
        engine2.LoadSource("v4", "v4.mcc", "# beacon 1\nserver \"home\"\n");
        BeaconRunResult bad = await engine2.RunTopLevelAsync("v4");
        Assert.False(bad.Success);
        Assert.Empty(host2.Servers);
        Assert.NotNull(bad.Error);
        Assert.Contains("server \"/home\"", bad.Error!.Suggestion ?? bad.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcc_ReturnsText()
    {
        var (engine, host) = NewEngine();
        host.MccHandler = cmd => cmd == "/list" ? "Alice, Bob" : string.Empty;
        BeaconRunResult result = await RunCleanAsync(engine, "v5", "set answer to mcc \"/list\"\nsay \"Online now: {answer}\"\n");

        Assert.True(result.Success);
        Assert.Single(host.Mccs);
        Assert.Equal("/list", host.Mccs[0]);
        Assert.Single(host.Says);
        Assert.Equal("Online now: Alice, Bob", host.Says[0]);
    }

    [Fact]
    public async Task Mcc_Failure_RaisesCatchably_InsideTry()
    {
        var (engine, host) = NewEngine();
        host.MccHandler = _ => throw new InvalidOperationException("no such command");
        BeaconRunResult result = await RunCleanAsync(engine, "v6",
            "try\nset x to mcc \"/bad\"\ncatch err\nshow err.message\nend try\nshow \"after\"\n");

        Assert.True(result.Success);
        Assert.Equal(2, result.LocalOutput.Count);
        Assert.Contains("no such command", result.LocalOutput[0]);
        Assert.Equal("after", result.LocalOutput[1]);
    }

    [Fact]
    public async Task Mcc_Failure_StopsHandlerReadably_OutsideTry()
    {
        var (engine, host) = NewEngine();
        host.MccHandler = _ => throw new InvalidOperationException("no such command");
        BeaconRunResult result = await RunCleanAsync(engine, "v7", "set x to mcc \"/bad\"\nshow \"never\"\n");

        Assert.False(result.Success);
        Assert.Empty(host.Says);
        Assert.DoesNotContain("never", result.LocalOutput);
        Assert.NotNull(result.Error);
        Assert.Contains("mcc", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no such command", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Say_Newline_RefusesCatchably_BeforeSend()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v9",
            "try\nsay \"line one\\nline two\"\ncatch err\nshow err.code\nend try\nshow \"after\"\n");

        Assert.True(result.Success);
        Assert.Empty(host.Says);
        Assert.Equal(
            [BeaconDiagnosticCodes.StrictChatText, "after"],
            result.LocalOutput.ToList());
    }

    [Fact]
    public async Task Whisper_SectionSign_RefusesCatchably_BeforeSend()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v10",
            "try\nwhisper \"Steve\" \"hi §cSteve\"\ncatch err\nshow err.code\nend try\n");

        Assert.True(result.Success);
        Assert.Empty(host.Whispers);
        Assert.Equal(BeaconDiagnosticCodes.StrictChatText, Assert.Single(result.LocalOutput));
    }

    [Fact]
    public async Task Server_Newline_RefusesCatchably_BeforeSend()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v11",
            "try\nserver \"/say hi\\nbye\"\ncatch err\nshow err.code\nend try\n");

        Assert.True(result.Success);
        Assert.Empty(host.Servers);
        Assert.Equal(BeaconDiagnosticCodes.StrictChatText, Assert.Single(result.LocalOutput));
    }

    [Fact]
    public async Task Show_NeverTouchesTransport()
    {
        var (engine, host) = NewEngine();
        host.MccHandler = _ => "should-not-be-called";
        BeaconRunResult result = await RunCleanAsync(engine, "v8", "show \"thinking out loud\"\n");

        Assert.True(result.Success);
        Assert.Contains("thinking out loud", result.LocalOutput);
        Assert.Empty(host.Says);
        Assert.Empty(host.Whispers);
        Assert.Empty(host.Servers);
        Assert.Empty(host.Mccs);
    }

    [Fact]
    public async Task WireSilence_RefusedSay_AndShow()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult result = await RunCleanAsync(engine, "v9", "say \"/home\"\nshow \"local only\"\n");

        Assert.True(result.Success);
        Assert.Empty(host.Says);
        Assert.Empty(host.Servers);
        Assert.Contains("local only", result.LocalOutput);
    }

    [Fact]
    public async Task Passthrough_DebugLog_CarriesScriptIdAndLine()
    {
        var (engine, host) = NewEngine();
        host.MccHandler = _ => "ok";
        BeaconRunResult result = await RunCleanAsync(engine, "trace",
            "say \"one\"\nwhisper \"Steve\" \"two\"\nserver \"/home\"\nset a to mcc \"/list\"\n");

        Assert.True(result.Success);
        Assert.Equal(4, result.PassthroughLog.Count);
        Assert.Equal(["say", "whisper", "server", "mcc"], result.PassthroughLog.Select(e => e.Verb).ToList());
        Assert.All(result.PassthroughLog, e => Assert.Equal("trace", e.ScriptId));
        Assert.Equal([2, 3, 4, 5], result.PassthroughLog.Select(e => e.Line).ToList());
        foreach (BeaconPassthroughLog entry in result.PassthroughLog)
            Assert.Contains("trace", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeamConformance_CustomDouble_WorksThroughInterface()
    {
        var engine = new BeaconEngine(new MinimalHost(), new VirtualClock(), new SeededRng(3), new FuelBudget());
        engine.LoadSource("mini", "mini.mcc", "# beacon 1\nset a to mcc \"/list\"\nshow a\n");
        Assert.DoesNotContain(engine.Lint("mini"), d => d.Severity == BeaconSeverity.Error);
        BeaconRunResult result = await engine.RunTopLevelAsync("mini");
        Assert.True(result.Success);
        Assert.Contains("minimal:/list", result.LocalOutput);
    }

    [Fact]
    public async Task WelcomeBot_Verbs_OnFakes()
    {
        var (engine, host) = NewEngine();
        engine.LoadSource("welcome", "welcome.mcc",
            "# beacon 1\n# needs: chat.send\non join:\nsay \"Welcome to the server, {player}!\"\nend on\n" +
            "on chat when message contains \"!rules\":\nwhisper player \"1. Be kind.\"\nend on\n");
        Assert.DoesNotContain(engine.Lint("welcome"), d => d.Severity == BeaconSeverity.Error);
        Assert.True((await engine.RunTopLevelAsync("welcome")).Success);

        BeaconRunResult join = await engine.InvokeHandlerAsync(
            "welcome", "join", new Dictionary<string, BeaconValue> { ["player"] = BeaconValue.Text("Steve") });
        Assert.True(join.Success);
        Assert.Equal("Welcome to the server, Steve!", host.Says[^1]);
        Assert.DoesNotContain("[welcome]", host.Says[^1]);

        BeaconRunResult chat = await engine.InvokeHandlerAsync(
            "welcome", "chat", new Dictionary<string, BeaconValue>
            {
                ["player"] = BeaconValue.Text("Alex"),
                ["message"] = BeaconValue.Text("!rules please"),
            });
        Assert.True(chat.Success);
        Assert.Equal("Alex", host.Whispers[^1].Player);
    }
}

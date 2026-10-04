using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Client;
using Umpk.Protocol.Java;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Regression tests that stay green headless.
/// Loading one script re-fired every loaded script's <c>on start</c>.
/// <c>every</c> and one-shot timers never fired on a live session (only the headless runner pumped them).
/// </summary>
public sealed class LiveDefectTests : IDisposable
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
        public Task SayAsync(string text, CancellationToken ct = default)
        {
            Says.Add(text);
            return Task.CompletedTask;
        }
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public IReadOnlyList<string> OnlinePlayers(int limit) => [];
        public double? ServerTps => null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    [Fact]
    public async Task RemoveScript_CancelsRunningTasksAndWaits()
    {
        var host = new RecordingHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(7), new FuelBudget());

        BeaconRunResult run = await engine.RunScriptAsync(
            "looper",
            WithHeader("start p()\nfunction p()\nwhile yes\nsay \"tick\"\nwait 1 seconds\nend while\nend function\n"));
        Assert.True(run.Success);

        clock.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        int before = host.Says.Count;
        Assert.True(before > 0);

        Assert.True(engine.RemoveScript("looper"));

        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(before, host.Says.Count);
    }

    [Fact]
    public async Task RunSecondScript_DoesNotRefireFirstScriptStart()
    {
        var engine = new BeaconEngine(new RecordingHost(), new VirtualClock(), new SeededRng(7), new FuelBudget());

        BeaconRunResult first = await engine.RunScriptAsync(
            "a", WithHeader("on start:\nshow \"A-START\"\nend on\n"));
        Assert.True(first.Success);
        Assert.Contains("A-START", first.LocalOutput);

        BeaconRunResult second = await engine.RunScriptAsync(
            "b", WithHeader("on start:\nshow \"B-START\"\nend on\n"));
        Assert.True(second.Success);
        Assert.Contains("B-START", second.LocalOutput);
        Assert.DoesNotContain("A-START", second.LocalOutput);
    }

    [Fact]
    public async Task RunScriptWithoutStart_DoesNotFireOtherScriptsStart()
    {
        var engine = new BeaconEngine(new RecordingHost(), new VirtualClock(), new SeededRng(7), new FuelBudget());

        BeaconRunResult first = await engine.RunScriptAsync(
            "a", WithHeader("on start:\nshow \"A-START\"\nend on\n"));
        Assert.True(first.Success);

        BeaconRunResult second = await engine.RunScriptAsync(
            "b", WithHeader("show \"B-TOP\"\n"));
        Assert.True(second.Success);
        Assert.Contains("B-TOP", second.LocalOutput);
        Assert.DoesNotContain("A-START", second.LocalOutput);
    }

    [Fact]
    public async Task LivePump_FiresEveryBlocksWhilePlaying()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-pump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        string scriptPath = Path.Combine(root, "tick.bcn");
        await File.WriteAllTextAsync(
            scriptPath,
            "# beacon 1\n# needs: chat.send\n\nevery 1 seconds\nsay \"tick\"\nend every\n");

        await using FakeJavaServer server = FakeJavaServer.Create();
        await using Client client = McPluginSessionHarness.BuildClient(
            server,
            "Tester",
            Path.Combine(root, "plugins"),
            NullLoggerFactory.Instance);

        using var loginCts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        Task starting = client.StartAsync(loginCts.Token);
        await McPluginSessionHarness.DriveLoginAsync(server, loginCts.Token);
        await starting.WaitAsync(McPluginSessionHarness.Budget, loginCts.Token);
        Assert.Equal(ClientStatus.Playing, client.Status);

        CmdResult run = await client.Commands.DispatchAsync($"scripts run \"{scriptPath}\"");
        Assert.Equal(CmdStatus.Done, run.Status);

        // The in-client timer pump ticks while Playing: the first every-fire lands well inside this bound (pump period plus one interval).
        // Without the pump no chat ever arrives.
        // Login chatter (brand, position) is ignored; only the scripted word counts.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool sawTick = false;
        while (!sawTick)
        {
            InboundFrame frame = await server.NextFrameAsync(timeout.Token);
            if (System.Text.Encoding.UTF8.GetString(frame.Payload).Contains("tick", StringComparison.Ordinal))
                sawTick = true;
        }

        Assert.True(sawTick);
    }

    [Fact]
    public async Task LiveUse_ResolvesInsteadOfHanging()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        string scriptPath = Path.Combine(root, "touch.bcn");
        await File.WriteAllTextAsync(
            scriptPath,
            "# beacon 1\n# needs: chat.send world.write\n\non start:\n  try\n"
            + "    set c to world.use(0, 64, 0)\n"
            + "    if c is none\n      say \"use returned none\"\n"
            + "    else\n      say \"opened window\"\n    end if\n"
            + "  catch err\n    say \"use failed\"\n  end try\nend on\n");

        await using FakeJavaServer server = FakeJavaServer.Create();
        await using Client client = McPluginSessionHarness.BuildClient(
            server,
            "Tester",
            Path.Combine(root, "plugins"),
            NullLoggerFactory.Instance);

        using var loginCts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        Task starting = client.StartAsync(loginCts.Token);
        await McPluginSessionHarness.DriveLoginAsync(server, loginCts.Token);
        await starting.WaitAsync(McPluginSessionHarness.Budget, loginCts.Token);
        Assert.Equal(ClientStatus.Playing, client.Status);

        CmdResult run = await client.Commands.DispatchAsync($"scripts run \"{scriptPath}\"");
        Assert.Equal(CmdStatus.Done, run.Status);

        // The empty harness world opens no window, so use must resolve to none and say so.
        // The verdict word distinguishes the branches; either resolves.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool sawVerdict = false;
        while (!sawVerdict)
        {
            InboundFrame frame = await server.NextFrameAsync(timeout.Token);
            string text = System.Text.Encoding.UTF8.GetString(frame.Payload);
            if (text.Contains("use returned none", StringComparison.Ordinal)
                || text.Contains("opened window", StringComparison.Ordinal)
                || text.Contains("use failed", StringComparison.Ordinal))
                sawVerdict = true;
        }

        Assert.True(sawVerdict);
    }

    private sealed class CaptureOutput : ICommandOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string line) => Lines.Add(line);
    }

    private sealed class ConsoleRecordingHost(ICommandOutput? output) : IHostInterface
    {
        public IUserPrompt? Prompt => null;
        public Umpk.Auth.IAuthInteraction? AuthInteraction => null;
        public ICommandOutput? CommandOutput { get; } = output;
        public IHostUi? Ui => null;
    }

    [Fact]
    public async Task HandlerFailure_SurfacesOnConsole_Throttled()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        string scriptPath = Path.Combine(root, "brittle.bcn");
        await File.WriteAllTextAsync(
            scriptPath,
            "# beacon 1\n# needs: world.read\n\non chat\n  set b to world.block_at(99999999, 64, 0)\nend on\n");

        var output = new CaptureOutput();
        await using FakeJavaServer server = FakeJavaServer.Create();
        await using Client client = McPluginSessionHarness.BuildClient(
            server,
            "Tester",
            Path.Combine(root, "plugins"),
            NullLoggerFactory.Instance,
            new ConsoleRecordingHost(output));

        using var loginCts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        Task starting = client.StartAsync(loginCts.Token);
        await McPluginSessionHarness.DriveLoginAsync(server, loginCts.Token);
        await starting.WaitAsync(McPluginSessionHarness.Budget, loginCts.Token);
        Assert.Equal(ClientStatus.Playing, client.Status);

        CmdResult run = await client.Commands.DispatchAsync($"scripts run \"{scriptPath}\"");
        Assert.Equal(CmdStatus.Done, run.Status);

        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        await McPluginSessionHarness.SendAsync(
            server,
            Umpk.Protocol.Java.ProtocolPhase.Play,
            new Umpk.Protocol.Java.Packets.ClientboundSystemChatPacket(
                Umpk.Text.Component.Text("<Alice> hello"), false),
            cts.Token);

        // The failed handler names its script on the console instead of dying silently.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!output.Lines.Any(l => l.Contains("brittle", StringComparison.Ordinal)))
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);

        string first = output.Lines.First(l => l.Contains("brittle", StringComparison.Ordinal));
        Assert.Contains("on chat", first, StringComparison.Ordinal);
        int count = output.Lines.Count(l => l.Contains("brittle", StringComparison.Ordinal));

        // An immediate repeat stays throttled: no second note for the same failure.
        await McPluginSessionHarness.SendAsync(
            server,
            Umpk.Protocol.Java.ProtocolPhase.Play,
            new Umpk.Protocol.Java.Packets.ClientboundSystemChatPacket(
                Umpk.Text.Component.Text("<Alice> hello again"), false),
            cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        Assert.Equal(count, output.Lines.Count(l => l.Contains("brittle", StringComparison.Ordinal)));
    }
}

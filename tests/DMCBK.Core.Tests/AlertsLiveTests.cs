using DMCBK.Core.Commands;
using DMCBK.Testing;
using Umpk.Protocol.Java.Packets;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Alerts against the real OpenRouter backend.
/// Opt-in only: runs when LLM_LIVE_TEST_KEY holds a key, and does nothing otherwise.
/// Uses the cheapest flash model with tiny prompts, a few cents at most.
/// </summary>
public sealed class AlertsLiveTests
{
    private const string LiveModel = "openrouter-live/deepseek/deepseek-v4.1-flash";
    private const string LiveProvider = "openrouter-live";
    private const string LiveEndpoint = "https://openrouter.ai/api/v1";

    [Fact]
    public async Task Live_SmartGated_Confirms_And_Rejects()
    {
        string key = LiveKey();
        if (key.Length == 0)
            return;

        await using PluginTestHost host = PluginTestHost.Create();
        string alerts = host.AddPluginFolder(Path.Combine(RepositoryRoot(), "plugins", "Alerts"));
        File.WriteAllText(Path.Combine(alerts, "settings.toml"), $"""
            Enabled = true
            LogToFile = true
            [[Rule]]
            Name = "admin-watch"
            Match = [ "admin" ]
            Routes = [ "file" ]
            Beep = false
            Smart = true
            [Smart]
            Mode = "rules"
            Model = "{LiveModel}"
            Confidence = 0.7
            Prompt = "Alert when the line needs a human. Ignore shop ads and minigame scores."
            Context = [ ]
            """);
        string core = host.AddPluginFolder(Path.Combine(RepositoryRoot(), "plugins", "LlmCore"));
        File.WriteAllText(Path.Combine(core, "settings.toml"), $"""
            AllowLocalEndpoints = true
            DefaultModel = "{LiveModel}"
            [[Providers]]
            Name = "{LiveProvider}"
            Endpoint = "{LiveEndpoint}"
            Api = "responses"
            Enabled = true
            ApiKeyEnv = "LLM_LIVE_TEST_KEY"
            """);
        await host.LoadAsync();

        string log = host.DataFile("Alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            Environment.SetEnvironmentVariable("LLM_LIVE_TEST_KEY", key);

            await session.SendAsync(
                new ClientboundSystemChatPacket(
                    Component.Text("<Alice> admin, someone is griefing my house, help"), false),
                ct: CancellationToken.None);
            Assert.True(await WaitForJudgeAsync(host, session,
                () => Read(log).Contains("griefing", StringComparison.Ordinal)));

            await session.SendAsync(
                new ClientboundSystemChatPacket(
                    Component.Text("<Bob> admin shop restocked, cheap diamonds"), false),
                ct: CancellationToken.None);
            Assert.True(await WaitForJudgeAsync(host, session,
                () => StatusText(host).Contains("calls: 2", StringComparison.Ordinal)));
            await Task.Delay(2000, CancellationToken.None);
            Assert.DoesNotContain("restocked", Read(log), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Live_FullMode_Judges_And_Batches()
    {
        string key = LiveKey();
        if (key.Length == 0)
            return;

        await using PluginTestHost host = PluginTestHost.Create();
        string alerts = host.AddPluginFolder(Path.Combine(RepositoryRoot(), "plugins", "Alerts"));
        File.WriteAllText(Path.Combine(alerts, "settings.toml"), $"""
            Enabled = true
            LogToFile = true
            [Smart]
            Mode = "full"
            Model = "{LiveModel}"
            Confidence = 0.7
            Prompt = "Alert only when a player needs help or reports danger. Ignore everything else."
            CatchAllRoutes = [ "file" ]
            Batch = true
            BatchSize = 10
            BatchSeconds = 20
            Context = [ "nick" ]
            """);
        string core = host.AddPluginFolder(Path.Combine(RepositoryRoot(), "plugins", "LlmCore"));
        File.WriteAllText(Path.Combine(core, "settings.toml"), $"""
            AllowLocalEndpoints = true
            DefaultModel = "{LiveModel}"
            [[Providers]]
            Name = "{LiveProvider}"
            Endpoint = "{LiveEndpoint}"
            Api = "responses"
            Enabled = true
            ApiKeyEnv = "LLM_LIVE_TEST_KEY"
            """);
        await host.LoadAsync();

        string log = host.DataFile("Alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            Environment.SetEnvironmentVariable("LLM_LIVE_TEST_KEY", key);

            await session.SendAsync(
                new ClientboundSystemChatPacket(
                    Component.Text("<Steve> can someone help me, I am lost in a cave"), false),
                ct: CancellationToken.None);
            Assert.True(await WaitForJudgeAsync(host, session,
                () => Read(log).Contains("lost in a cave", StringComparison.Ordinal)));
        });
    }

    private static string LiveKey()
        => (Environment.GetEnvironmentVariable("LLM_LIVE_TEST_KEY") ?? string.Empty).Trim();

    private static async Task<CmdResult> Dispatch(PluginTestHost host, string command)
    {
        CmdResult result = await host.Client.Commands.DispatchAsync(command);
        Assert.True(result.Status == CmdStatus.Done, $"Command '{command}' failed: {result.Message}");
        return result;
    }

    private static string StatusText(PluginTestHost host)
    {
        try
        {
            CmdResult result = host.Client.Commands.DispatchAsync("alerts smart status")
                .GetAwaiter().GetResult();
            return result.Message ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task RunLongSessionAsync(PluginTestHost host, Func<PluginTestSession, Task> body)
    {
        using var sessionCts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        await host.RunSessionAsync(body, sessionCts.Token);
    }

    private static async Task<bool> WaitForJudgeAsync(
        PluginTestHost host, PluginTestSession session, Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var keepalive = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                while (!keepalive.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), keepalive.Token);
                    try
                    {
                        await session.SendAsync(
                            new ClientboundSetTimePacket(0, 0, false, []), ct: keepalive.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        try
        {
            return await host.WaitForAsync(condition, budget.Token);
        }
        finally
        {
            keepalive.Cancel();
            await pump;
        }
    }

    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "plugins"))
                && File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}

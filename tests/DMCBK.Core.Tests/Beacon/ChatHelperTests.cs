using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Chat bounds: last_from, count_matching, history, and online_players.
/// </summary>
public sealed class ChatHelperTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task LastFrom_Miss_SurfacesNone()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.ChatLines = [new BeaconChatLine("[Steve] hi", DateTimeOffset.UtcNow)];
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "c", "last_from(\"Nobody\")"));
    }

    [Fact]
    public async Task CountMatching_IsCaseInsensitive()
    {
        var (engine, host, clock) = ScriptTestHelpers.NewEngine();
        DateTimeOffset now = clock.UtcNow;
        host.ChatLines =
        [
            new BeaconChatLine("[Steve] !BID 5", now),
            new BeaconChatLine("[Alex] !bid 6", now),
        ];
        Assert.Equal("2", await ScriptTestHelpers.ShowAsync(engine, "c", "count_matching(\"!bid\", 10)"));
    }

    [Fact]
    public async Task ChatHistory_Negative_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "c", "show chat_history(0 - 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, run.Error?.Code);
    }

    [Fact]
    public async Task OnlinePlayers_ClampsToMax()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.Players = Enumerable.Range(0, 150).Select(i => "P" + i).ToList();
        BeaconRunResult run = await engine.RunScriptAsync("o", ScriptTestHelpers.WithHeader("show len(online_players(1000))\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("100", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task OnlinePlayers_Negative_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "o", "show online_players(0 - 1)\n");
        Assert.False(run.Success);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, run.Error?.Code);
    }
}

using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Hooks: player_list, container_open, and container_close fire with field tables and throttle-clause support like the shipped hooks.
/// </summary>
public sealed class PlayerListAndContainerHookTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private static (BeaconEngine Engine, ScriptTestHost Host, VirtualClock Clock) NewEngine(int seed = 13)
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock();
        var engine = new BeaconEngine(host, clock, new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host, clock);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    [Fact]
    public void Catalog_ListsNewHooksWithFields()
    {
        Assert.Contains("player_list", BeaconHookCatalog.KnownHooks);
        Assert.Contains("container_open", BeaconHookCatalog.KnownHooks);
        Assert.Contains("container_close", BeaconHookCatalog.KnownHooks);
        Assert.True(BeaconHookCatalog.TryGetSchema("player_list", out BeaconHookSchema? list));
        Assert.Contains(list!.Fields, f => f.Name == "players");
        Assert.Contains(list.Fields, f => f.Name == "header");
        Assert.True(BeaconHookCatalog.TryGetSchema("container_open", out BeaconHookSchema? opened));
        Assert.Contains(opened!.Fields, f => f.Name == "window");
        Assert.True(BeaconHookCatalog.TryGetSchema("container_close", out BeaconHookSchema? closed));
        Assert.Contains(closed!.Fields, f => f.Name == "window");
    }

    [Fact]
    public async Task PlayerList_FiresWithFields()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("p", WithHeader(
            "on player_list:\nshow \"{count} online, header is {header}\"\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        BeaconFireResult fire = await engine.FireEventAsync(
            "player_list", BeaconEventFields.PlayerList(["Alice", "Bob"], "Queue: 3", null));
        BeaconHandlerFire handler = Assert.Single(fire.Handlers);
        Assert.True(handler.Result!.Success);
        Assert.Equal("2 online, header is Queue: 3", Assert.Single(handler.Result.LocalOutput));
    }

    [Fact]
    public async Task ContainerOpenClose_FireWithFields()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("c", WithHeader(
            "on container_open:\nshow \"opened {title}\"\nend on\non container_close:\nshow \"closed {window}\"\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        BeaconFireResult opened = await engine.FireEventAsync(
            "container_open", BeaconEventFields.ContainerOpen(3, "Chest", "minecraft:chest"));
        Assert.Equal("opened Chest", Assert.Single(Assert.Single(opened.Handlers).Result!.LocalOutput));
        BeaconFireResult closed = await engine.FireEventAsync("container_close", BeaconEventFields.ContainerClose(3));
        Assert.Equal("closed 3", Assert.Single(Assert.Single(closed.Handlers).Result!.LocalOutput));
    }

    [Fact]
    public async Task PlayerList_ThrottleClause_SkipsInWindow()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("p", WithHeader(
            "on player_list cooldown 60 seconds named \"plist\":\nshow count\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        BeaconFireResult first = await engine.FireEventAsync(
            "player_list", BeaconEventFields.PlayerList(["Alice"]));
        Assert.False(Assert.Single(first.Handlers).Throttled);
        BeaconFireResult second = await engine.FireEventAsync(
            "player_list", BeaconEventFields.PlayerList(["Alice", "Bob"]));
        BeaconHandlerFire throttled = Assert.Single(second.Handlers);
        Assert.True(throttled.Throttled);
        Assert.True(throttled.ThrottleRemaining > TimeSpan.Zero);
    }

    [Fact]
    public async Task NewHooks_LintClean()
    {
        var (engine, _, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("p", WithHeader(
            "on player_list:\nshow count\nend on\non container_open:\nshow window\nend on\non container_close:\nshow window\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.DoesNotContain(run.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }
}

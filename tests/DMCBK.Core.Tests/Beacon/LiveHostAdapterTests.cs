using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Game.Items;
using Umpk.Game.Players;
using Umpk.Game.Registries;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using DMCBK.Testing.Server;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Live-host proof on the fake server: the session-backed adapter returns real values where the session knows them (name, vitals, position, gamemode, inventory shape, scoreboard shape) and degrades to unknown where the fake pipe knows nothing (light/biome outside loaded chunks, mspt).
/// </summary>
public sealed class LiveHostAdapterTests
{
    private static BeaconClientHost NewLiveHost(DMCBK.Core.Client client)
        => new(client, () => false, () => { });

    [Fact]
    public async Task LiveReads_ReturnRealValues()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);

        // SelfName rides the auth session (null on the offline fake pipe); the game reads below are what this adapter owns.
        Assert.NotNull(host.SelfVitals);
        Assert.NotNull(host.SelfPosition);
        Assert.NotNull(host.Gamemode);
        Assert.NotNull(host.ServerInfo);
        Assert.NotNull(host.GameProtocol);
        Assert.True(host.GameProtocol > 0);
    }

    [Fact]
    public async Task LiveReads_UnknownsDegradeToNone()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);

        // Mspt has no Core API behind it yet: null (surfaced as none), never an invented number.
        // Light and biome have Core APIs (WorldApi.GetLightAsync and GetBiomeIdAsync), but the fake pipe carries no loaded chunks, so unloaded columns still degrade to null here.
        var reads = (IBeaconExtendedReads)host;
        Assert.Null(reads.ServerMspt);
        Assert.Null(reads.GetLight(0, 64, 0));
        Assert.Null(reads.GetBiome(0, 64, 0));
    }

    [Fact]
    public async Task LiveMcc_SlashFormsAgree_AndFailureThrows()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);

        // The documented slash form and the bare form reach the same command.
        string bare = await host.RunMccAsync("scripts list", ct);
        string slashed = await host.RunMccAsync("/scripts list", ct);
        Assert.Equal(bare, slashed);

        // A failing command throws (the script layer turns this into a catchable B4001), never a mystery empty string.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.RunMccAsync("scripts run definitely-missing-123", ct));
    }

    [Fact]
    public async Task LiveInventory_HasPlayerShape()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);

        Assert.Equal(46, host.InventorySlots.Count);
        Assert.InRange(host.SelectedSlot, 0, 8);
        Assert.True(await host.SelectSlotAsync(3, ct));
        Assert.False(await host.SelectSlotAsync(40, ct));
        Assert.Empty(host.CraftList());
        Assert.False(await host.CraftOneAsync("minecraft:torch", ct));
        Assert.Null(host.OpenContainer);
    }

    [Fact]
    public async Task LiveScoreboard_Bossbars_Shapes()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);

        Assert.NotNull(host.Scoreboard);
        Assert.NotNull(host.BossBars);
        IReadOnlyList<BeaconBlockPos> found =
            await host.FindBlocksAsync("minecraft:stone", 8, 8, ct);
        Assert.NotNull(found);
    }

    [Fact]
    public async Task LiveChatHistory_CollectsSessionLines()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        BeaconClientHost host = NewLiveHost(fixture.Client);
        Assert.Empty(host.ChatHistory(10));
        Assert.Null(host.LastFrom("Nobody"));
    }

    [Fact]
    public async Task LiveMovement_LeasePreemption_Intact()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        // The runner keeps newest-wins preemption with a live-shaped executor behind it.
        var runner = new BeaconMovementRunner(
            new BeaconFreeMovementScope(),
            BeaconGameplayGates.AllOn,
            async (target, taskCt) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), taskCt).ConfigureAwait(false);
                return new BeaconMoveArrival(true, "arrived");
            });
        Task<BeaconValue> first = runner.GotoAsync(10, 64, 10, "script-a", ct);
        Task<BeaconValue> second = runner.GotoAsync(
            20, 64, 20, "script-b", new BeaconMoveOptions(Tolerance: 2, Sneak: true, Sprint: false), ct);
        BeaconRuntimeException loser = await Assert.ThrowsAsync<BeaconRuntimeException>(() => first);
        Assert.Equal(BeaconTaskErrorCodes.Superseded, loser.Code);
        BeaconValue arrival = await second;
        Assert.IsType<BeaconMapValue>(arrival);
    }
}

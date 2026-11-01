using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Umpk.Geometry;
using Umpk.Protocol.Java.Packets;
using Xunit;
using Fixture = DMCBK.Core.Tests.BlockInteractionCommandTests.Fixture;

namespace DMCBK.Core.Tests;

public sealed class HeldDigCommandTests
{
    [Fact]
    public async Task HeldAttackReturnsControlAndStopReleasesItBeforeAnotherCommand()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync("dig", ct), ct);
        try
        {
            ServerboundPlayerActionPacket start = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
            Assert.Equal(0, start.Action);
            Assert.Equal(Fixture.Target, start.Position);
            CmdResult result = await dispatch.WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.Equal(CmdStatus.Done, result.Status);
            Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("move get", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);

            Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig stop", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
            ServerboundPlayerActionPacket abort = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
            Assert.Equal(1, abort.Action);
            Assert.Equal(Fixture.Target, abort.Position);
            Assert.Equal((byte)Direction.Down, abort.Direction);
            Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig stop", ct)).Status);
            Assert.Equal("minecraft:bedrock", (await fixture.Client.Game.World.GetBlockAsync(Fixture.Target, ct)).BlockId);
        }
        finally
        {
            budget.Cancel();
            try { await dispatch; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CallerCancellationReleasesOnlyItsAttackAndAllowsAnotherOne()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync("dig", caller.Token), ct);
        try
        {
            Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
            Assert.Equal(CmdStatus.Done, (await dispatch.WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
            caller.Cancel();
            Assert.Equal(1, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
            await fixture.Client.Commands.DispatchAsync("dig stop", ct).WaitAsync(TimeSpan.FromSeconds(2), ct);

            Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
            Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
            await fixture.Client.Commands.DispatchAsync("dig stop", ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.Equal(1, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        }
        finally
        {
            caller.Cancel(); budget.Cancel();
            try { await dispatch; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task DispatcherDisposalReleasesTheAttackWhileItsConnectionIsStillLive()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync("dig", ct), ct);
        try
        {
            Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
            Assert.Equal(CmdStatus.Done, (await dispatch.WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
            await Assert.IsAssignableFrom<IAsyncDisposable>(fixture.Client.Commands).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            ServerboundPlayerActionPacket abort = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
            Assert.Equal(1, abort.Action);
            Assert.Equal((byte)Direction.Down, abort.Direction);
            Assert.Equal(new Vec3d(2.5, 64, 3.5), (await fixture.Client.Game.Movement.GetPoseAsync(ct)).Position);
        }
        finally
        {
            budget.Cancel();
            try { await dispatch; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ClientsOwnIndependentAttacksAndBusyCommandsDoNotReplaceThem()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture first = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        await using Fixture second = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Assert.Equal(CmdStatus.Done, (await first.Client.Commands.DispatchAsync("dig", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        Assert.Equal(CmdStatus.Done, (await second.Client.Commands.DispatchAsync("dig", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        Assert.Equal(0, (await first.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        Assert.Equal(0, (await second.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);

        CmdResult busy = await second.Client.Commands.DispatchAsync("dig 2 64 2", ct);
        Assert.Equal(CmdStatus.Fail, busy.Status);
        Assert.Equal(CommandStrings.DigAlreadyRunning("/"), busy.Message);
        await first.Client.Commands.DispatchAsync("dig stop", ct);
        Assert.Equal(1, (await first.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        Assert.Equal(CmdStatus.Fail, (await second.Client.Commands.DispatchAsync("dig", ct)).Status);
        await second.Client.Commands.DispatchAsync("dig stop", ct);
        Assert.Equal(1, (await second.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
    }

    [Theory]
    [InlineData("dig")]
    [InlineData("dig 2 64 1")]
    public async Task BackgroundFailureIsObservedAndTheSlotCanBeReleased(string command)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        var fault = new TaskCompletionSource<CommandFaultedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Commands.CommandFaulted += (_, args) => fault.TrySetResult(args);
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync(command, ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);

        await McPluginSessionHarness.SendAsync(fixture.Server, Umpk.Protocol.Java.ProtocolPhase.Play, new ClientboundGameEventPacket(3, 3), ct);
        CommandFaultedEventArgs failure = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        Assert.Equal(command, failure.Line);
        Assert.Equal("dig", failure.Command!.CmdName);
        Assert.IsAssignableFrom<InvalidOperationException>(failure.Exception);
        Assert.Equal(1, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig stop", ct)).Status);
    }

    [Fact]
    public async Task StopRemainsAvailableWhenTerrainCommandsAreDisabled()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        fixture.Client.Features.Terrain = false;

        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig stop", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        Assert.Equal(1, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
    }

    [Fact]
    public async Task NaturalCompletionReleasesTheSlotWithoutAStopCommand()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct);
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig", ct).WaitAsync(TimeSpan.FromSeconds(2), ct)).Status);
        ServerboundPlayerActionPacket start = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
        await fixture.AcknowledgeAsync(start.Sequence!.Value, ct);
        await McPluginSessionHarness.SendAsync(fixture.Server, Umpk.Protocol.Java.ProtocolPhase.Play, new ClientboundBlockUpdatePacket(Fixture.Target, 0), ct);

        CmdResult probe;
        do
        {
            probe = await fixture.Client.Commands.DispatchAsync("dig 1000 64 1000", ct);
            if (probe.Message != CommandStrings.DigAlreadyRunning("/"))
                break;
            await Task.Delay(10, ct);
        } while (true);
        Assert.Equal(CmdStatus.Fail, probe.Status);
        Assert.Equal(CommandStrings.DigNotRunning, (await fixture.Client.Commands.DispatchAsync("dig stop", ct)).Message);
    }

}

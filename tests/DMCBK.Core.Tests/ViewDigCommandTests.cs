using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Umpk;
using Umpk.Data.Java;
using Umpk.Geometry;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using Xunit;
using Fixture = DMCBK.Core.Tests.BlockInteractionCommandTests.Fixture;

namespace DMCBK.Core.Tests;

public sealed class ViewDigCommandTests
{
    [Theory]
    [InlineData("look_away")]
    [InlineData("out_of_reach")]
    [InlineData("another_block")]
    public async Task LosingTheRequestedBlockReleasesTheCommand(string change)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct, survival: true, block: "bedrock");
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("dig", ct)).Status);
        Assert.Equal(0, (await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct)).Action);
        if (change == "look_away")
            await fixture.Client.Game.Movement.LookAtAsync(new Vec3d(2.5, 70, 3.5), ct);
        else if (change == "out_of_reach")
        {
            var position = new Vec3d(20.5, 64, 3.5);
            await McPluginSessionHarness.SendAsync(fixture.Server, ProtocolPhase.Play,
                new ClientboundPlayerPositionPacket(position.X, position.Y, position.Z, 180, 29, 0, 2,
                    new PositionMoveRotation(position, default, 180, 29)), ct);
        }
        else
        {
            var other = new BlockPos(3, 64, 1);
            Assert.True(JavaGameData.Registries(770).Blocks.TryGetValue(Identifier.Minecraft("stone"), out var stone));
            await McPluginSessionHarness.SendAsync(fixture.Server, ProtocolPhase.Play, new ClientboundBlockUpdatePacket(other, stone.DefaultStateId), ct);
            await fixture.Client.Game.Movement.LookAtAsync(other.Center, ct);
        }
        ServerboundPlayerActionPacket abort = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
        Assert.Equal(1, abort.Action);
        Assert.Equal(Fixture.Target, abort.Position);
        Assert.Equal((byte)Direction.Down, abort.Direction);
        Assert.Equal("minecraft:bedrock", (await fixture.Client.Game.World.GetBlockAsync(Fixture.Target, ct)).BlockId);
        await fixture.Client.Commands.DispatchAsync("dig stop", ct);
        Assert.Equal(CommandStrings.DigNotRunning, (await fixture.Client.Commands.DispatchAsync("dig stop", ct)).Message);
    }
}

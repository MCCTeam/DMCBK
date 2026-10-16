using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Umpk.Client.Snapshots;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using Umpk.Protocol.Java.Packets;
using Umpk.Geometry;
using Xunit;
using Fixture = DMCBK.Core.Tests.BlockInteractionCommandTests.Fixture;

namespace DMCBK.Core.Tests;

public sealed class UseItemInteractionTests
{
    [Theory]
    [InlineData("useitem")]
    [InlineData("useitem 2 64 1")]
    public async Task BlockUsePreservesTheEyeRayFaceAndCursor(string command)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct);

        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync(command, ct), ct);
        ServerboundUseItemOnPacket use = await fixture.NextPacketAsync<ServerboundUseItemOnPacket>(ct);
        BlockRaycastHit? hit = await fixture.Client.Game.World.RaycastAsync(4.5, false, ct);
        await fixture.AcknowledgeAsync(use.Sequence, ct);
        await dispatch.WaitAsync(McPluginSessionHarness.Budget, ct);

        Assert.NotNull(hit);
        Assert.Equal(Fixture.Target, hit.Position);
        Assert.Equal(Fixture.Target, use.Position);
        Assert.Equal((int)hit.Face, use.Face);
        Assert.Equal(hit.InsideBlock, use.Inside);
        Assert.Equal((float)(hit.Point.X - Fixture.Target.X), use.CursorX);
        Assert.Equal((float)(hit.Point.Y - Fixture.Target.Y), use.CursorY);
        Assert.Equal((float)(hit.Point.Z - Fixture.Target.Z), use.CursorZ);
    }

    [Theory]
    [InlineData("useitem")]
    [InlineData("useitem 2 64 1")]
    public async Task PassiveBlockUseDoesNotAppendAnUnconditionalSwing(string command)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct);

        var (_, packets) = await DispatchPacketsAsync(fixture, command, ct);
        Assert.Single(packets.OfType<ServerboundUseItemOnPacket>());

        Assert.Empty(packets.OfType<ServerboundSwingPacket>());
    }

    [Fact]
    public async Task CoordinateUseRefusesABlockBeyondTheEyeRay()
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct);

        var (result, packets) = await DispatchPacketsAsync(fixture, "useitem 20 64 1", ct);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Empty(packets.OfType<ServerboundUseItemOnPacket>());
        Assert.Empty(packets.OfType<ServerboundSwingPacket>());
    }

    private static async Task<(CmdResult Result, List<object> Packets)> DispatchPacketsAsync(Fixture fixture, string command, CancellationToken ct)
    {
        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync(command, ct), ct);
        PhaseRegistry outbound = McPluginSessionHarness.Version.Protocol.GetRegistry(ProtocolPhase.Play, PacketFlow.Serverbound);
        List<object> packets = [];
        Task<InboundFrame>? next = null;
        object Decode(InboundFrame frame)
        {
            Assert.True(outbound.TryGetInbound(frame.WireId, out BoundPacketCodec codec));
            return codec.Decode(frame.Payload, PacketCodecContext.Registryless);
        }
        while (!dispatch.IsCompleted)
        {
            next ??= fixture.Server.NextFrameAsync(ct).AsTask();
            if (await Task.WhenAny(dispatch, next) == dispatch)
                break;
            object packet = Decode(await next);
            next = null;
            packets.Add(packet);
            if (packet is ServerboundUseItemOnPacket use)
                await fixture.AcknowledgeAsync(use.Sequence, ct);
        }
        CmdResult result = await dispatch;
        const string marker = "useitem-order-marker";
        await fixture.Client.Game.Chat.SendAsync(marker, ct);
        while (true)
        {
            next ??= fixture.Server.NextFrameAsync(ct).AsTask();
            object packet = Decode(await next);
            next = null;
            if (packet is ServerboundSignedChatPacket { Message: marker })
                break;
            packets.Add(packet);
        }
        return (result, packets);
    }

}

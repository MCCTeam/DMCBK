using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using DMCBK.Testing.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Geometry;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using Umpk.Protocol.Java.Packets;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class BlockInteractionCommandTests
{
    [Theory]
    [InlineData("dig")]
    [InlineData("dig 2 64 1")]
    public async Task DigPreservesTheActualRaycastFace(string command)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 64, 3.5), ct);
        Assert.Equal(Direction.South, fixture.Hit.Face);
        if (command != "dig")
            await fixture.Client.Game.Movement.LookAtAsync(new Vec3d(2.5, 64.5, 8.5), ct);

        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync(command, ct), ct);
        ServerboundPlayerActionPacket start = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
        Assert.Equal(0, start.Action);
        await fixture.AcknowledgeAsync(start.Sequence!.Value, ct);
        await McPluginSessionHarness.SendAsync(fixture.Server, ProtocolPhase.Play,
            new ClientboundBlockUpdatePacket(Fixture.Target, 0), ct);
        await dispatch.WaitAsync(McPluginSessionHarness.Budget, ct);

        Assert.Equal(Fixture.Target, start.Position);
        Assert.Equal((byte)fixture.Hit.Face, start.Direction);
    }

    [Theory]
    [InlineData(64.0, 2.2, false)]
    [InlineData(63.0, 1.5, true)]
    public async Task UseBlockTransmitsTheEyeRayFaceHitPointAndInsideFlag(double feetY, double feetZ, bool inside)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, feetY, feetZ), ct);
        Assert.Equal(Direction.Up, fixture.Hit.Face);

        Task<CmdResult> dispatch = Task.Run(() => fixture.Client.Commands.DispatchAsync("useblock 2 64 1", ct), ct);
        ServerboundUseItemOnPacket use = await fixture.NextPacketAsync<ServerboundUseItemOnPacket>(ct);
        BlockRaycastHit? aimedHit = await fixture.Client.Game.World.RaycastAsync(4.5, false, ct);
        Assert.NotNull(aimedHit);
        await fixture.AcknowledgeAsync(use.Sequence, ct);
        await dispatch.WaitAsync(McPluginSessionHarness.Budget, ct);

        Assert.Equal(Fixture.Target, use.Position);
        Assert.Equal(inside, use.Inside);
        Assert.Equal((int)aimedHit.Face, use.Face);
        Assert.Equal((float)(aimedHit.Point.X - Fixture.Target.X), use.CursorX);
        Assert.Equal((float)(aimedHit.Point.Y - Fixture.Target.Y), use.CursorY);
        Assert.Equal((float)(aimedHit.Point.Z - Fixture.Target.Z), use.CursorZ);
    }

    [Theory]
    [InlineData("dig 2 64 1")]
    [InlineData("useblock 2 64 1")]
    public async Task CoordinateActionsReachTheRequestedCellAboveAContiguousFloor(string command)
    {
        using var budget = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = budget.Token;
        await using Fixture fixture = await Fixture.CreateAsync(new Vec3d(2.5, 65, 3.5), ct, contiguousFloor: true);
        Assert.Equal(Direction.Up, fixture.Hit.Face);

        CmdResult result = await Task.Run(() => fixture.Client.Commands.DispatchAsync(command, ct), ct).WaitAsync(McPluginSessionHarness.Budget, ct);

        Assert.Equal(CmdStatus.Done, result.Status);
        if (command.StartsWith("dig", StringComparison.Ordinal))
        {
            ServerboundPlayerActionPacket start = await fixture.NextPacketAsync<ServerboundPlayerActionPacket>(ct);
            Assert.Equal(Fixture.Target, start.Position);
            Assert.Equal((byte)Direction.Up, start.Direction);
        }
        else
        {
            ServerboundUseItemOnPacket use = await fixture.NextPacketAsync<ServerboundUseItemOnPacket>(ct);
            Assert.Equal(Fixture.Target, use.Position);
            Assert.Equal((int)Direction.Up, use.Face);
            Assert.Equal(1.0F, use.CursorY);
        }
    }

    private sealed class Fixture(FakeJavaServer server, Client client, BlockRaycastHit hit) : IAsyncDisposable
    {
        public static readonly BlockPos Target = new(2, 64, 1);
        public FakeJavaServer Server { get; } = server;
        public Client Client { get; } = client;
        public BlockRaycastHit Hit { get; } = hit;

        public static async Task<Fixture> CreateAsync(Vec3d position, CancellationToken ct, bool contiguousFloor = false)
        {
            FakeJavaServer server = FakeJavaServer.Create();
            string plugins = Path.Combine(Path.GetTempPath(), "dmcbk-block-face-" + Guid.NewGuid().ToString("N"));
            Client client = McPluginSessionHarness.BuildClient(server, "Tester", plugins, NullLoggerFactory.Instance);
            try
            {
                UmpkClient? session = null;
                client.ObserveSessionClients(value => session = value);
                Task starting = client.StartAsync(ct);
                await McPluginSessionHarness.DriveLoginAsync(server, ct);
                await starting.WaitAsync(McPluginSessionHarness.Budget, ct);
                Assert.NotNull(session);
                var spawn = new CommonPlayerSpawnInfo(0, "minecraft:overworld", 0, 1, -1, false, true, null, 0, 64);
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundLoginPacket(1, false, ["minecraft:overworld"], 5, 4, 4, false, true, false, spawn, OnlineMode: false, EnforcesSecureChat: false, Legacy: null), ct);
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundPlayerAbilitiesPacket(8, 0.05F, 0.1F), ct);
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundPlayerPositionPacket(position.X, position.Y, position.Z, 180, 29, 0, 1,
                        new PositionMoveRotation(position, default, 180, 29)), ct);
                Assert.True(JavaGameData.Registries(770).Blocks.TryGetValue(Identifier.Minecraft("stone"), out var stone));
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundBlockUpdatePacket(Target, stone.DefaultStateId), ct);
                if (contiguousFloor)
                    for (int z = 0; z <= 4; z++)
                        await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                            new ClientboundBlockUpdatePacket(new BlockPos(2, 64, z), stone.DefaultStateId), ct);
                while (!await session.InvokeAsync(value => value.State.HasWorld && value.State.World.GetColumn(Target) is not null && !value.State.World.GetBlock(Target).IsAir && (!contiguousFloor || !value.State.World.GetBlock(Target.Offset(0, 0, 3)).IsAir), ct))
                    await Task.Delay(10, ct);
                await client.Game.Movement.LookAtAsync(new Vec3d(2.5, 64.5, 1.5), ct);
                BlockRaycastHit? hit = await client.Game.World.RaycastAsync(4.5, false, ct);
                Assert.NotNull(hit);
                Assert.Equal(Target, hit.Position);
                return new Fixture(server, client, hit);
            }
            catch
            {
                await client.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        public async Task<TPacket> NextPacketAsync<TPacket>(CancellationToken ct) where TPacket : class
        {
            PhaseRegistry outbound = McPluginSessionHarness.Version.Protocol.GetRegistry(ProtocolPhase.Play, PacketFlow.Serverbound);
            while (true)
            {
                InboundFrame frame = await Server.NextFrameAsync(ct);
                Assert.True(outbound.TryGetInbound(frame.WireId, out BoundPacketCodec codec));
                if (codec.Decode(frame.Payload, PacketCodecContext.Registryless) is TPacket packet)
                    return packet;
            }
        }

        public Task AcknowledgeAsync(int sequence, CancellationToken ct) =>
            McPluginSessionHarness.SendAsync(Server, ProtocolPhase.Play, new ClientboundBlockChangedAckPacket(sequence), ct);

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); }
            finally { await Server.DisposeAsync(); }
        }
    }
}

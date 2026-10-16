using System.IO.Pipelines;
using DMCBK.Core.Tests.Fakes;
using DMCBK.Testing.Server;
using Umpk;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Data.Java;
using Umpk.Geometry;
using Umpk.Hosting;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using Umpk.Protocol.Java.Transport;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PoseTargetingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PacketlessAimUsesTheObservedEye(bool crawl)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using Fixture fixture = await Fixture.CreateAsync(crawl, budget.Token);
        PlayerPose before = await fixture.Movement.GetPoseAsync(budget.Token);
        var target = new Vec3d(before.Position.X, before.Position.Y + fixture.EyeHeight, before.Position.Z + 3);

        await fixture.Movement.LookAt(target, budget.Token);

        PlayerPose after = await fixture.Movement.GetPoseAsync(budget.Token);
        Assert.Equal(0, after.Yaw);
        Assert.Equal(0, after.Pitch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisibilityStartsAtTheObservedEye(bool crawl)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using Fixture fixture = await Fixture.CreateAsync(crawl, budget.Token);
        await fixture.Client.InvokeAsync(current =>
        {
            // A block update can remove the ceiling before the next physics tick changes the observed crawl pose.
            if (crawl)
                current.State.World.SetBlockStateId(new BlockPos(8, 65, 8), 0);
            int slab = current.State.Registries!.Blocks[Identifier.Minecraft("smooth_stone_slab")].Value.DefaultStateId;
            current.State.World.SetBlockStateId(new BlockPos(8, crawl ? 64 : 65, 9), slab);
            return true;
        }, budget.Token);
        var targetFeet = new Vec3d(8.5, 64 + fixture.EyeHeight - WorldApi.VisibilityAimHeight, 14.5);

        Assert.False(await fixture.World.HasLineOfSightAsync(targetFeet, budget.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoseReadKeepsTheObservedEyeHeight(bool crawl)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using Fixture fixture = await Fixture.CreateAsync(crawl, budget.Token);
        PlayerPose pose = await fixture.Movement.GetPoseAsync(budget.Token);
        Assert.Equal(fixture.EyeHeight, pose.EyeHeight);
        Assert.Equal(pose.Position.Add(0, fixture.EyeHeight, 0), pose.EyePosition);
    }

    [Theory]
    [InlineData(.4f, true)]
    [InlineData(1.27f, true)]
    [InlineData(1.54f, false)]
    [InlineData(1.62f, false)]
    public void FacingUsesTheCapturedEyeHeight(double eyeHeight, bool inFront)
    {
        var pose = new PlayerPose(Vec3d.Zero, 0, -90, true, false, false) { EyeHeight = eyeHeight };
        Assert.Equal(inFront, EntityTargeting.IsInFront(pose, new Vec3d(0, 1.4, 0)));
    }

    [Theory]
    [InlineData(.4f)]
    [InlineData(1.27f)]
    [InlineData(1.54f)]
    public void PoseCopiesKeepTheirHeightAndFollowNewFeet(double eyeHeight)
    {
        var pose = new PlayerPose(new Vec3d(8, 64, 8), 0, 0, true, false, false) { EyeHeight = eyeHeight };
        PlayerPose copy = pose with { Position = new Vec3d(-10, 128, 12) };
        Assert.Equal(new Vec3d(8, 64 + eyeHeight, 8), pose.EyePosition);
        Assert.Equal(new Vec3d(-10, 128 + eyeHeight, 12), copy.EyePosition);
    }

    [Fact]
    public async Task CanceledPacketlessAimLeavesRotationUnchanged()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using Fixture fixture = await Fixture.CreateAsync(false, budget.Token);
        PlayerPose before = await fixture.Movement.GetPoseAsync(budget.Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Movement.LookAt(new Vec3d(1, 1, 1), canceled.Token));
        Assert.Equal(before, await fixture.Movement.GetPoseAsync(budget.Token));
    }

    private sealed class Fixture(FakeJavaServer server, UmpkClient client, GameSession session, double eyeHeight) : IAsyncDisposable
    {
        public UmpkClient Client { get; } = client;
        public MovementApi Movement { get; } = new(session);
        public WorldApi World { get; } = new(session);
        public double EyeHeight { get; } = eyeHeight;

        public static async Task<Fixture> CreateAsync(bool crawl, CancellationToken ct)
        {
            var ticks = new ManualTickSource();
            FakeJavaServer server = FakeJavaServer.Create();
            JavaVersion version = McPluginSessionHarness.Version;
            UmpkClient client = new UmpkClientBuilder()
                .UseVersion(version)
                .UseProfile(new GameProfile(Guid.NewGuid(), "PoseTester"))
                .UseConnectionFactory(new PipeConnectionFactory(server.ClientPipe))
                .UseStaticRegistries(JavaGameData.Registries(version.Version.Protocol))
                .UseTickSource(ticks)
                .ConfigureFeatures(features => features.Pathfinding = false)
                .Build();
            var session = new GameSession();
            session.Bind(client);
            try
            {
                await Task.WhenAll(client.ConnectAsync(new ServerEndpoint("test", 25565), ct), McPluginSessionHarness.DriveLoginAsync(server, ct));
                var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using IDisposable join = client.Events.Subscribe<JoinedGame>(_ => joined.TrySetResult());
                var spawn = new CommonPlayerSpawnInfo(0, "minecraft:overworld", 0, 0, -1, false, true, null, 0, 63);
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundLoginPacket(1, false, ["minecraft:overworld"], 20, 8, 8, false, true, false, spawn, false, false, null), ct);
                await joined.Task.WaitAsync(ct);
                var corrected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using IDisposable correction = client.Events.Subscribe<PositionCorrected>(_ => corrected.TrySetResult());
                var position = new Vec3d(8.5, 64, 8.5);
                await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                    new ClientboundPlayerPositionPacket(position.X, position.Y, position.Z, 0, 0, 0, 1,
                        new PositionMoveRotation(position, Vec3d.Zero, 0, 0)), ct);
                await corrected.Task.WaitAsync(ct);
                await client.InvokeAsync(current =>
                {
                    int stone = current.State.Registries!.Blocks[Identifier.Minecraft("stone")].Value.DefaultStateId;
                    for (int x = 7; x <= 9; x++)
                        for (int z = 7; z <= 15; z++)
                            current.State.World.SetBlockStateId(new BlockPos(x, 63, z), stone);
                    if (crawl)
                        current.State.World.SetBlockStateId(new BlockPos(8, 65, 8), stone);
                    return true;
                }, ct);
                if (!crawl)
                    await client.Actions.Movement.SetSneakingAsync(true, ct);
                long before = await client.InvokeAsync(current => current.State.SessionTick, ct);
                ticks.Advance();
                while (!await client.InvokeAsync(current => current.State.SessionTick > before, ct))
                    await Task.Delay(1, ct);
                double eyeHeight = crawl ? .4f : 1.27f;
                Assert.Equal(eyeHeight, await client.InvokeAsync(current => current.SelfEyeHeight, ct));
                return new Fixture(server, client, session, eyeHeight);
            }
            catch
            {
                session.Unbind();
                await client.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            session.Unbind();
            try { await Client.DisposeAsync(); }
            finally { await server.DisposeAsync(); }
        }
    }

    private sealed class PipeConnectionFactory(IDuplexPipe pipe) : IConnectionFactory
    {
        public ValueTask<IDuplexPipe> ConnectAsync(ServerEndpoint endpoint, CancellationToken ct) => ValueTask.FromResult(pipe);
    }
}

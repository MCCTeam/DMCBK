using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk.Auth;
using Umpk.Game.Players;
using Umpk.Game.World;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>/entity attack</c> facing gate: attacks only land in front of the player unless the run opts out with <c>-a</c>, while <c>use</c> and every listing shape ignore facing.
/// <para>
/// The gate is the same hemisphere AutoAttack's <c>Only_In_Front</c> uses, over the live pose here instead of the plugin session state.
/// The geometry pins against <see cref="EntityCommand.IsInFront"/> directly; the grammar pins by dispatching the <c>-a</c> form with no session attached, where a full parse reaches the not-connected gate and a failed one dies on the offending token.
/// </para>
/// </summary>
public sealed class EntityFacingTests
{
    private static PlayerPose Pose(double x, double y, double z, float yaw, float pitch)
        => new(new Vec3d(x, y, z), yaw, pitch, OnGround: true, Sneaking: false, Sprinting: false);

    [Theory]
    [InlineData(0, 0, 0, 0, 5, true)]
    [InlineData(0, 0, 0, 0, -5, false)]
    [InlineData(180, 0, 0, 0, -5, true)]
    [InlineData(90, 0, -5, 0, 0, true)]
    [InlineData(90, 0, 5, 0, 0, false)]
    [InlineData(0, -90, 0, 10, 0, true)]
    [InlineData(0, 90, 0, -10, 0, true)]
    [InlineData(0, 0, 5, 0, 0, false)]
    public void IsInFront_IsAHemisphereAroundTheLookVector(
        float yaw, float pitch, double x, double y, double z, bool expected)
    {
        PlayerPose pose = Pose(0, 0, 0, yaw, pitch);
        var target = new Vec3d(x, y, z);
        Assert.Equal(expected, EntityTargeting.IsInFront(pose, target));
        Assert.Equal(expected, EntityCommand.IsInFront(pose, target));
    }

    [Fact]
    public void IsInFront_MeasuresFromTheFeet_PlusEyeHeight()
    {
        // Ten blocks up, looking level from y=64: the eye-to-target direction points up into the hemisphere, so it reads as ahead even though the feet-to-feet direction is straight up.
        Assert.True(EntityCommand.IsInFront(
            Pose(0, 64, 0, yaw: 0, pitch: 0), new Vec3d(0, 74, 5)));
        Assert.False(EntityCommand.IsInFront(
            Pose(0, 64, 0, yaw: 0, pitch: 0), new Vec3d(0, 74, -5)));
    }

    /// <summary>
    /// The occlusion half of the gate, over the same eye-to-mid-torso segment production casts (null shapes read blocks as full cubes): a stone wall on the lane blocks, water and empty lanes do not, and an unloaded chunk reads as air.
    /// </summary>
    [Fact]
    public void IsOccluded_BlocksOnStone_AndPassesOnAirWaterAndUnloaded()
    {
        Vec3d eye = PlayerReach.EyePosition(new Vec3d(0, 0, 0));
        var target = new Vec3d(0, 0, 5);

        Assert.False(WorldApi.IsOccluded(VisibilityWorldFixture.NewWorld(), eye, target, shapes: null));

        World walled = VisibilityWorldFixture.NewWorld();
        for (int y = 0; y <= 2; y++)
            VisibilityWorldFixture.SetStone(walled, new BlockPos(0, y, 2));

        Assert.True(WorldApi.IsOccluded(walled, eye, target, shapes: null));

        World water = VisibilityWorldFixture.NewWorld();
        for (int y = 0; y <= 2; y++)
            VisibilityWorldFixture.SetWater(water, new BlockPos(0, y, 2));

        Assert.False(WorldApi.IsOccluded(water, eye, target, shapes: null));
    }

    private sealed class SilentHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }

    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost", 25565)
            .UseHostInterface(new SilentHost())
            .Build();

    /// <summary>
    /// The <c>-a</c> opt-out parses on both selectors: with no session the run reaches the not-connected gate instead of dying on the flag token.
    /// </summary>
    [Theory]
    [InlineData("entity zombie attack -a")]
    [InlineData("entity near zombie attack -a")]
    [InlineData("entity 21339 attack -a")]
    [InlineData("entity zombie attack")]
    public async Task DashA_Parses_AndReachesTheSessionGate(string command)
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(command);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain("<--[HERE]", result.Message, StringComparison.Ordinal);
    }
}

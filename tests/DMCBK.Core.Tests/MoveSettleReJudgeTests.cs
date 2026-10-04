using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk;
using Umpk.Client;
using Umpk.Client.Navigation;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The bot can arrive (the server floors its position to the exact goal cell) but <c>move</c> prints neither the success line nor a failure line.
///
/// <para>
/// Both cases share a shape: <see cref="Navigator.Judge"/> is asked to verdict the move at the instant the navigation TASK reports completion, but the body is still settling (a downstream current's continuous push in one case, landing/grab momentum in a descend-plus-ladder-grab in the other), so the position <c>Judge</c> is handed is a tick or two stale: the SERVER's settled position floors into the goal cell, the position <c>Judge</c> saw at verdict time did not.
/// <c>Judge</c> is pure and correct given its input; the staleness is in WHEN <see cref="Navigator.MoveToVerifiedAsync"/> samples that input, which is UMPK's call (Umpk.Client/Navigation/Navigator.cs:148-159) and not fixed here.
/// </para>
///
/// <para>
/// <see cref="MoveCommand.ReJudge"/> is the client-side workaround: <c>DoMove</c> now gives a <c>StoppedShort</c> verdict a bounded second look, re-reading the pose and re-running this exact re-judge before it trusts the failure.
/// This file pins the DECISION half of that (a pure recompute against UMPK's own <c>Navigator.Judge</c>), which is the half a test can pin without a live session; see <see cref="Move_StillFailsCleanly_WhenTheBodyNeverSettlesIntoTheGoalCell"/> for the end-to-end half, which follows <c>MoveFailureReasonTests</c>' own precedent of exercising the real dispatcher against a bound-but-idle session rather than mocking the navigator.
/// </para>
/// </summary>
public sealed class MoveSettleReJudgeTests
{
    /// <summary>
    /// Ladder grab case: goal cell [647, 100, 138], the position <c>Judge</c> saw at verdict time was the pre-move start (a stand-in for "not there yet"), block-judged (not a sub-block request) and therefore <c>StoppedShort</c>.
    /// A fresher read at the actual final position [647.698, 100.0, 138.5], floors into the goal cell, so the re-judge upgrades it to reached.
    /// </summary>
    [Fact]
    public void ReJudge_UpgradesAStoppedShortVerdict_WhenTheFreshPositionFloorsIntoTheGoalCell()
    {
        var target = new Vec3d(647, 100, 138);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(650.5, 106.0, 138.5), subBlockRequest: false);
        Assert.False(stale.Reached);

        MoveResult fresh = MoveCommand.ReJudge(stale, new Vec3d(647.698, 100.0, 138.5));

        Assert.True(fresh.Reached);
    }

    /// <summary>
    /// Downstream swim case: goal cell [386, 100, 705], final settled position [386.7, 100.823, 705.5].
    /// Same upgrade, different medium (a swim under current rather than a descend-plus-grab), which is the point: the mechanism is generic to "the body kept moving after the verdict," not specific to one template.
    /// </summary>
    [Fact]
    public void ReJudge_UpgradesAStoppedShortVerdict_ForTheDownstreamSwimRowToo()
    {
        var target = new Vec3d(386, 100, 705);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(407.5, 103.0, 705.5), subBlockRequest: false);
        Assert.False(stale.Reached);

        MoveResult fresh = MoveCommand.ReJudge(stale, new Vec3d(386.7, 100.823, 705.5));

        Assert.True(fresh.Reached);
    }

    /// <summary>
    /// The upgrade only fires when the fresh position actually agrees.
    /// A body that is still short of the goal cell (as opposed to having drifted the rest of the way into it) re-judges to the same verdict, so the retry cannot manufacture an arrival that never happens.
    /// </summary>
    [Fact]
    public void ReJudge_KeepsTheVerdictAMiss_WhenTheFreshPositionStillDisagrees()
    {
        var target = new Vec3d(647, 100, 138);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(650.5, 106.0, 138.5), subBlockRequest: false);

        // One block short of the goal cell on X: still a miss.
        MoveResult stillShort = MoveCommand.ReJudge(stale, new Vec3d(646.2, 100.0, 138.5));

        Assert.False(stillShort.Reached);
    }

    /// <summary>
    /// A sub-block request (<c>move center</c>, or any target inside the block the player already occupies) is judged by horizontal distance rather than by block, per <see cref="Navigator.Judge"/>'s own contract.
    /// The re-judge has to carry that classification over from the original verdict rather than re-deriving it, since a fresh position alone cannot say whether the request ever left its start block.
    /// </summary>
    [Theory]
    [InlineData(100.55, 80.0, 100.52, true)] // horizontal distance ~0.054, inside the 0.1 tolerance
    [InlineData(100.70, 80.0, 100.50, false)] // horizontal distance 0.2, outside it
    public void ReJudge_UsesDistanceForASubBlockRequest(double x, double y, double z, bool expectedReached)
    {
        var target = new Vec3d(100.5, 80, 100.5);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(100.12, 80.0, 100.12), subBlockRequest: true);
        Assert.False(stale.Reached);

        MoveResult fresh = MoveCommand.ReJudge(stale, new Vec3d(x, y, z));

        Assert.Equal(expectedReached, fresh.Reached);
    }

    /// <summary>
    /// The re-judge carries the original request's target and sub-block classification forward untouched; only the position is fresh.
    /// A version that accidentally re-derived either from the fresh position would silently change what "reached" means for the very verdict it is supposed to be re-checking.
    /// </summary>
    [Fact]
    public void ReJudge_PreservesTheOriginalTargetAndSubBlockClassification()
    {
        var target = new Vec3d(8, 64, 8);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(0, 64, 0), subBlockRequest: true);

        MoveResult fresh = MoveCommand.ReJudge(stale, new Vec3d(1, 1, 1));

        Assert.Equal(target, fresh.Target);
        Assert.True(fresh.SubBlockRequest);
    }

    /// <summary>
    /// The re-judge carries <c>DestinationUnstandable</c> forward as well, and this is not bookkeeping.
    /// <see cref="Navigator.Judge"/> is pure: it compares two points and cannot look at the world, so a fresh judgement always comes back with the flag at its default.
    /// The fact was established once, with a session, by <c>MoveToVerifiedAsync</c>, and it is a property of the DESTINATION rather than of the pose, so it survives a re-read of the pose.
    /// A version that dropped it would send every settled near-stop back to the generic failure line, which is the line this change exists to stop printing.
    /// </summary>
    [Fact]
    public void ReJudge_CarriesTheUnstandableDestinationForward()
    {
        var target = new Vec3d(-295, -14, 393);
        MoveResult near = Navigator.Judge(target, arrival: new Vec3d(-295.5, -14.0, 393.5), subBlockRequest: false)
            with
        { DestinationUnstandable = true };
        Assert.Equal(MoveOutcome.StoppedNear, near.Outcome);

        MoveResult fresh = MoveCommand.ReJudge(near, new Vec3d(-295.52, -14.0, 393.48));

        Assert.False(fresh.Reached);
        Assert.True(fresh.DestinationUnstandable);
        Assert.Equal(MoveOutcome.StoppedNear, fresh.Outcome);
    }

    /// <summary>
    /// The other direction, which is what keeps the flag from becoming a sticky lie: a verdict the fresher position UPGRADES to an arrival is an arrival, full stop.
    /// Reaching the destination block is proof that a body fits in it, so carrying "nothing can stand there" onto that verdict would state the opposite of what just happened.
    /// </summary>
    [Fact]
    public void ReJudge_DropsTheUnstandableFlagWhenTheFreshPositionIsAnArrival()
    {
        var target = new Vec3d(647, 100, 138);
        MoveResult stale = Navigator.Judge(target, arrival: new Vec3d(650.5, 106.0, 138.5), subBlockRequest: false)
            with
        { DestinationUnstandable = true };

        MoveResult fresh = MoveCommand.ReJudge(stale, new Vec3d(647.698, 100.0, 138.5));

        Assert.True(fresh.Reached);
        Assert.False(fresh.DestinationUnstandable);
        Assert.Equal(MoveOutcome.Reached, fresh.Outcome);
    }

    /// <summary>
    /// The sentence itself, pinned whole.
    /// It names the block that cannot be stood in, names where the body actually stopped, says outright that this is not the destination that was asked for, and does NOT suggest <c>-f</c>: no flag makes a cell occupiable, so that advice would send the user to re-run a command that cannot behave any differently.
    /// </summary>
    [Fact]
    public void TheStoppedNearLineNamesBothBlocksAndDoesNotSuggestForce()
    {
        string line = CommandStrings.MoveStoppedNextToIt(
            "X:-295.00 Y:-14.00 Z:393.00", "X:-295.50 Y:-14.00 Z:393.50");

        Assert.Equal(
            "Nothing can stand in the block at X:-295.00 Y:-14.00 Z:393.00, so the walk stopped in the "
            + "nearest block that can hold a body: X:-295.50 Y:-14.00 Z:393.50. That is not the destination"
            + " you asked for.",
            line);
        Assert.DoesNotContain("-f", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to compute", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end through the real dispatcher against a bound-but-idle session (the same trick <see cref="MoveFailureReasonTests"/> uses): a body that never moves at all still, correctly, ends in the legacy failure line once the settle-retry window in <c>DoMove</c> runs out.
    /// This is the guard against the obvious way the fix could go wrong: a retry loop that never terminates, or one that reports success regardless of where the body actually is.
    /// </summary>
    [Fact]
    public async Task Move_StillFailsCleanly_WhenTheBodyNeverSettlesIntoTheGoalCell()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .Build();
        await using UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        CmdResult result = await client.Commands.DispatchAsync("move center");

        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.StartsWith("Failed to compute path to", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A built-but-unconnected UMPK client.
    /// Same trick <c>MoveFailureReasonTests</c> and <c>FalseSuccessReportingTests</c> use: capabilities resolve from the version at construction, so a bound-but-idle session lets the command's real branches run without a server.
    /// </summary>
    private static UmpkClient IdleUmpkClient()
    {
        Assert.True(Umpk.Data.Java.JavaVersions.TryGetByName("1.21.5", out Umpk.Protocol.Java.JavaVersion? version));
        return new UmpkClientBuilder()
            .UseVersion(version!)
            .UseProfile(new GameProfile(Guid.NewGuid(), "Tester"))
            .Build();
    }
}

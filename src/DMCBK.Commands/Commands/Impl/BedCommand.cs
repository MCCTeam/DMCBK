using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>bed</c> command: sleep in the bed at a coordinate, walk to the nearest bed within a radius and sleep in it, or leave the bed.
/// Ported from the legacy <c>MinecraftClient/Commands/Bed.cs</c>, including its search/arrival/use reporting.
/// <para>
/// The one deliberate departure is the legacy busy-wait: legacy spawned a fire-and-forget task that spun a CPU core polling the player position for 60 seconds and reported <c>Done</c> before it had arrived (Bed.cs:113-146).
/// This awaits the verified move instead, so the reported result is the real one.
/// </para>
/// </summary>
public sealed class BedCommand : CommandBase
{
    private static readonly string[] BedColors =
    [
        "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
        "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black",
    ];

    /// <inheritdoc/>
    public override string CmdName => "bed";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.bed.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "bed leave|sleep <x> <y> <z>|sleep <radius>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("sleep <x> <y> <z>", "sleep in the bed at a position"),
        new("sleep <radius>", "find a bed within a radius and sleep"),
        new("leave", "get out of bed"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["bed sleep 150 79 380", "bed sleep 8", "bed leave"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["respawn", "useblock"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy also accepted "help bed leave|sleep"; both rendered the same description (Bed.cs:20-28).
        help.ThenLiteral("leave", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("sleep", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .ThenLiteral("leave", h => h.Executes(ctx => DoLeaveBed(ctx.Source)))
            .ThenLiteral("sleep", h => h
                .ThenArgument("Location", DmcbkArguments.Location(), a => a
                    .Executes(ctx => DoSleepBedWithLocation(ctx.Source, ctx.GetArgument<CommandLocation>("Location"))))
                .ThenArgument("Radius", Arguments.Double(), a => a
                    .Executes(ctx => DoSleepBedWithRadius(ctx.Source, ctx.GetArgument<double>("Radius")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// The port of legacy <c>DoLeaveBed</c> (Bed.cs:56-60).
    /// Deliberately not behind the terrain gate that <c>sleep</c> uses, as in legacy: waking up is a single player-command packet, needs no block lookup or movement, and its action is bound on every protocol from 1.8 to 26.2.
    /// </summary>
    private static int DoLeaveBed(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        try
        {
            ctx.Run(ct => ctx.Game.Movement.LeaveBedAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Legacy reported the same line with the send's own success flag (SetAndReturn(text, sent)).
            return ctx.Result.Set(CmdStatus.Fail, McStrings.Get("cmd.bed.leaving"));
        }

        return ctx.Result.Ok(McStrings.Get("cmd.bed.leaving"));
    }

    /// <summary>
    /// The port of legacy <c>DoSleepBedWithRadius</c> (Bed.cs:62-152): announce the search, take the nearest bed of any color, refuse an unloaded chunk, walk there, then use it.
    /// </summary>
    private static int DoSleepBedWithRadius(CommandContext ctx, double radius)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        ctx.Output.WriteLine(McStrings.Format("cmd.bed.searching", radius));

        BlockPos? nearest;
        try
        {
            nearest = ctx.Run(ct => ctx.Game.World.FindNearestAsync(BedIds, Math.Max(1, (int)Math.Ceiling(radius)), ct));
        }
        catch (DmcbkFeatureDisabledException)
        {
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }

        if (nearest is not { } bed)
            return ctx.Result.Fail(McStrings.Get("cmd.bed.bed_not_found"));

        ctx.Output.WriteLine(McStrings.Format("cmd.bed.found_a_bed_at", bed.X, bed.Y, bed.Z));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        if (ChunkGate(ctx, pose.Position, bed) is { } chunkFailure)
            return chunkFailure;

        if (!WalkToBed(ctx, bed, out int failure))
            return failure;

        // Arrival is judged by vanilla's own rule rather than by the navigator's opinion: a planner can report success at a spot the server still calls TOO_FAR_AWAY, and the use packet would then be rejected with nothing to explain why.
        PlayerPose arrived = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        if (!BedInRange(arrived.Position, bed))
            return ctx.Result.Fail(McStrings.Format("cmd.bed.failed_to_reach_in_time", bed.X, bed.Y, bed.Z));

        ctx.Output.WriteLine(McStrings.Format("cmd.bed.moving", bed.X, bed.Y, bed.Z));
        return ctx.Result.Ok(McStrings.Format(
            "cmd.bed.trying_to_use",
            bed.X,
            bed.Y,
            bed.Z,
            UseBed(ctx, bed) ? McStrings.Get("cmd.bed.in") : McStrings.Get("cmd.bed.not_in")));
    }

    /// <summary>
    /// Walks to within reach of the bed, through the PLANNER when pathfinding is available.
    /// <para>
    /// Two things were wrong here.
    /// It asked for the bed's own block as the destination, and a bed is a solid block a player cannot stand in, so the request was for somewhere unreachable by construction: on flat ground with the bed four blocks away, "Can not reach the bed safely!".
    /// And it used the straight-line physics move rather than the planner, so anything between the player and the bed ended the attempt even when a path existed.
    /// Legacy's <c>handler.MoveTo</c> was the planner-backed call (Bed.cs:113), so this is a port that lost the planner rather than a deliberate difference.
    /// </para>
    /// <para>
    /// The destination is now NEAR the bed, not the bed: <see cref="BedApproachRange"/> blocks, which sits inside vanilla's own sleeping range (see <see cref="BedInRange"/>) with room for the planner to pick whichever adjacent column it can actually stand on.
    /// The physics move stays as the fallback for a session with pathfinding disabled, and it aims at the same nearby target for the same reason.
    /// </para>
    /// </summary>
    private static bool WalkToBed(CommandContext ctx, BlockPos bed, out int failure)
    {
        try
        {
            if (ctx.Run(ct => ctx.Game.Movement.IsPathfindingAvailableAsync(ct)))
            {
                ctx.Run(ct => ctx.Game.Movement.NavigateToAsync(bed, BedApproachRange, ct));
                failure = 0;
                return true;
            }

            MoveResult move = ctx.Run(ct => ctx.Game.Movement.MoveToVerifiedAsync(ApproachTarget(ctx, bed), ct));
            if (!move.Reached)
            {
                // Legacy's own giving-up line, now reported as the command's result: legacy printed it from a background task long after the command had already answered Done (Bed.cs:135-139).
                failure = ctx.Result.Fail(McStrings.Format("cmd.bed.failed_to_reach_in_time", bed.X, bed.Y, bed.Z));
                return false;
            }
        }
        catch (DmcbkFeatureDisabledException)
        {
            failure = ctx.Result.Set(CmdStatus.FailNeedTerrain);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Legacy's "MoveTo returned false" answer: a path it could not compute or could not run.
            failure = ctx.Result.Fail(McStrings.Get("cmd.bed.cant_reach_safely"));
            return false;
        }

        failure = 0;
        return true;
    }

    /// <summary>
    /// A spot beside the bed for the planner-less fallback: one block back along whichever axis the player already stands on the far side of, so the target is not the bed's own occupied block.
    /// </summary>
    private static Vec3d ApproachTarget(CommandContext ctx, BlockPos bed)
    {
        Vec3d from = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
        Vec3d target = bed.BottomCenter;
        double dx = from.X - target.X;
        double dz = from.Z - target.Z;

        return Math.Abs(dx) >= Math.Abs(dz)
            ? new Vec3d(target.X + Math.Sign(dx == 0 ? 1 : dx), target.Y, target.Z)
            : new Vec3d(target.X, target.Y, target.Z + Math.Sign(dz == 0 ? 1 : dz));
    }

    /// <summary>
    /// Vanilla's own test for whether a bed can be slept in from where the player stands: <c>ServerPlayer.isReachableBedBlock</c> (1.21.11-client-decompiled ServerPlayer.java:1238-1241) measures against the block's BOTTOM CENTRE and allows 3 blocks horizontally on each axis and 2 vertically.
    /// Checked before the use packet goes out so a rejection is reported as "did not get there" rather than as a use that silently did nothing.
    /// </summary>
    private static bool BedInRange(Vec3d player, BlockPos bed)
    {
        Vec3d centre = bed.BottomCenter;
        return Math.Abs(player.X - centre.X) <= 3.0
            && Math.Abs(player.Y - centre.Y) <= 2.0
            && Math.Abs(player.Z - centre.Z) <= 3.0;
    }

    /// <summary>
    /// How close the planner is asked to get.
    /// Two blocks: inside vanilla's three-block sleeping range with a margin, and loose enough that the planner may choose any standable column around the bed instead of one exact square it might not be able to occupy.
    /// </summary>
    private const int BedApproachRange = 2;

    /// <summary>
    /// The port of legacy <c>DoSleepBedWithLocation</c> (Bed.cs:154-176): use the bed at the given coordinate where the player stands.
    /// Legacy did NOT walk to it, and neither does this.
    /// </summary>
    private static int DoSleepBedWithLocation(CommandContext ctx, CommandLocation location)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d absolute = location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch);
        BlockPos block = BlockPos.Containing(absolute);

        // Legacy Location.ToCenter() (Location.cs:202-205) centres X and Z only; Y stays the block's own.
        var center = new Vec3d(Math.Floor(absolute.X) + 0.5, absolute.Y, Math.Floor(absolute.Z) + 0.5);

        BlockInfo info;
        try
        {
            info = ctx.Run(ct => ctx.Game.World.GetBlockAsync(block, ct));
        }
        catch (DmcbkFeatureDisabledException)
        {
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }

        if (!IsBed(info.BlockId))
            return ctx.Result.Fail(McStrings.Format("cmd.bed.not_a_bed", center.X, center.Y, center.Z));

        return ctx.Result.Ok(McStrings.Format(
            "cmd.bed.trying_to_use",
            center.X,
            center.Y,
            center.Z,
            UseBed(ctx, block) ? McStrings.Get("cmd.bed.in") : McStrings.Get("cmd.bed.not_in")));
    }

    /// <summary>
    /// The port of legacy <c>Movement.CheckChunkLoading</c> (Movement.cs:703-714) as the bed command used it (Bed.cs:109-111): both the player's chunk and the bed's chunk must be loaded.
    /// </summary>
    private static int? ChunkGate(CommandContext ctx, Vec3d start, BlockPos goal)
    {
        bool loaded;
        try
        {
            loaded = ctx.Run(ct => ctx.Game.World.GetBlockAsync(goal, ct)).ChunkLoaded
                && ctx.Run(ct => ctx.Game.World.GetBlockAsync(BlockPos.Containing(start), ct)).ChunkLoaded;
        }
        catch (DmcbkFeatureDisabledException)
        {
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }

        return loaded
            ? null
            : (int?)ctx.Result.Set(
                CmdStatus.FailChunkNotLoad,
                McStrings.Format("cmd.move.chunk_not_loaded", goal.X, goal.Y, goal.Z));
    }

    /// <summary>
    /// Uses the bed block, the port of legacy <c>McClient.PlaceBlock(location, Direction.Down)</c> (McClient.cs:3058-3069).
    /// The returned flag is what legacy's flag was: whether the placement/use packet was sent, NOT whether the server put the player to bed.
    /// Vanilla reports nothing back that would say the latter.
    /// </summary>
    private static bool UseBed(CommandContext ctx, BlockPos bed)
    {
        try
        {
            ctx.Run(ct => ctx.Game.World.PlaceBlockAsync(bed, Direction.Down, new Vec3d(0.5, 0.5, 0.5), Hand.Main, ct));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The 16 bed block identifiers (every color), built once so the search is a single scan.</summary>
    private static readonly IReadOnlyList<Umpk.Identifier> BedIds = BuildBedIds();

    private static IReadOnlyList<Umpk.Identifier> BuildBedIds()
    {
        var ids = new Umpk.Identifier[BedColors.Length];
        for (int i = 0; i < BedColors.Length; i++)
            ids[i] = Umpk.Identifier.Parse($"minecraft:{BedColors[i]}_bed");

        return ids;
    }

    /// <summary>True when a block id names a bed (any color), the port of legacy <c>Material.IsBed()</c>.</summary>
    public static bool IsBed(string blockId)
    {
        ArgumentNullException.ThrowIfNull(blockId);
        return blockId.EndsWith("_bed", StringComparison.OrdinalIgnoreCase)
            || blockId.Equals("minecraft:bed", StringComparison.OrdinalIgnoreCase);
    }
}

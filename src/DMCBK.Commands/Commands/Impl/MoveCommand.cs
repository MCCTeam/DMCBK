using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>move</c> command: toggle terrain/movement handling, report or query gravity, step one block in a direction, recentre in the current block, walk to a coordinate, or print the current position.
/// Ported from the legacy <c>MinecraftClient/Commands/Move.cs</c>, including its <c>on</c>/<c>off</c>, <c>gravity [on|off]</c> and <c>-f</c> grammar.
/// </summary>
public sealed class MoveCommand : CommandBase
{
    /// <summary>The legacy inspection range of the directional step, one block (Movement.cs:662-665).</summary>
    private const int StepLength = 1;

    /// <summary>
    /// How long <see cref="SettleAndReJudge"/> keeps re-checking a <c>StoppedShort</c> verdict before it gives up and reports the failure.
    /// The wait lets residual motion settle so a later position read reflects where the body stopped.
    /// </summary>
    private static readonly TimeSpan SettleRetryBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>How often <see cref="SettleAndReJudge"/> re-reads the pose within <see cref="SettleRetryBudget"/>.</summary>
    private static readonly TimeSpan SettleRetryInterval = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc/>
    public override string CmdName => "move";

    /// <summary>
    /// The legacy two-part description (Move.cs:14): the command description, then the <c>-f</c> label spelled out.
    /// The separator is the legacy call site's own, verbatim.
    /// </summary>
    // Just the description.
    // What -f does is stated once, in the FLAGS block of the help page (see Flags), rather than appended to a line that is read as a column in the index.
    public override string CmdDesc => McStrings.Get("cmd.move.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "move <on|off|get|up|down|east|west|north|south|center|x y z|gravity [on|off]> [-f]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<x> <y> <z>", "path there, avoiding hazards"),
        new("<direction>", "one step: up down east west north south"),
        new("on", "start walking forward"),
        new("off", "stop walking"),
        new("center", "centre on the current block"),
        new("get", "print your position"),
        new("gravity [on|off]", "show or set gravity"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("-f", "force unsafe moves: falling, fire, lava"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain | CommandFeature.Physics | CommandFeature.Pathfinding;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["move 150 80 380", "move north -f", "move gravity off"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["look", "blockinfo"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy also accepted "help move enable|gravity|direction|center|get|location|-f" (Move.cs:18-36); every one of them rendered the same description.
        help.ThenLiteral("enable", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("gravity", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("direction", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("center", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("get", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("location", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("-f", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        // Every direction takes the legacy optional "-f" force flag (Move.cs:49-72).
        static Action<CommandNodeBuilder<CommandContext>> Step(Direction direction) => node => node
            .Executes(ctx => MoveOnDirection(ctx.Source, direction, takeRisk: false))
            .ThenLiteral("-f", f => f.Executes(ctx => MoveOnDirection(ctx.Source, direction, takeRisk: true)));

        builder.Literal(CmdName, l => l
            // The legacy root had no body (a bare "move" was an incomplete command); reporting the position is kept from this tree's own grammar, and is what "move get" does.
            .Executes(ctx => GetCurrentLocation(ctx.Source))
            .ThenLiteral("on", h => h.Executes(ctx => SetMovementEnable(ctx.Source, enable: true)))
            .ThenLiteral("off", h => h.Executes(ctx => SetMovementEnable(ctx.Source, enable: false)))
            .ThenLiteral("gravity", h => h
                .Executes(ctx => SetGravityEnable(ctx.Source, enable: null))
                .ThenLiteral("on", a => a.Executes(ctx => SetGravityEnable(ctx.Source, enable: true)))
                .ThenLiteral("off", a => a.Executes(ctx => SetGravityEnable(ctx.Source, enable: false))))
            .ThenLiteral("up", Step(Direction.Up))
            .ThenLiteral("down", Step(Direction.Down))
            .ThenLiteral("east", Step(Direction.East))
            .ThenLiteral("west", Step(Direction.West))
            .ThenLiteral("north", Step(Direction.North))
            .ThenLiteral("south", Step(Direction.South))
            .ThenLiteral("center", h => h.Executes(ctx => MoveToCenter(ctx.Source)))
            .ThenLiteral("get", h => h.Executes(ctx => GetCurrentLocation(ctx.Source)))
            .ThenArgument("location", MccArguments.Location(), h => h
                .Executes(ctx => MoveToLocation(ctx.Source, ctx.GetArgument<CommandLocation>("location"), takeRisk: false))
                .ThenLiteral("-f", f => f
                    .Executes(ctx => MoveToLocation(ctx.Source, ctx.GetArgument<CommandLocation>("location"), takeRisk: true))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Turns terrain and movement handling on or off, the port of legacy <c>SetMovementEnable</c> (Move.cs:104-117) over <c>McClient.SetTerrainEnabled</c> (McClient.cs:1355-1376).
    /// Legacy enabling only REQUESTED terrain (it landed on the next login, respawn or world change) while disabling took effect at once, which is exactly what mutating the client's feature composition does here: UMPK composes a session's modules at build time from this very instance (MccSessionFactory.cs:221-231), so a session already running keeps its modules, while every command gate (<see cref="CommandContext.TerrainEnabled"/>) flips immediately.
    /// <para>
    /// Physics and pathfinding move with terrain because they depend on it: UMPK's <c>ClientFeatures.Normalized()</c> forces terrain back on for either of them, and the config validator states the same rule in the other direction (ConfigurationValidation.cs:160-169).
    /// Legacy carried one flag for "Terrain and Movements" together, so the whole movement stack is what the switch owns.
    /// </para>
    /// </summary>
    private static int SetMovementEnable(CommandContext ctx, bool enable)
    {
        ctx.Features.Terrain = enable;
        ctx.Features.Physics = enable;
        ctx.Features.Pathfinding = enable;
        return ctx.Result.Ok(McStrings.Get(enable ? "cmd.move.enable" : "cmd.move.disable"));
    }

    /// <summary>
    /// Reports the gravity state, the port of legacy <c>SetGravityEnable</c> (Move.cs:119-129).
    /// Legacy kept a client-side flag (<c>Settings.InternalConfig.GravityEnabled</c>) that its own one-block stepper read; UMPK's physics engine applies vanilla gravity unconditionally and exposes no switch for it, so there is nothing to set and the only honest answer is the state that actually holds.
    /// A request to disable it therefore reports the enabled line as a FAILURE rather than claiming a change it did not make.
    /// </summary>
    private static int SetGravityEnable(CommandContext ctx, bool? enable)
        => enable == false
            ? ctx.Result.Fail(McStrings.Get("cmd.move.gravity.enabled"))
            : ctx.Result.Ok(McStrings.Get("cmd.move.gravity.enabled"));

    /// <summary>
    /// Prints the current position in the legacy <c>Location.ToString()</c> shape, the port of <c>GetCurrentLocation</c> (Move.cs:131-138) including its terrain gate.
    /// </summary>
    private static int GetCurrentLocation(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        return ctx.Result.Ok(MovementCommandText.Position(pose.Position));
    }

    /// <summary>
    /// Walks to the centre of the block the player stands in, the port of <c>MoveToCenter</c> (Move.cs:140-150).
    /// The target is legacy <c>Location.ToCenter()</c> (Location.cs:202-205): X and Z centred, Y left exactly where the player already is.
    /// Legacy had no chunk check and no <c>-f</c> here, so neither does this.
    /// </summary>
    private static int MoveToCenter(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        Vec3d current = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
        var currentCenter = new Vec3d(Math.Floor(current.X) + 0.5, current.Y, Math.Floor(current.Z) + 0.5);

        return DoMove(
            ctx,
            currentCenter,
            takeRisk: false,
            MovementCommandText.Walk(currentCenter, current),
            MovementCommandText.Fail(currentCenter));
    }

    /// <summary>
    /// Steps one block in a direction, the port of <c>MoveOnDirection</c> (Move.cs:152-174): the terrain gate, the chunk-loading gate, then the move, reported as "Moving &lt;direction&gt;".
    /// </summary>
    private static int MoveOnDirection(CommandContext ctx, Direction direction, bool takeRisk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        Vec3d current = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
        Vec3d goal = current.Add(
            direction.StepX() * StepLength, direction.StepY() * StepLength, direction.StepZ() * StepLength);

        if (ChunkGate(ctx, current, goal) is { } notLoaded)
            return notLoaded;

        return DoMove(
            ctx,
            goal,
            takeRisk,
            McStrings.Format("cmd.move.moving", direction.ToString()),
            takeRisk
                ? McStrings.Get("cmd.move.dir_fail")
                : McStrings.Format("cmd.move.suggestforce", MovementCommandText.Position(goal)));
    }

    /// <summary>
    /// Walks to a coordinate, the port of <c>MoveToLocation</c> (Move.cs:176-200): the terrain gate, the tilde/absolute resolution against the current position, the chunk-loading gate, then the move.
    /// </summary>
    private static int MoveToLocation(CommandContext ctx, CommandLocation location, bool takeRisk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d current = pose.Position;

        // 2-arg ToAbsolute (not the local-throwing 1-arg overload): UMPK's coordinate grammar is a strict superset of the legacy one and now accepts a local (^) triple, which has no meaning without a facing to resolve it against.
        Vec3d goal = location.ToAbsolute(current, pose.Yaw, pose.Pitch);

        if (ChunkGate(ctx, current, goal) is { } notLoaded)
            return notLoaded;

        return DoMove(
            ctx,
            goal,
            takeRisk,
            MovementCommandText.Walk(goal, current),
            takeRisk
                ? MovementCommandText.Fail(goal)
                : MovementCommandText.SuggestForce(goal));
    }

    /// <summary>
    /// The legacy chunk-loading precondition (<c>Movement.CheckChunkLoading</c>, Movement.cs:703-714): both the column the player is in and the column the target is in have to be loaded before a step is attempted.
    /// Returns the failure code when either is missing, null when the move may proceed.
    /// </summary>
    private static int? ChunkGate(CommandContext ctx, Vec3d start, Vec3d goal)
    {
        bool loaded;
        try
        {
            loaded = ctx.Run(ct => ctx.Game.World.GetBlockAsync(BlockPos.Containing(goal), ct)).ChunkLoaded
                && ctx.Run(ct => ctx.Game.World.GetBlockAsync(BlockPos.Containing(start), ct)).ChunkLoaded;
        }
        catch (MccFeatureDisabledException)
        {
            // "move on" opens the command gates at once but the running session keeps the modules it was built with, so the world can still be absent until the next login.
            // That is the legacy answer too: enabling only took effect on the next login, respawn or world change.
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }

        return loaded
            ? null
            : (int?)ctx.Result.Set(
                CmdStatus.FailChunkNotLoad,
                McStrings.Format("cmd.move.chunk_not_loaded", goal.X, goal.Y, goal.Z));
    }

    /// <summary>
    /// Runs the verified move and reports what actually happened.
    /// A completed move TASK is not an arrival: the navigator falls back to the closest block it can finish in when nothing can stand in the destination block, and <c>Umpk.Client.Navigation.Navigator.MoveToVerifiedAsync</c> is what re-reads the player's own position and judges it against the request instead of reporting the fallback as a success.
    /// The legacy client never waited at all (it queued a path and printed its line straight away), so <paramref name="success"/> is the legacy line and it is only printed once the player is really there.
    /// <para>
    /// A <c>StoppedShort</c> verdict is rechecked before it is reported.
    /// The last segment's own completion tolerance is coarser than the exact destination block.
    /// Residual motion such as a swim current push or landing momentum can carry the body into the goal cell after the verdict is taken.
    /// <see cref="SettleAndReJudge"/> re-reads the pose over a short window and re-runs UMPK's own pure <c>Judge</c> against the fresher position.
    /// <c>Judge</c> is pure and correct given the position it was handed.
    /// </para>
    /// </summary>
    private static int DoMove(
        CommandContext ctx, Vec3d target, bool takeRisk, string success, string failure)
    {
        MoveResult result;
        try
        {
            // -f relaxes the planner.
            result = ctx.Run(ct => ctx.Game.Movement.MoveToVerifiedAsync(target, takeRisk, ct));
        }
        catch (MccFeatureDisabledException)
        {
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every failure the navigator raises here is a path it could not compute or could not run, which is what the legacy failure lines say.
            // The USER-VISIBLE line stays that legacy wording (parity is deliberate), but the reason is no longer thrown away with it: "no path to the goal", "replans exhausted" and "movement is held by 'Farmer'" all reduce to the same sentence on screen, and a transcript of that sentence cannot tell them apart.
            // The cause goes to the debug log instead, where the host's own level filtering decides who sees it.
            ctx.Logger.LogDebug(
                ex,
                "move to {Target} failed: {Reason}",
                MovementCommandText.Position(target),
                ex.Message);
            return ctx.Result.Fail(failure);
        }

        if (!result.Reached)
            result = SettleAndReJudge(ctx, result);

        if (result.Reached)
            return ctx.Result.Ok(success);

        // Still a FAILURE status, deliberately.
        // The user asked to be AT a cell and the body is not at it, and CmdResult.IsSuccess is what a script or a plugin branches on, so flipping it would be the fake success this project refuses.
        // What changes is the sentence: "nothing can stand there, and here is where I stopped instead" is actionable, while the -f suggestion attached to the ordinary failure is not, because no flag makes a cell occupiable.
        return ctx.Result.Fail(
            result.Outcome == MoveOutcome.StoppedNear ? MovementCommandText.StoppedNear(result) : failure);
    }

    /// <summary>
    /// Re-judges a move against a freshly read position, by re-applying UMPK's own pure <see cref="Navigator.Judge"/> under the original request's target and sub-block classification.
    /// Pure and free-standing (no clock, no session) so the upgrade rule itself is pinned by a plain unit test: <see cref="DoMove"/> only ever calls this on a verdict that was already <c>StoppedShort</c>, which is what makes the re-judge a one-way upgrade IN PRACTICE, but the function is a plain recompute and does not enforce that itself.
    /// </summary>
    internal static MoveResult ReJudge(MoveResult original, Vec3d freshPosition)
    {
        ArgumentNullException.ThrowIfNull(original);
        MoveResult judged = Navigator.Judge(original.Target, freshPosition, original.SubBlockRequest);

        // Judge is pure: it compares two points and cannot know whether the destination BLOCK is a place a body fits.
        // That fact was established once, with the world, by MoveToVerifiedAsync, and it is a property of the destination rather than of the pose, so a re-judge against a fresher position must carry it rather than silently reset it to the record's default.
        // Dropping it here turned every settled near-stop back into the generic failure line, which is the exact defect this command was changed to stop printing.
        return judged.Reached
            ? judged
            : judged with { DestinationUnstandable = original.DestinationUnstandable };
    }

    /// <summary>
    /// Gives a <c>StoppedShort</c> verdict a bounded second look: re-reads the pose every <see cref="SettleRetryInterval"/> for up to <see cref="SettleRetryBudget"/>, re-judging after each read, and returns as soon as one of those re-judges says reached (or the budget runs out, whichever is first).
    /// Only ever makes a miss into an arrival, never the reverse, because a verdict that was already <c>Reached</c> never reaches this method.
    /// The loop body is deliberately thin and delegates the actual decision to <see cref="ReJudge"/>, which is what a test can pin without a live session.
    /// </summary>
    private static MoveResult SettleAndReJudge(CommandContext ctx, MoveResult result) => ctx.Run(async ct =>
    {
        MoveResult current = result;
        DateTime deadline = DateTime.UtcNow + SettleRetryBudget;
        while (!current.Reached && DateTime.UtcNow < deadline)
        {
            await Task.Delay(SettleRetryInterval, ct).ConfigureAwait(false);
            Vec3d pose = (await ctx.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false)).Position;
            current = ReJudge(current, pose);
        }

        return current;
    });
}

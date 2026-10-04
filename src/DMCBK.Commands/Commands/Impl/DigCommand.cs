using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>dig</c> command: break the block you look at, or one at a coordinate.
///
/// <para>
/// The report is the legacy one: "Attempting to dig block at ...", set when the dig sequence went out, and <c>cmd.dig.fail</c> when it did not.
/// That wording is already honest about being a send (a survival player without enough mining progress, a protected region and a spectator all produce the same successful write), so this does not need the outcome-flavoured text that replaced it.
/// </para>
/// </summary>
public sealed class DigCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "dig";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.dig.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "dig <x> <y> <z>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "break the block you are looking at"),
        new("<x> <y> <z>", "break a specific block"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["dig", "dig 150 79 380"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["useblock", "blockinfo"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => DigLookAt(ctx.Source))
            .ThenArgument("Location", MccArguments.Location(), h => h
                .Executes(ctx => DigAt(ctx.Source, ctx.GetArgument<CommandLocation>("Location"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int DigLookAt(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        BlockRaycastHit? hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(4.5, false, ct));
        if (hit is not { } h)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.too_far"));

        // Legacy Dig.cs:79: the look-at form reports the block's own coordinates, not the centred ones.
        return Send(ctx, h.Position, h.BlockId, h.Position.X, h.Position.Y, h.Position.Z);
    }

    private int DigAt(CommandContext ctx, CommandLocation location)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d target = location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch);

        // Legacy Dig.cs:57-58: eye-to-target squared distance against a flat 25, measured on the requested (unfloored) coordinates.
        if ((target - PlayerReach.EyePosition(pose.Position)).LengthSqr() > 25)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.too_far"));

        BlockPos block = BlockPos.Containing(target);
        BlockInfo info = ctx.Run(ct => ctx.Game.World.GetBlockAsync(block, ct));
        if (info.IsAir)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.no_block"));

        // Legacy Dig.cs:64: the coordinate form reports the centred position (X/Z centred, Y untouched).
        return Send(ctx, block, info.BlockId, Math.Floor(target.X) + 0.5, target.Y, Math.Floor(target.Z) + 0.5);
    }

    private static int Send(CommandContext ctx, BlockPos block, string blockId, double reportX, double reportY, double reportZ)
    {
        try
        {
            // Direction.Down is the face the legacy command always dug against (Dig.cs:61 and :81).
            _ = ctx.Run(ct => ctx.Game.World.DigBlockAsync(block, Direction.Down, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(McStrings.Get("cmd.dig.fail"));
        }

        return ctx.Result.Ok(McStrings.Format(
            "cmd.dig.dig", reportX, reportY, reportZ, WorldCommandText.BlockName(ctx, blockId)));
    }
}

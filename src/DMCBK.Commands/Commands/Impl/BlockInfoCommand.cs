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

/// <summary>The <c>blockinfo</c> command: report the block at a coordinate, optionally with its six neighbours.</summary>
public sealed class BlockInfoCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "blockinfo";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.blockinfo.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "blockinfo <x> <y> <z> [-s]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<x> <y> <z>", "what the CLIENT believes is there"),
        new("<x> <y> <z> -s", "also report the six neighbours"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("-s", "include the blocks around the target"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["blockinfo 150 79 380", "blockinfo 150 79 380 -s"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["chunk", "dig"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy BlockInfo.cs:21-23 also answered "help blockinfo -s" (with the same text).
        help.ThenLiteral("-s", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source, null, false))
            .ThenLiteral("-s", h => h.Executes(ctx => Run(ctx.Source, null, true)))
            .ThenArgument("Location", MccArguments.Location(), h => h
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), false))
                .ThenLiteral("-s", a => a.Executes(ctx => Run(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), true))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, CommandLocation? location, bool reportSurrounding)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d target = location is { } loc
            ? loc.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch)
            : pose.Position;
        BlockPos block = BlockPos.Containing(target);

        string blockType = McStrings.Get("cmd.blockinfo.BlockType");
        ctx.Output.WriteLine($"{blockType}: {ReadName(ctx, block)}");

        if (reportSurrounding)
        {
            // Legacy BlockInfo.cs:66-91: the six neighbours in X/Y/Z order, positive before negative, each axis pair separated by a single-space line.
            string positive = McStrings.Get("cmd.blockinfo.Positive");
            string negative = McStrings.Get("cmd.blockinfo.Negative");
            ctx.Output.WriteLine($"{McStrings.Get("cmd.blockinfo.BlocksAround")}:");
            ctx.Output.WriteLine(Neighbour(ctx, "X", positive, block.Offset(1, 0, 0)));
            ctx.Output.WriteLine(Neighbour(ctx, "X", negative, block.Offset(-1, 0, 0)));
            ctx.Output.WriteLine(" ");
            ctx.Output.WriteLine(Neighbour(ctx, "Y", positive, block.Offset(0, 1, 0)));
            ctx.Output.WriteLine(Neighbour(ctx, "Y", negative, block.Offset(0, -1, 0)));
            ctx.Output.WriteLine(" ");
            ctx.Output.WriteLine(Neighbour(ctx, "Z", positive, block.Offset(0, 0, 1)));
            ctx.Output.WriteLine(Neighbour(ctx, "Z", negative, block.Offset(0, 0, -1)));
        }

        return ctx.Result.Set(CmdStatus.Done);
    }

    private static string Neighbour(CommandContext ctx, string axis, string sign, BlockPos position)
        => $"[{axis} {sign}] {McStrings.Get("cmd.blockinfo.BlockType")}: {ReadName(ctx, position)}";

    private static string ReadName(CommandContext ctx, BlockPos position)
    {
        BlockInfo block = ctx.Run(ct => ctx.Game.World.GetBlockAsync(position, ct));
        return WorldCommandText.BlockName(ctx, block.BlockId);
    }
}

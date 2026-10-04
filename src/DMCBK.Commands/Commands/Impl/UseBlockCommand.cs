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
/// The <c>useblock</c> command: right-click (use) a block at a coordinate.
///
/// <para>
/// Legacy aimed at the face nearest the player and turned to look at the block first (Useblock.cs:53-56), both of which decide what the server does with the interaction, so both are restored here.
/// </para>
/// </summary>
public sealed class UseBlockCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "useblock";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.useblock.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "useblock <x> <y> <z> [mainhand|offhand]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<x> <y> <z>", "right-click a block: chests, levers, doors"),
        new("<x> <y> <z> mainhand", "right-click with the main hand"),
        new("<x> <y> <z> offhand", "right-click with the off hand"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["useblock 150 79 380", "useblock 150 79 380 offhand"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["useitem", "dig"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenArgument("Location", DmcbkArguments.Location(), h => h
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Main))
                .ThenLiteral("mainhand", a => a.Executes(ctx => Run(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Main)))
                .ThenLiteral("offhand", a => a.Executes(ctx => Run(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Off))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, CommandLocation location, Hand hand)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        BlockPos block = BlockPos.Containing(location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch));
        Vec3d center = WorldCommandText.BlockCenter(block);
        Direction face = FaceNearestPlayer(pose.Position, center);

        bool used;
        try
        {
            ctx.Run(ct => ctx.Game.Movement.LookAtAsync(center, ct));
            ctx.Run(ct => ctx.Game.World.PlaceBlockAsync(block, face, WorldCommandText.FaceHitCursor(face), hand, ct));
            used = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            used = false;
        }

        // "succeeded" / "failed" are the legacy client's own literals inside this line (Useblock.cs:57); the 975-key corpus has no entry for them because legacy never translated them either.
        string message = McStrings.Format("cmd.useblock.use", center.X, center.Y, center.Z, used ? "succeeded" : "failed");
        return used ? ctx.Result.Ok(message) : ctx.Result.Fail(message);
    }

    /// <summary>
    /// The face of the block that points at the player, the port of legacy <c>Useblock.GetFaceNearestPlayer</c> (Useblock.cs:60-77).
    /// </summary>
    internal static Direction FaceNearestPlayer(Vec3d playerPosition, Vec3d blockCenter)
    {
        double dx = playerPosition.X - blockCenter.X;
        double dy = playerPosition.Y - blockCenter.Y;
        double dz = playerPosition.Z - blockCenter.Z;

        double absX = Math.Abs(dx);
        double absY = Math.Abs(dy);
        double absZ = Math.Abs(dz);

        if (absX >= absY && absX >= absZ)
            return dx >= 0 ? Direction.East : Direction.West;

        if (absY >= absZ)
            return dy >= 0 ? Direction.Up : Direction.Down;

        return dz >= 0 ? Direction.South : Direction.North;
    }
}

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
/// Uses the surface and hit point reached by the player's eye ray after turning toward the requested block.
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

        bool used;
        try
        {
            ctx.Run(ct => ctx.Game.Movement.LookAtAsync(block.Center, ct));
            BlockRaycastHit? hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(4.5, false, ct));
            if (hit is { } h && h.Position == block)
            {
                ctx.Run(ct => ctx.Game.World.PlaceBlockHitAsync(h, hand, ct));
                used = true;
            }
            else
                used = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            used = false;
        }

        // "succeeded" / "failed" are the legacy client's own literals inside this line (Useblock.cs:57); the 975-key corpus has no entry for them because legacy never translated them either.
        string message = McStrings.Format("cmd.useblock.use", center.X, center.Y, center.Z, used ? "succeeded" : "failed");
        return used ? ctx.Result.Ok(message) : ctx.Result.Fail(message);
    }

}

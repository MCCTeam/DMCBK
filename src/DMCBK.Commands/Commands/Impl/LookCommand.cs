using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>look</c> command: look in a direction, at an angle, at a coordinate, or inspect the block in the line of sight.
/// Ported from the legacy <c>MinecraftClient/Commands/Look.cs</c>.
/// </summary>
public sealed class LookCommand : CommandBase
{
    /// <summary>The legacy inspection range of the bare <c>look</c> command (Look.cs:74).</summary>
    private const double MaxDistance = 8.0;

    /// <summary>The legacy eye offset above the feet position (<c>Location.EyesLocation</c>, Location.cs:299-302).</summary>
    private const double EyeHeight = 1.62;

    /// <inheritdoc/>
    public override string CmdName => "look";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.look.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "look <x y z|yaw pitch|up|down|east|west|north|south>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<direction>", "up down east west north south"),
        new("<yaw> <pitch>", "aim at exact angles"),
        new("<x> <y> <z>", "aim at a block"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["look north", "look 150 80 380"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["move"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy also accepted "help look direction|angle|location" (Look.cs:18-28); all three rendered the same description.
        help.ThenLiteral("direction", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("angle", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("location", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => LogCurrentLooking(ctx.Source))
            .ThenLiteral("up", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.Up)))
            .ThenLiteral("down", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.Down)))
            .ThenLiteral("east", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.East)))
            .ThenLiteral("west", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.West)))
            .ThenLiteral("north", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.North)))
            .ThenLiteral("south", h => h.Executes(ctx => LookAtDirection(ctx.Source, Direction.South)))
            .ThenArgument("Yaw", Arguments.Float(), h => h
                .ThenArgument("Pitch", Arguments.Float(), a => a
                    .Executes(ctx => LookAtAngle(ctx.Source, ctx.GetArgument<float>("Yaw"), ctx.GetArgument<float>("Pitch")))))
            .ThenArgument("Location", DmcbkArguments.Location(), h => h
                .Executes(ctx => LookAtLocation(ctx.Source, ctx.GetArgument<CommandLocation>("Location"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Inspects the block in the line of sight, the port of <c>LogCurrentLooking</c> (Look.cs:68-86): an eight-block raycast that ignores fluids, reported with both distances the legacy line carries, from the feet and from the eyes, each measured to the block's centre.
    /// </summary>
    private static int LogCurrentLooking(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        BlockRaycastHit? hit;
        try
        {
            hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(MaxDistance, includeFluids: false, ct));
        }
        catch (DmcbkFeatureDisabledException)
        {
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        }

        if (hit is not { } block)
            return ctx.Result.Fail(McStrings.Format("cmd.look.noinspection", MaxDistance));

        Vec3d current = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;

        // Legacy Location.ToCenter() (Location.cs:202-205) centres X and Z only; Y stays the block's own.
        var targetCenter = new Vec3d(block.Position.X + 0.5, block.Position.Y, block.Position.Z + 0.5);

        return ctx.Result.Ok(McStrings.Format(
            "cmd.look.inspection",
            BlockName(ctx, block.BlockId),
            block.Position.X,
            block.Position.Y,
            block.Position.Z,
            current.Subtract(targetCenter).Length(),
            current.Add(0, EyeHeight, 0).Subtract(targetCenter).Length()));
    }

    /// <summary>
    /// Faces one of the six directions, the port of <c>LookAtDirection</c> (Look.cs:88-97) over <c>McClient.UpdateLocation(Location, Direction)</c> (McClient.cs:3747-3776), whose angle table this reproduces exactly.
    /// </summary>
    private static int LookAtDirection(CommandContext ctx, Direction direction)
    {
        (float yaw, float pitch) = FacingAngles(direction);
        return LookAtAngle(ctx, yaw, pitch);
    }

    /// <summary>Faces an explicit yaw/pitch pair, the port of <c>LookAtAngle</c> (Look.cs:99-108).</summary>
    private static int LookAtAngle(CommandContext ctx, float yaw, float pitch)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        ctx.Run(ct => ctx.Game.Movement.SetRotation(yaw, pitch, ct));
        return ctx.Result.Ok(McStrings.Format(
            "cmd.look.at",
            yaw.ToString("0.00", CultureInfo.CurrentCulture),
            pitch.ToString("0.00", CultureInfo.CurrentCulture)));
    }

    /// <summary>
    /// Faces a coordinate, the port of <c>LookAtLocation</c> (Look.cs:110-120).
    /// The triple is resolved against the player first (legacy read the raw parsed triple, so its own <c>~</c> forms aimed at the offsets themselves), and the aim itself is UMPK's eye-based <see cref="MovementApi.LookAtAsync"/>.
    /// </summary>
    private static int LookAtLocation(CommandContext ctx, CommandLocation location)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(MovementCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d target = location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch);
        ctx.Run(ct => ctx.Game.Movement.LookAt(target, ct));
        return ctx.Result.Ok(McStrings.Format("cmd.look.block", MovementCommandText.Position(target)));
    }

    /// <summary>The legacy per-direction angle table (McClient.cs:3747-3776).</summary>
    private static (float Yaw, float Pitch) FacingAngles(Direction direction) => direction switch
    {
        Direction.Up => (0f, -90f),
        Direction.Down => (0f, 90f),
        Direction.East => (270f, 0f),
        Direction.West => (90f, 0f),
        Direction.North => (180f, 0f),
        Direction.South => (0f, 0f),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>
    /// The block's display name, the port of legacy <c>Block.GetTypeString()</c> (Block.cs:116-121): the vanilla <c>block.&lt;namespace&gt;.&lt;path&gt;</c> translation, falling back to the raw id when the negotiated version's table has no entry (legacy fell back to the <c>Material</c> enum name, which this tree does not have: block identity is the registry id).
    /// </summary>
    private static string BlockName(CommandContext ctx, string blockId)
    {
        if (string.IsNullOrEmpty(blockId))
            return blockId;

        int colon = blockId.IndexOf(':', StringComparison.Ordinal);
        string key = colon < 0
            ? $"block.minecraft.{blockId}"
            : $"block.{blockId[..colon]}.{blockId[(colon + 1)..]}";
        return ctx.Translations.TryResolve(key, out string? name) ? name : blockId;
    }
}

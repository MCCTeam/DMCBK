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
/// Text and geometry helpers shared by the world commands, each one the port of a legacy helper the new tree had dropped: the "not connected" line the legacy console printed instead of running a command (<c>Program.cs:1059</c>), <c>Location.ToString()</c>, <c>Block.GetTypeString()</c> and the per-face hit cursor <c>Protocol18.GetFaceHitCursor</c> computed for every block placement.
/// </summary>
internal static class WorldCommandText
{
    /// <summary>
    /// The legacy offline reply.
    /// The legacy client never entered a command body without a session: the console loop answered with <c>mcc.disconnected</c> and stopped there (Program.cs:1059), so that is the wording the session guards use here.
    /// </summary>
    public static string NotConnected(CommandContext ctx)
        => CommandText.NotConnected(ctx);

    /// <summary>Renders a position exactly as legacy <c>Location.ToString()</c> did (Location.cs:435-438).</summary>
    public static string Position(Vec3d position)
        => string.Format(CultureInfo.CurrentCulture, "X:{0:0.00} Y:{1:0.00} Z:{2:0.00}", position.X, position.Y, position.Z);

    /// <summary>
    /// The block's display name, the port of legacy <c>Block.GetTypeString()</c> (Block.cs:116-121): the vanilla <c>block.&lt;namespace&gt;.&lt;path&gt;</c> translation, falling back to the raw id when the negotiated version's table has no entry (legacy fell back to the <c>Material</c> enum name, which this tree does not have: block identity is the registry id).
    /// </summary>
    public static string BlockName(CommandContext ctx, string blockId)
    {
        if (string.IsNullOrEmpty(blockId))
            return blockId;

        int colon = blockId.IndexOf(':', StringComparison.Ordinal);
        string key = colon < 0
            ? $"block.minecraft.{blockId}"
            : $"block.{blockId[..colon]}.{blockId[(colon + 1)..]}";
        return ctx.Translations.TryResolve(key, out string? name) ? name : blockId;
    }

    /// <summary>
    /// The hit position a block interaction reports for a face, the port of <c>Protocol18.GetFaceHitCursor</c> (Protocol18.cs:5950-5957).
    /// The legacy handler derived it from the face for every placement; UMPK takes the cursor explicitly, so the derivation lives at the call site.
    /// </summary>
    public static Vec3d FaceHitCursor(Direction face) => face switch
    {
        Direction.Up => new Vec3d(0.5, 1.0, 0.5),
        Direction.Down => new Vec3d(0.5, 0.0, 0.5),
        Direction.North => new Vec3d(0.5, 0.5, 0.0),
        Direction.South => new Vec3d(0.5, 0.5, 1.0),
        Direction.West => new Vec3d(0.0, 0.5, 0.5),
        _ => new Vec3d(1.0, 0.5, 0.5),
    };

    /// <summary>
    /// The block's centre as legacy <c>Location.ToCenter()</c> computed it (Location.cs:202-205): X and Z centred, Y left at the block's bottom.
    /// Several printed lines carry these exact numbers, so the asymmetry is deliberate.
    /// </summary>
    public static Vec3d BlockCenter(BlockPos block) => new(block.X + 0.5, block.Y, block.Z + 0.5);
}

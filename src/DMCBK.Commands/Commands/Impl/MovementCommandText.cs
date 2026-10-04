using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// Text the two movement commands share.
/// Kept here rather than pulled from a neighbouring command file so this file stands on its own; the consolidation pass can merge it with the identical helpers next door.
/// </summary>
internal static class MovementCommandText
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
    /// The move announcement, "Walking from &lt;current&gt; to &lt;target&gt;".
    /// Note the legacy corpus orders the placeholders target-first ("Walking from {1} to {0}"), so the arguments go in that order; getting them the natural way round silently swaps the two positions in the message.
    /// </summary>
    public static string Walk(Vec3d target, Vec3d current)
        => McStrings.Format("cmd.move.walk", Position(target), Position(current));

    /// <summary>The path-failure line, "Failed to compute path to &lt;target&gt;".</summary>
    public static string Fail(Vec3d target) => McStrings.Format("cmd.move.fail", Position(target));

    /// <summary>The unsafe-path line, which points at the -f flag.</summary>
    public static string SuggestForce(Vec3d target)
        => McStrings.Format("cmd.move.suggestforce", Position(target));

    /// <summary>
    /// The line for a move that finished BESIDE its destination because nothing can occupy the destination block.
    /// Reports both positions, because "next to it" is only useful to a reader who can see which block that turned out to be.
    /// </summary>
    /// <remarks>
    /// From <see cref="CommandStrings"/> rather than the generated corpus: the 1.x client had no such outcome and therefore no key to keep parity with.
    /// See <c>CommandStrings.MoveStoppedNextToIt</c>.
    /// </remarks>
    public static string StoppedNear(MoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return CommandStrings.MoveStoppedNextToIt(Position(result.Target), Position(result.StoppedAt));
    }
}

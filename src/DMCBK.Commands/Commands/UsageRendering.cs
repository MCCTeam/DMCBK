using System.Collections.Immutable;
using System.Text;

namespace DMCBK.Core.Commands;

/// <summary>
/// Renders one command's full usage listing (the body of <c>help &lt;command&gt;</c>) from the lines <see cref="Umpk.Commands.CommandService{TSource}.GetUsage(string, TSource)"/> returns.
/// Ported from the legacy Brigadier-backed <c>CommandDispatcherExtensions.GetAllUsageString</c>: each line is prefixed with the command char and name, and a <c>_help</c> redirect line is rewritten to read as a real <c>help</c> invocation.
/// The old swallow-to-empty <c>catch</c> is gone: UMPK's own <c>GetUsage</c> already returns an empty array for an unknown command instead of throwing.
/// </summary>
internal static class UsageRendering
{
    /// <summary>
    /// Renders <paramref name="lines"/> (one raw Brigadier usage line per branch of the command's subtree) into the full multi-line usage block, or the empty string when there is nothing to render.
    /// </summary>
    /// <param name="lines">The command's raw usage lines, as returned by UMPK's per-command <c>GetUsage</c>.</param>
    /// <param name="commandName">The command's own root literal name (omitted from each returned line).</param>
    /// <param name="prefix">The active internal-command prefix character.</param>
    /// <param name="noPrefix">True in <c>none</c> prefix mode, where no prefix character is ever shown.</param>
    public static string Render(ImmutableArray<string> lines, string commandName, char prefix, bool noPrefix)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        if (lines.IsDefaultOrEmpty)
            return string.Empty;

        string prefixText = noPrefix ? string.Empty : prefix.ToString();
        var sb = new StringBuilder();
        foreach (string usage in lines)
        {
            // `_help` is an internal redirect node every command mirrors so `<cmd> _help` reaches the same page as `help <cmd>`.
            // It is plumbing: nobody types it, and listing it put two lines of noise on all 44 pages (120 of the corpus's 460 lines).
            // Dropped from the rendering, not from the tree, so the redirect keeps working.
            if (usage.StartsWith('_'))
                continue;

            sb.Append(prefixText).Append(commandName);

            // A command whose ROOT is executable contributes an empty usage line, which rendered as the name plus a dangling space.
            // The bare form is a real branch worth listing, so it is listed, just without the space that made it look like a truncated line.
            if (usage.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            sb.Append(' ').AppendLine(usage);
        }

        if (sb.Length > 0)
            sb.Length--; // drop the trailing newline

        return sb.ToString();
    }
}

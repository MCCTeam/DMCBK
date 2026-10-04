using System.Text;
using DMCBK.Core.Localization;
using DMCBK.Core.Presentation;

namespace DMCBK.Core.Commands;

/// <summary>
/// Turns a parse failure into something a user can act on.
/// <para>
/// The raw Brigadier message points its caret at the position the parser stopped at.
/// For <c>/move nowhere</c> that is character 5, the space after <c>move</c>, not the word that is wrong.
/// The raw message also lists every branch of the command subtree, which buries the fix.
/// What the user needs instead is the offending token marked, two or three forms that would have worked, and a pointer to the full page.
/// </para>
/// </summary>
internal static class CommandDiagnostics
{
    private const int MaxSuggestions = 3;

    /// <summary>
    /// The recovery block for a command that exists but was typed wrongly.
    /// </summary>
    /// <param name="command">The command line as typed, without the prefix.</param>
    /// <param name="error">Brigadier's own message, if any.</param>
    /// <param name="target">The command that was named, when it could be resolved.</param>
    /// <param name="prefix">The active prefix (empty in <c>none</c> mode).</param>
    /// <param name="glyphs">The resolved glyph vocabulary.</param>
    public static string BadUsage(
        string command, string? error, CommandBase? target, string prefix, GlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(glyphs);

        string name = FirstToken(command);
        string rest = command.Length > name.Length ? command[name.Length..].TrimStart() : string.Empty;

        var sb = new StringBuilder();
        sb.Append("§c").Append(glyphs.Fail).Append(" §r§f").Append(prefix).Append(name);

        if (rest.Length > 0)
        {
            // Mark the token the parser choked on, not the position it stopped at.
            // The caret sits under the first token past the command name, which is what a person reads as "this word".
            string bad = FirstToken(rest);
            sb.Append(' ').Append("§c").Append(bad).Append("§r");

            // The caret has to clear the status glyph, the space after it, the prefix, the command name and the space before the bad token.
            // Glyph WIDTH, not length: an emoji is double-width.
            int caret = glyphs.StatusWidth + 1 + prefix.Length + name.Length + 1;
            sb.Append('\n').Append(new string(' ', caret))
              .Append("§c^ ").Append(Reason(error)).Append("§r");
        }
        else
            sb.Append("§r §c").Append(McStrings.Get("cmd.error.incomplete")).Append("§r");

        if (target is not null)
        {
            IReadOnlyList<string> tries = Suggestions(target);
            if (tries.Count > 0)
            {
                sb.Append('\n').Append("   §7").Append(McStrings.Get("cmd.error.try")).Append("§r  ")
                  .Append("§f").Append(string.Join("   ", tries.Select(t => prefix + t))).Append("§r");
            }

            sb.Append('\n').Append("   §7")
              .Append(McStrings.Format("cmd.error.full_syntax", $"{prefix}help {target.CmdName}")).Append("§r");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The notice for a line that named no internal command at all, when a close match exists.
    /// Returns null when nothing is close enough to be worth guessing at.
    /// </summary>
    /// <remarks>
    /// In slash mode an unrecognised internal command is forwarded to the server, which is correct (that is how a server-side <c>/tell</c> reaches the server) but means a TYPO produces no local output at all: <c>/helpp</c> looked like the client ignoring the user.
    /// This says what happened before forwarding.
    /// </remarks>
    public static string? NearMiss(
        string typed, IReadOnlyList<CommandBase> commands, string prefix, GlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(glyphs);

        string name = FirstToken(typed ?? string.Empty);
        if (name.Length < 2)
            return null;

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (CommandBase command in commands)
        {
            foreach (string candidate in new[] { command.CmdName }.Concat(command.Aliases))
            {
                int d = Distance(name, candidate);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }
            }
        }

        // One edit for a short word, two for a longer one.
        // Beyond that the "did you mean" is noise: the user typed something else entirely, and it is probably a real server command.
        int allowed = name.Length <= 4 ? 1 : 2;
        if (best is null || bestDistance > allowed)
            return null;

        return "§e" + glyphs.Warn + " §r"
            + McStrings.Format("cmd.error.unknown_did_you_mean", prefix + name, prefix + best);
    }

    /// <summary>Up to three concrete forms of a command, drawn from its examples then its usage rows.</summary>
    private static IReadOnlyList<string> Suggestions(CommandBase command)
    {
        var tries = new List<string>();
        foreach (string example in command.Examples)
        {
            if (tries.Count == MaxSuggestions)
                return tries;

            tries.Add(example);
        }

        foreach (UsageLine row in command.UsageLines)
        {
            if (tries.Count == MaxSuggestions)
                break;

            if (row.Syntax.Length > 0)
                tries.Add($"{command.CmdName} {row.Syntax}");
        }

        return tries;
    }

    /// <summary>
    /// A short reason for the caret line.
    /// Brigadier's message names a type and a position; the position is already expressed by the caret, so only the type half is worth keeping, and even that is generic enough that a plain "not valid here" is no worse when it cannot be extracted.
    /// </summary>
    private static string Reason(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return McStrings.Get("cmd.error.not_valid_here");

        int at = error.IndexOf(" at position", StringComparison.Ordinal);
        string head = at > 0 ? error[..at] : error;
        head = head.Trim();
        return head.Length == 0 ? McStrings.Get("cmd.error.not_valid_here") : head;
    }

    private static string FirstToken(string command)
    {
        ReadOnlySpan<char> span = command.AsSpan().TrimStart();
        int end = span.IndexOfAny(' ', '\t');
        return (end < 0 ? span : span[..end]).ToString();
    }

    /// <summary>Levenshtein distance, bounded by the shorter of the two rows.</summary>
    private static int Distance(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return 0;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

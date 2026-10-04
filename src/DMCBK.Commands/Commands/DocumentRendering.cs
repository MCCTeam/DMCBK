using System.Text;

namespace DMCBK.Core.Commands;

/// <summary>
/// Sends a Markdown document to whichever renderer the host has, and renders it as readable plain text when the host has none.
/// <para>
/// Introduced for <c>/man</c>, and used by any command whose output is a document rather than a line: <c>/entity</c>'s listing is a table of sixty rows, and a table is exactly the thing Markdown expresses and a hand-built string does not.
/// Going through here means such a command is written ONCE and comes out as a bordered table in the classic console, as aligned Consolonia text in the TUI, and as aligned plain text in a host with no renderer at all.
/// </para>
/// <para>
/// This is only ever reached from a command.
/// Server chat is a Minecraft component tree on its own path and is never treated as Markdown.
/// </para>
/// </summary>
internal static class DocumentRendering
{
    /// <summary>Renders a document through the host, falling back to plain text.</summary>
    public static void Write(CommandContext ctx, string markdown)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(markdown);

        if (ctx.Ui?.TryWriteDocument(markdown) != true)
            ctx.Output.WriteLine(PlainText(markdown));
    }

    /// <summary>
    /// Markdown reduced to readable plain text: headings uppercased, emphasis and code markers dropped, alert markers turned into a label, and tables ALIGNED into columns.
    /// </summary>
    /// <remarks>
    /// Aligning the tables is the part that matters.
    /// Simply stripping the pipes would turn a sixty-row entity listing into sixty ragged lines, which is the shape this whole change exists to replace.
    /// A host with no Markdown renderer still gets a table, just one drawn with spaces.
    /// </remarks>
    public static string PlainText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string[] lines = markdown.Split('\n');
        var sb = new StringBuilder(markdown.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');

            if (IsTableRow(line))
            {
                i = EmitTable(lines, i, sb) - 1;
                continue;
            }

            sb.AppendLine(Inline(line));
        }

        return sb.ToString().TrimEnd();
    }

    private static string Inline(string line)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
            return string.Empty;

        if (line.StartsWith('#'))
            line = line.TrimStart('#').Trim().ToUpperInvariant();
        else if (line.StartsWith("> [!", StringComparison.Ordinal))
        {
            int close = line.IndexOf(']');
            line = close > 0 ? line[4..close] + ":" : line;
        }
        else if (line.StartsWith('>'))
            line = "  " + line[1..].Trim();

        return line
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    /// <summary>
    /// Consumes the run of table rows starting at <paramref name="start"/>, writes them aligned, and returns the index of the first line after the table.
    /// </summary>
    private static int EmitTable(string[] lines, int start, StringBuilder sb)
    {
        var rows = new List<string[]>();
        int i = start;
        for (; i < lines.Length && IsTableRow(lines[i].TrimEnd('\r')); i++)
        {
            string[] cells = SplitRow(lines[i].TrimEnd('\r'));

            // The `| --- | --- |` separator carries no data; the rule below the header replaces it.
            if (cells.All(IsSeparatorCell))
                continue;

            rows.Add(cells);
        }

        if (rows.Count == 0)
            return i;

        int columns = rows.Max(r => r.Length);
        int[] widths = new int[columns];
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var line = new StringBuilder("  ");
            for (int c = 0; c < columns; c++)
            {
                string cell = c < rows[r].Length ? rows[r][c] : string.Empty;
                line.Append(c == columns - 1 ? cell : cell.PadRight(widths[c]));
                if (c < columns - 1)
                    line.Append("  ");
            }

            sb.AppendLine(line.ToString().TrimEnd());

            if (r == 0)
                sb.AppendLine("  " + new string('-', widths.Sum() + ((columns - 1) * 2)));
        }

        return i;
    }

    private static string[] SplitRow(string line)
    {
        string trimmed = line.Trim().Trim('|');
        return [.. trimmed.Split('|').Select(c => Inline(c.Trim()))];
    }

    private static bool IsSeparatorCell(string cell)
        => cell.Length > 0 && cell.All(ch => ch is '-' or ':' or ' ');
}

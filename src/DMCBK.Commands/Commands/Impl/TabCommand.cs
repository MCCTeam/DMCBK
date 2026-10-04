using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Commands;
using Umpk.Text;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>tab</c> command: shows a vanilla-like tab list, or the host's live overlay when it has one.
/// Ported from the legacy <c>MinecraftClient/Commands/Tab.cs</c> and its <c>MinecraftClient/TabList/TabListFormatter.cs</c> renderer.
/// </summary>
public sealed class TabCommand : CommandBase
{
    /// <summary>Legacy's table geometry (TabListFormatter.cs:26-28).</summary>
    private const int MaxRowsPerColumn = 20;

    private const int PingColumnWidth = 11;

    private const int ColumnGapWidth = 4;

    /// <inheritdoc/>
    public override string CmdName => "tab";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.tab.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "tab";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Entities;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "show the tab list; a live overlay in TUI mode"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["tab"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["list", "teams", "scoreboard"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowTab(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders the legacy text tab list: the coloured title, the server's header block, the ping/player table balanced across columns of at most twenty rows, then the footer block.
    /// <para>
    /// The team column legacy could add is not rendered: it is gated on <c>Console.TabList.ShowTeams</c>, a host console setting the core's configuration does not carry, and its default was off.
    /// The team a player belongs to is still read, because legacy ordered the rows by it before falling back to the name (TabListFormatter.cs:59-66).
    /// </para>
    /// </summary>
    /// <param name="snapshot">The tab-list snapshot.</param>
    /// <param name="scoreboard">The scoreboard snapshot, for the team each listed player belongs to.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    internal static string Render(TabListSnapshot snapshot, ScoreboardSnapshot scoreboard)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scoreboard);

        var teamOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (TeamInfo team in scoreboard.Teams)
        {
            foreach (string member in team.Members)
                teamOf[member] = team.Name;
        }

        var lines = new List<string>
        {
            $"§e{McStrings.Format("cmd.tab.title", snapshot.Entries.Count)}§r",
        };

        AppendSection(lines, snapshot.Header);

        List<TabListEntryInfo> listed =
        [
            .. snapshot.Entries
                .Where(static entry => entry.Listed && !string.IsNullOrWhiteSpace(entry.Name))
                .OrderBy(static entry => entry.ListOrder)
                .ThenBy(static entry => IsSpectator(entry) ? 1 : 0)
                .ThenBy(entry => teamOf.GetValueOrDefault(entry.Name, string.Empty), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        ];

        if (listed.Count == 0)
            lines.Add($"§7{McStrings.Get("cmd.tab.no_players")}§r");
        else
            lines.AddRange(BuildTableLines(listed));

        AppendSection(lines, snapshot.Footer);
        return string.Join('\n', lines);
    }

    private int ShowTab(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        // Legacy opened the live overlay on the TUI backend and reported it (Tab.cs:38-46); a host with no overlay falls through to the text table.
        if (ctx.Ui?.TryShowTabOverlay() == true)
            return ctx.Result.Set(CmdStatus.Done, McStrings.Get("cmd.tab.tui_opened"));

        TabListSnapshot list = ctx.Run(ct => ctx.Game.Player.GetTabListAsync(ct));
        ScoreboardSnapshot scoreboard = ctx.Run(ct => ctx.Game.Player.GetScoreboardAsync(ct));
        return ctx.Result.Set(CmdStatus.Done, Render(list, scoreboard));
    }

    private static bool IsSpectator(TabListEntryInfo entry)
        => string.Equals(entry.GameMode, "Spectator", StringComparison.OrdinalIgnoreCase);

    private static void AppendSection(List<string> lines, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }
    }

    private static List<string> BuildTableLines(IReadOnlyList<TabListEntryInfo> entries)
    {
        int columns = 1;
        int rows = entries.Count;
        while (rows > MaxRowsPerColumn)
        {
            columns++;
            rows = (entries.Count + columns - 1) / columns;
        }

        // §7, not §8: a column header labels the data under it, so it has to be legible. §8 is the vanilla dark grey (85,85,85), which on a dark terminal is barely above the background.
        string headerRow = BuildRow(
            $"§7{McStrings.Get("cmd.tab.column_ping")}§r",
            $"§7{McStrings.Get("cmd.tab.column_player")}§r");

        var renderedRows = new List<string>(entries.Count);
        foreach (TabListEntryInfo entry in entries)
            renderedRows.Add(BuildRow(PingCell(entry.Latency), PlayerLabel(entry)));

        int[] columnWidths = new int[columns];
        for (int column = 0; column < columns; column++)
        {
            int width = VisibleLength(headerRow);
            for (int row = 0; row < rows; row++)
            {
                int index = row + (column * rows);
                if (index >= renderedRows.Count)
                    break;

                width = Math.Max(width, VisibleLength(renderedRows[index]));
            }

            columnWidths[column] = width;
        }

        var headerCells = new string[columns];
        Array.Fill(headerCells, headerRow);

        var lines = new List<string> { CombineColumns(headerCells, columnWidths) };
        for (int row = 0; row < rows; row++)
        {
            var cells = new string[columns];
            for (int column = 0; column < columns; column++)
            {
                int index = row + (column * rows);
                cells[column] = index < renderedRows.Count ? renderedRows[index] : string.Empty;
            }

            lines.Add(CombineColumns(cells, columnWidths));
        }

        return lines;
    }

    private static string CombineColumns(IReadOnlyList<string> parts, IReadOnlyList<int> widths)
    {
        var sb = new StringBuilder();
        for (int index = 0; index < parts.Count; index++)
        {
            if (index > 0)
                sb.Append(' ', ColumnGapWidth);

            sb.Append(PadFormattedRight(parts[index], widths[index]));
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildRow(string pingCell, string playerCell)
        => $"{PadFormattedRight(pingCell, PingColumnWidth)}  {playerCell}";

    /// <summary>Legacy's five-bar ping gauge and right-aligned figure (TabListFormatter.cs:196-234).</summary>
    private static string PingCell(int ping)
    {
        string barColor;
        int filledBars;

        if (ping < 0)
        {
            barColor = "§8";
            filledBars = 0;
        }
        else if (ping < 150)
        {
            barColor = "§a";
            filledBars = 5;
        }
        else if (ping < 300)
        {
            barColor = "§e";
            filledBars = 4;
        }
        else if (ping < 600)
        {
            barColor = "§6";
            filledBars = 3;
        }
        else if (ping < 1000)
        {
            barColor = "§c";
            filledBars = 2;
        }
        else
        {
            barColor = "§4";
            filledBars = 1;
        }

        string numericPing = ping >= 0 ? $"{Math.Min(ping, 9999),4}ms" : " ???ms";
        return $"{barColor}{new string('|', filledBars)}§8{new string('.', 5 - filledBars)}§r {numericPing}";
    }

    private static string PlayerLabel(TabListEntryInfo entry)
    {
        string? display = entry.DisplayName;
        string label = string.IsNullOrWhiteSpace(display) ? entry.Name : display;
        return IsSpectator(entry) ? $"§7§o{label}§r" : label;
    }

    private static string PadFormattedRight(string text, int totalWidth)
    {
        int visibleLength = VisibleLength(text);
        return visibleLength >= totalWidth ? text : text + new string(' ', totalWidth - visibleLength);
    }

    /// <summary>
    /// The printed width of a row: its length once the section-sign codes are dropped.
    /// Ports legacy's <c>ChatBot.GetVerbatim</c> (Scripting/ChatBot.cs:629-654), including the <c>§#rrggbb</c> form, and counts instead of allocating.
    /// </summary>
    private static int VisibleLength(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '§')
                length++;
            else if (i + 1 < text.Length && text[i + 1] == '#' && i + 7 < text.Length)
                i += 7;
            else
                i++;
        }

        return length;
    }
}

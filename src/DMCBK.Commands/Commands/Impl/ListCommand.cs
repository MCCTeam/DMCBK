using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>list</c> command: lists players in the tab list.</summary>
public sealed class ListCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "list";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.list.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "list";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Entities;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list the players online"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["list"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["tab", "teams", "scoreboard"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        TabListSnapshot list = ctx.Run(ct => ctx.Game.Player.GetTabListAsync(ct));

        // Legacy had ONE branch: it joined whatever GetOnlinePlayers() returned into the same line, so an empty list printed "PlayerList:" and nothing else -- a dangling colon that reads as a bug, while /tab answered the same fact with a whole sentence.
        // Both now use the shared empty-state wording.
        List<string> names = ListedNames(list);
        return names.Count == 0
            ? ctx.Result.Ok(CommandStrings.NothingToShow(ctx.Glyphs, McStrings.Get("cmd.empty.players")))
            : ctx.Result.Ok(McStrings.Format("cmd.list.players", string.Join(", ", names)));
    }

    /// <summary>
    /// The names a vanilla client would show in its tab overlay, in the order it would show them.
    /// <para>
    /// The filter is the point.
    /// A tab-list entry carries a <c>listed</c> flag (1.19.3+) and vanilla renders only the entries that set it: the overlay draws <c>connection.getListedOnlinePlayers()</c> (1.21.11-client-decompiled PlayerTabOverlay.java:95), a collection the packet handler keeps separate from <c>getOnlinePlayers()</c>.
    /// Proxies use unlisted entries routinely, and a Velocity network was where this surfaced: the proxy holds an unlisted, unnamed entry alongside the real player, so <c>list</c> answered "PlayerList: , testomir2" with a leading empty name that no vanilla client would have drawn.
    /// </para>
    /// <para>
    /// The order is vanilla's comparator from the same file (:54-57): descending list order, spectators after everyone else, then name case-insensitively.
    /// Vanilla sorts by team name between those last two; a team is not on this snapshot, so that key is absent and equal-named entries keep their arrival order.
    /// The 80-entry display cap is deliberately NOT applied: it exists because vanilla's overlay has to fit on a screen, and a text answer does not.
    /// </para>
    /// </summary>
    internal static List<string> ListedNames(TabListSnapshot list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var listed = new List<TabListEntryInfo>(list.Entries.Count);
        foreach (TabListEntryInfo entry in list.Entries)
        {
            // A listed entry with no name is not a player either: nothing to print, and printing it is how the stray comma got there.
            if (entry.Listed && !string.IsNullOrWhiteSpace(entry.Name))
                listed.Add(entry);
        }

        listed.Sort(static (left, right) =>
        {
            int byOrder = right.ListOrder.CompareTo(left.ListOrder);
            if (byOrder != 0)
                return byOrder;

            int bySpectator = IsSpectator(left).CompareTo(IsSpectator(right));
            return bySpectator != 0
                ? bySpectator
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        var names = new List<string>(listed.Count);
        foreach (TabListEntryInfo entry in listed)
            names.Add(entry.Name);

        return names;
    }

    private static int IsSpectator(TabListEntryInfo entry)
        => string.Equals(entry.GameMode, "Spectator", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}

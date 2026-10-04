using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>teams</c> command: lists scoreboard teams and their members.</summary>
public sealed class TeamsCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "teams";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.teams.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "teams";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Entities;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list scoreboard teams and their members"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["teams"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["list", "tab", "scoreboard"];

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

        ScoreboardSnapshot board = ctx.Run(ct => ctx.Game.Player.GetScoreboardAsync(ct));
        if (board.Teams.Count == 0)
            return ctx.Result.Ok(McStrings.Get("cmd.teams.no_teams"));

        // The team rules (name-tag visibility, collision rule, friendly fire, see-invisibles) are tracked by UMPK's Umpk.Game.Scoreboard.Team but dropped by the snapshot MCC reads (Umpk.Client's TeamSnapshot and DMCBK.Core's TeamInfo carry neither), so the four fields legacy printed cannot be read here.
        // They render as the generic "unknown" token rather than as invented defaults: printing "friendlyFire: False" for a value nobody read would be a fabricated reading.
        string unknown = McStrings.Get("dialog.action_desc_unknown");

        StringBuilder response = new();
        foreach (TeamInfo team in board.Teams.OrderBy(static t => t.Name, StringComparer.Ordinal))
        {
            response.AppendLine(McStrings.Format(
                "cmd.teams.team_header",
                team.Name,
                team.DisplayName,
                team.Color,
                team.Prefix,
                team.Suffix,
                unknown,
                unknown,
                unknown,
                unknown));

            response.AppendLine(team.Members.Count == 0
                ? McStrings.Get("cmd.teams.team_no_members")
                : McStrings.Format(
                    "cmd.teams.team_members",
                    team.Members.Count,
                    string.Join(", ", team.Members.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase))));
        }

        return ctx.Result.Ok(response.ToString().TrimEnd());
    }
}

using System.Globalization;
using System.Text;
using DMCBK.Core.Presentation;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>scoreboard</c> command: lists scoreboard objectives, or shows one objective's scores.</summary>
public sealed class ScoreboardCommand : CommandBase
{
    /// <summary>How many scores a detail view prints before pointing at <c>--all</c>. Vanilla's sidebar fits 15.</summary>
    internal const int DefaultScoreLimit = 15;

    /// <inheritdoc/>
    public override string CmdName => "scoreboard";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.ScoreboardDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "scoreboard [ui|<objective>] [--all]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Entities;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list objectives with entry counts"),
        new("ui", CommandStrings.ScoreboardUiUsage),
        new("<objective>", "show the top scores for one objective"),
        new("<objective> --all", "show every score for one objective"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("--all", "show every score instead of the top 15"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["scoreboard", "scoreboard ui", "scoreboard kills", "scoreboard kills --all"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["list", "tab", "teams"];

    /// <inheritdoc/>
    public override string? ManTopic => "entities";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => List(ctx.Source))
            .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
            .ThenArgument("objective", Arguments.Word(), a => a
                .Executes(ctx => Show(ctx.Source, ctx.GetArgument<string>("objective"), all: false))
                .ThenLiteral("--all", f => f.Executes(
                    ctx => Show(ctx.Source, ctx.GetArgument<string>("objective"), all: true))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static int Ui(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        return ctx.Ui?.TryOpenScoreboard() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
    }

    private int List(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        ScoreboardSnapshot board = ctx.Run(ct => ctx.Game.Player.GetScoreboardAsync(ct));
        return ctx.Result.Ok(RenderList(board, ctx.Glyphs));
    }

    private int Show(CommandContext ctx, string objective, bool all)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        ScoreboardSnapshot board = ctx.Run(ct => ctx.Game.Player.GetScoreboardAsync(ct));
        return ctx.Result.Ok(RenderObjective(board, objective, all, ctx.Glyphs));
    }

    /// <summary>
    /// Renders the objective index: a coloured title plus one row per objective with its display name, entry count and render type.
    /// Objectives sort by internal name, the only ordering vanilla guarantees (display names are free text and collide).
    /// </summary>
    /// <param name="board">The scoreboard snapshot.</param>
    /// <param name="glyphs">The resolved glyph vocabulary, for the empty state.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    internal static string RenderList(ScoreboardSnapshot board, GlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(glyphs);

        if (board.Objectives.Count == 0)
            return CommandStrings.NothingToShow(glyphs, "scoreboard objectives");

        var lines = new List<string>
        {
            $"§e{CommandStrings.ScoreboardHeader(board.Objectives.Count)}§r",
        };

        foreach (ObjectiveInfo objective in board.Objectives.OrderBy(static o => o.Name, StringComparer.Ordinal))
            lines.Add("  " + Label(objective) + " - " + CommandStrings.ScoreboardEntries(objective.Scores.Count));

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Renders one objective's scores, highest first, entries with equal scores ordered by name.
    /// Prints the top <see cref="DefaultScoreLimit"/> unless <paramref name="all"/> is set, and says how many were held back.
    /// An unknown name answers with the names that do exist, so a typo is one glance away from the fix.
    /// </summary>
    /// <param name="board">The scoreboard snapshot.</param>
    /// <param name="name">The objective name as typed.</param>
    /// <param name="all">Whether to print every score instead of the top few.</param>
    /// <param name="glyphs">The resolved glyph vocabulary (unused today; kept so the empty states match).</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    internal static string RenderObjective(ScoreboardSnapshot board, string name, bool all, GlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(glyphs);

        ObjectiveInfo? objective = board.Objectives
            .FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (objective is null)
        {
            var response = new StringBuilder(CommandStrings.ScoreboardUnknown(name));
            if (board.Objectives.Count > 0)
            {
                response.Append('\n').Append(CommandStrings.ScoreboardKnown(string.Join(
                    ", ",
                    board.Objectives
                        .OrderBy(static o => o.Name, StringComparer.Ordinal)
                        .Select(static o => o.Name))));
            }

            return response.ToString();
        }

        if (objective.Scores.Count == 0)
            return CommandStrings.ScoreboardNoScores(objective.Name);

        List<KeyValuePair<string, int>> scores = [.. objective.Scores];
        scores.Sort(static (left, right) =>
        {
            int byValue = right.Value.CompareTo(left.Value);
            if (byValue != 0)
                return byValue;

            int byName = string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
        });

        int shown = all ? scores.Count : Math.Min(scores.Count, DefaultScoreLimit);
        int rankWidth = shown.ToString(CultureInfo.InvariantCulture).Length;

        string label = Label(objective);
        var lines = new List<string>(shown + 2)
        {
            // Label already ends in a reset when it carries the grey "(name)" part; adding another §r there prints no differently but reads as a typo in transcripts.
            label.EndsWith("§r", StringComparison.Ordinal) ? $"§e{label}" : $"§e{label}§r",
        };

        for (int index = 0; index < shown; index++)
        {
            string rank = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(rankWidth);
            lines.Add($"  {rank}. {scores[index].Key} - {scores[index].Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (shown < scores.Count)
            lines.Add($"  §7{CommandStrings.ScoreboardMore(scores.Count - shown)}§r");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// One objective's label: the display name with the internal name behind it, or the bare name when both are the same.
    /// Hearts objectives carry a tag, since single-digit values there are health, not points.
    /// </summary>
    private static string Label(ObjectiveInfo objective)
    {
        string head = string.Equals(objective.DisplayName, objective.Name, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(objective.DisplayName)
            ? objective.Name
            : $"{objective.DisplayName} §7({objective.Name})§r";

        if (string.Equals(objective.RenderType, CommandStrings.ScoreboardHearts, StringComparison.OrdinalIgnoreCase))
            head += $" §7[{CommandStrings.ScoreboardHearts}]§r";

        return head;
    }
}

using System.Text;
using DMCBK.Core.Localization;
using DMCBK.Core.Manual;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>man</c> command: MCC's manual.
/// <para>
/// <c>help</c> describes command syntax.
/// <c>man</c> describes concepts and how things relate, such as why a bot stopped, which gate a feature needs, or how to write a plugin.
/// </para>
/// <para>
/// Pages are Markdown (see <see cref="ManualCatalog"/>) and the host renders them: rich in the classic console and in the TUI, plain text anywhere else.
/// </para>
/// </summary>
public sealed class ManCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "man";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("man.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "man [topic|ui]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, McStrings.Get("man.usage_index")),
        new("<topic>", McStrings.Get("man.usage_topic")),
        new("-k <word>", McStrings.Get("man.usage_search")),
        new("ui", CommandStrings.BrowserUsage),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("-k", McStrings.Get("man.flag_k")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
        ["man getting-started", "man movement", "man -k pathfinding", "man ui"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["help"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .Executes(ctx => Index(ctx.Source))
            .ThenLiteral("ui", u => u.Executes(ctx => OpenUi(ctx.Source)))
            .ThenLiteral("-k", k => k
                .ThenArgument("term", Arguments.GreedyString(), a => a
                    .Executes(ctx => Search(ctx.Source, ctx.GetArgument<string>("term")))))
            .ThenArgument("topic", Arguments.Word(), a => a
                .Suggests(SuggestTopics)
                .Executes(ctx => Show(ctx.Source, ctx.GetArgument<string>("topic"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static int OpenUi(CommandContext ctx)
        => ctx.Ui?.TryOpenCommandBrowser(CommandBrowserTab.Manual) == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);

    private static ValueTask SuggestTopics(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (ManualTopic topic in ctx.Source.Client.Manuals.Topics())
        {
            if (topic.Id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(topic.Id);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The index: four groups, every topic, with its one-line summary.</summary>
    private static int Index(CommandContext ctx)
    {
        string p = ctx.Commands.NoPrefix ? string.Empty : ctx.Commands.Prefix.ToString();
        IReadOnlyList<ManualTopic> topics = ctx.Client.Manuals.Topics();
        var sb = new StringBuilder();
        sb.Append("§e").Append(McStrings.Format("man.index_header", topics.Count)).Append("§r");

        foreach (ManualGroup group in ManualTopics.Groups)
        {
            // MCC's own topics keep their editorial order (getting-started first, not alphabetically first).
            // Plugin topics have no such order to keep and arrive in load order, which is not an order a reader can predict, so those are sorted.
            List<ManualTopic> inGroup = group == ManualGroup.Plugins
                ? [.. topics.Where(t => t.Group == group).OrderBy(t => t.Id, StringComparer.Ordinal)]
                : [.. topics.Where(t => t.Group == group)];
            if (inGroup.Count == 0)
                continue;

            int width = inGroup.Max(t => t.Id.Length);
            sb.Append('\n').Append('\n').Append("§e").Append(ManualTopics.GroupName(group)).Append("§r");
            foreach (ManualTopic topic in inGroup)
            {
                sb.Append('\n').Append("  §b").Append(topic.Id.PadRight(width)).Append("§r  §7")
                  .Append(topic.Summary).Append("§r");
            }
        }

        sb.Append('\n').Append('\n').Append("§7")
          .Append(McStrings.Format("man.index_footer_topic", p)).Append("§r")
          .Append('\n').Append("§7").Append(McStrings.Format("man.index_footer_search", p)).Append("§r")
          .Append('\n').Append("§7").Append(McStrings.Format("man.index_footer_new", p)).Append("§r");

        ctx.Output.WriteLine(sb.ToString());
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>One page, rendered by the host when it can render documents, as plain text when it cannot.</summary>
    private static int Show(CommandContext ctx, string topicId)
    {
        if (ctx.Client.Manuals.Find(topicId) is not { } topic)
            return ctx.Result.Fail(NoSuchTopic(ctx, topicId));

        if (ctx.Client.Manuals.Read(topic.Id, ctx.Client.UiCulture) is not { Length: > 0 } markdown)
            return ctx.Result.Fail(McStrings.Format("man.missing_page", topic.Id));

        // One document path for every command that produces one, so /man and /entity render the same way and a host implements one hook rather than two.
        DocumentRendering.Write(ctx, markdown);

        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary><c>man -k</c>: every page whose text mentions the term, with the line it matched on.</summary>
    private static int Search(CommandContext ctx, string term)
    {
        string needle = term.Trim();
        if (needle.Length == 0)
            return ctx.Result.Fail(McStrings.Get("man.search_empty"));

        var sb = new StringBuilder();
        int hits = 0;
        foreach (ManualTopic topic in ctx.Client.Manuals.Topics())
        {
            if (ctx.Client.Manuals.Read(topic.Id, ctx.Client.UiCulture) is not { Length: > 0 } markdown)
                continue;

            string? line = FirstMatchingLine(markdown, needle);
            if (line is null)
                continue;

            hits++;
            sb.Append('\n').Append("  §b").Append(topic.Id).Append("§r  §7").Append(line).Append("§r");
        }

        if (hits == 0)
        {
            return ctx.Result.Ok(CommandStrings.NothingToShow(
                ctx.Glyphs, McStrings.Format("man.search_none", needle)));
        }

        ctx.Output.WriteLine("§e" + McStrings.Format("man.search_header", needle, hits) + "§r" + sb);
        return ctx.Result.Set(CmdStatus.Done);
    }

    private static string? FirstMatchingLine(string markdown, string needle)
    {
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            // Headings, rules and table rows are not prose.
            // A table row matched on "Pathfinding" would show the reader a row of pipes rather than a sentence explaining the hit.
            if (line.Length == 0
                || line.StartsWith('#')
                || line.StartsWith('|')
                || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return line.Length <= 72 ? line : line[..69] + "...";
        }

        return null;
    }

    private static string NoSuchTopic(CommandContext ctx, string typed)
    {
        string p = ctx.Commands.NoPrefix ? string.Empty : ctx.Commands.Prefix.ToString();
        return McStrings.Format("man.no_topic", typed, $"{p}man");
    }

    /// <summary>Markdown reduced to plain text. Kept as a test seam over the shared implementation.</summary>
    internal static string PlainText(string markdown) => DocumentRendering.PlainText(markdown);
}

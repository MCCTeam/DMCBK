using DMCBK.Core.Localization;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>help</c> command: the grouped command index, and the entry point for one command's page.
/// <para>
/// Legacy (and this port until now) printed every command's raw grammar in one flat alphabetical run wrapped in the <c>icmd.list</c> frame.
/// That answered "where is the command called X" for someone who already knew the name and nothing at all for someone who did not.
/// The listing is now built by <see cref="HelpRendering.Index"/> from the registered <see cref="CommandBase"/> objects, so it can group by <see cref="CommandCategory"/>, print <see cref="CommandBase.CmdDesc"/>, fold aliases inline and omit commands that opt out.
/// </para>
/// </summary>
public sealed class HelpCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "help";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.help.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "help [command|ui]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, McStrings.Get("cmd.help.usage_index")),
        new("<command>", McStrings.Get("cmd.help.usage_page")),
        new("ui", CommandStrings.BrowserUsage),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["help move", "help inventory", "help ui"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["man"];

    /// <inheritdoc/>
    public override string? ManTopic => "getting-started";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
        => builder.Literal("help", l => l
            .Executes(ctx => ListHelp(ctx.Source))
            .ThenLiteral("ui", h => h.Executes(ctx => OpenUi(ctx.Source)))
            // `help` had no child named `help`, so `/help help` was an argument-parse FAILURE, and the failure path prints the whole mirrored subtree: one mistyped word produced 76 lines.
            // Every other command answers for itself; this makes help answer for itself too.
            .ThenLiteral("help", h => h.Executes(ctx => ShowUsage(ctx.Source))));

    /// <summary>
    /// Renders this command's own page. `help`'s registered subtree is the MIRROR of every other command, so the generic path would enumerate all 44 of them as branches of `/help`; the authored <see cref="UsageLines"/> are used instead and the derived list is deliberately never consulted.
    /// </summary>
    private new int ShowUsage(CommandContext ctx) => ctx.Result.Ok(BuildUsage(ctx));

    private static int ListHelp(CommandContext ctx)
        => ctx.Result.Ok(ctx.Commands.BuildHelpListing());

    private static int OpenUi(CommandContext ctx)
        => ctx.Ui?.TryOpenCommandBrowser(CommandBrowserTab.Commands) == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
}

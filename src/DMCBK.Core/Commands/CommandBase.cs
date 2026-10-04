using Umpk.Commands;

namespace DMCBK.Core.Commands;

/// <summary>
/// Base class for an internal command.
/// A command exposes a name, a localized description and usage, and registers its subtree onto the shared UMPK <see cref="CommandBuilder{TSource}"/> handed to it by whatever scope is registering it.
/// Replaces the legacy <c>Command</c> base; commands receive the client through the injected <see cref="CommandContext"/> (the command source), never a static handler.
/// </summary>
public abstract class CommandBase
{
    /// <summary>The command name (the root literal).</summary>
    public abstract string CmdName { get; }

    /// <summary>The already-localized one-line description.</summary>
    public abstract string CmdDesc { get; }

    /// <summary>
    /// The usage hint, for example <c>changeslot &lt;1-9&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A GRAMMAR, never a sentence: <see cref="BuildUsage"/> prefixes it with the command character and suffixes it with <see cref="CmdDesc"/>, so a sentence here renders as <c>/Basic usage: ...: Some description</c>.
    /// <c>CommandUsageIsAGrammarTests</c> enforces it.
    /// </remarks>
    public abstract string CmdUsage { get; }

    /// <summary>
    /// What this command is for, which decides where it sits in the <c>/help</c> index.
    /// Defaults to <see cref="CommandCategory.Plugins"/>, so a plugin that says nothing still lands somewhere sensible.
    /// </summary>
    public virtual CommandCategory Category => CommandCategory.Plugins;

    /// <summary>
    /// Other names this command answers to.
    /// Rendered inline in the index (<c>exit (quit)</c>) instead of appearing as a separate row, which is how <c>/cc -&gt; clear-console</c> used to leak into the listing as a raw dispatcher redirect.
    /// </summary>
    public virtual IReadOnlyList<string> Aliases => [];

    /// <summary>
    /// Whether the command appears in the <c>/help</c> index.
    /// False for a command that still answers when typed but should not be advertised: the three removed commands (<c>upgrade</c>/<c>script</c>) took rows in the listing just to say they no longer exist.
    /// </summary>
    public virtual bool ShowInIndex => true;

    /// <summary>
    /// The authored <c>USAGE</c> rows: one per idea, each with a gloss.
    /// Empty means "derive them from the registered tree", which is what every command did before and what still happens for anything that does not author its own.
    /// See <see cref="UsageLine"/> for why authoring beats deriving.
    /// </summary>
    public virtual IReadOnlyList<UsageLine> UsageLines => [];

    /// <summary>The command's flags, stated once in their own block rather than doubling the usage rows.</summary>
    public virtual IReadOnlyList<UsageFlag> Flags => [];

    /// <summary>Concrete invocations a user can copy. Rendered under <c>EXAMPLES</c>.</summary>
    public virtual IReadOnlyList<string> Examples => [];

    /// <summary>
    /// Related commands, as bare names (<c>"look"</c>, not <c>"/look"</c>); the renderer adds the prefix.
    /// </summary>
    public virtual IReadOnlyList<string> SeeAlso => [];

    /// <summary>
    /// The <c>/man</c> topic that explains the concept behind this command, if there is one.
    /// Rendered as part of <c>SEE ALSO</c>, which is the only route from "what is the syntax" to "how does this work".
    /// </summary>
    public virtual string? ManTopic => null;

    /// <summary>
    /// The gameplay gates this command needs switched on, rendered as <c>REQUIRES</c> against the live configuration.
    /// </summary>
    public virtual CommandFeature RequiredFeatures => CommandFeature.None;

    /// <summary>
    /// Whether running this command is echoed into the console log (see <c>console.toml [General] EchoCommands</c>).
    /// <para>
    /// False for a command whose whole job is to change what is on the screen.
    /// Echoing <c>&gt; /cc</c> immediately after wiping the console leaves one stray line at the top of a display the user just asked to be empty, which is the opposite of what they asked for.
    /// </para>
    /// </summary>
    public virtual bool EchoWhenRun => true;

    /// <summary>Registers the command's subtree (and its mirrored help node) onto the shared builder.</summary>
    public abstract void Register(CommandBuilder<CommandContext> builder);

    /// <summary>
    /// Registers the mirrored <c>help &lt;name&gt;</c> node that renders this command's usage, and returns the captured child builder so the caller can <c>RedirectTo</c> it from its own <c>_help</c> node.
    /// Call this at the start of <see cref="Register"/> so the redirect target exists before it is referenced.
    /// <para>
    /// Every command that calls this adds its OWN top-level <c>help</c> literal (with one child: its own name); UMPK's dispatcher merges same-named top-level literals when a scope's roots are added to the tree, keeping the first non-null command body a merge encounters.
    /// <c>Impl.HelpCommand</c> is registered first in the builtins array for exactly that reason: its own top-level <c>help</c> (<c>Executes(ListHelp)</c>) is the one that survives the merge; every other command's contributes only its single child.
    /// </para>
    /// </summary>
    protected static CommandNodeBuilder<CommandContext> RegisterHelp(
        CommandBuilder<CommandContext> builder, string cmdName, Func<CommandContext, int> showUsage)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cmdName);
        ArgumentNullException.ThrowIfNull(showUsage);

        CommandNodeBuilder<CommandContext>? captured = null;
        builder.Literal("help", h => h.ThenLiteral(cmdName, c =>
        {
            captured = c;
            c.Executes(ctx => showUsage(ctx.Source));
        }));

        return captured!;
    }

    /// <summary>Sets and returns the usage text as a successful result.</summary>
    protected int ShowUsage(CommandContext ctx) => ctx.Result.Ok(BuildUsage(ctx));

    /// <summary>
    /// Builds this command's help page: header, usage rows, flags, live gate status, examples and cross-references.
    /// See <c>HelpRendering.Page</c> for the shape and why it is not the raw branch dump it used to be.
    /// </summary>
    protected string BuildUsage(CommandContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Commands.RenderUsage(this, ctx);
    }
}

using DMCBK.Core.Commands;
using Umpk.Commands;

namespace DMCBK.Core.Beacon;

/// <summary>
/// One script-registered command: the parsed pattern, its argument names, the owning script, and the doc-comment metadata that feeds <c>/help</c>.
/// </summary>
/// <param name="Name">The command name without slash (<c>price</c>).</param>
/// <param name="Pattern">The full pattern as written (<c>/price &lt;item&gt;</c>).</param>
/// <param name="Args">Argument names in order.</param>
/// <param name="ScriptId">The owning script id.</param>
/// <param name="Description">From <c># desc:</c> above the block, or the default text.</param>
/// <param name="Examples">From <c># example:</c> lines above the block.</param>
/// <param name="Block">The block body to run per invocation.</param>
public sealed record BeaconScriptCommandSpec(
    string Name,
    string Pattern,
    IReadOnlyList<string> Args,
    string ScriptId,
    string Description,
    IReadOnlyList<string> Examples,
    CommandBlock Block);

/// <summary>
/// Parses <c>command "/price &lt;item&gt;"</c> patterns and the doc comments above the block.
/// The pattern needs its leading slash; the first token is the command name and every <c>&lt;name&gt;</c> token after it is one word argument.
/// Doc comments are the <c>#</c> or <c>//</c> lines directly above the block: <c># desc:</c> becomes the help description and each <c># example:</c> becomes a copy-paste example.
/// Anything else above the block is ignored, so ordinary comments never leak into help.
/// </summary>
public static class BeaconCommandSpec
{
    /// <summary>Parses one command block into its spec; null when the pattern is unusable.</summary>
    public static BeaconScriptCommandSpec? Parse(
        string scriptId, CommandBlock block, IReadOnlyList<LexedComment>? comments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(block);
        string pattern = block.Pattern.Trim();
        if (!pattern.StartsWith("/", StringComparison.Ordinal))
            return null;

        string[] tokens = pattern[1..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;

        string name = tokens[0];
        if (!IsCommandName(name))
            return null;

        var args = new List<string>();
        foreach (string token in tokens[1..])
        {
            if (!token.StartsWith("<", StringComparison.Ordinal) || !token.EndsWith(">", StringComparison.Ordinal)
                || token.Length < 3 || !IsArgName(token[1..^1]))
                return null;

            args.Add(token[1..^1]);
        }

        (string Description, List<string> Examples) docs = ReadDocs(block, comments);
        return new BeaconScriptCommandSpec(
            name, pattern, args, scriptId,
            docs.Description.Length == 0
                ? CommandStrings.ScriptsCommandDesc(pattern, scriptId)
                : docs.Description,
            docs.Examples, block);
    }

    private static bool IsCommandName(string name)
        => name.Length > 0 && char.IsLetter(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsArgName(string name)
        => name.Length > 0 && char.IsLetter(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_');

    private static (string Description, List<string> Examples) ReadDocs(
        CommandBlock block, IReadOnlyList<LexedComment>? comments)
    {
        string description = string.Empty;
        var examples = new List<string>();
        if (comments is null)
            return (description, examples);

        int blockLine = block.Span.Origin.Line;
        foreach (LexedComment comment in comments
                     .Where(c => c.Span.Line < blockLine && c.Span.Line >= blockLine - 8)
                     .OrderBy(c => c.Span.Line))
        {
            string text = comment.Text.Trim();
            if (!BeaconCommentDocs.TryStripMarker(text, out string stripped))
                continue;

            if (BeaconCommentDocs.TryGetTag(stripped, "desc:", out string desc))
                description = desc;
            else if (BeaconCommentDocs.TryGetTag(stripped, "example:", out string example))
            {
                if (example.Length > 0)
                    examples.Add(example);
            }
        }

        return (description, examples);
    }
}

/// <summary>
/// A script-registered command on the real Brigadier tree: dispatches console input into a script block with <c>arg(name)</c> bound, and renders the doc-comment metadata in <c>/help</c>.
/// Unregistered by disposing the <c>RegisterScopedCommand</c> handle the host keeps per script (unload, reload, and <c>stop</c> drop it together with the script).
/// </summary>
public sealed class BeaconScriptCommand : CommandBase
{
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>>? _dispatch;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<BeaconRunResult>>? _runDispatch;

    /// <summary>Builds a Brigadier command over <paramref name="spec"/> dispatching to <paramref name="dispatch"/>.</summary>
    public BeaconScriptCommand(
        BeaconScriptCommandSpec spec,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(dispatch);
        Spec = spec;
        _dispatch = dispatch;
    }

    internal BeaconScriptCommand(
        BeaconScriptCommandSpec spec,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<BeaconRunResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(dispatch);
        Spec = spec;
        _runDispatch = dispatch;
    }

    /// <summary>The parsed spec (name, args, help metadata).</summary>
    public BeaconScriptCommandSpec Spec { get; }

    /// <inheritdoc/>
    public override string CmdName => Spec.Name;

    /// <inheritdoc/>
    public override string CmdDesc => Spec.Description;

    /// <inheritdoc/>
    public override string CmdUsage => Spec.Args.Count == 0
        ? Spec.Name
        : Spec.Name + " " + string.Join(" ", Spec.Args.Select(a => $"<{a}>"));

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(CmdUsage, CommandStrings.ScriptsCommandUsage(CmdUsage)),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => Spec.Examples;

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["scripts"];

    /// <inheritdoc/>
    public override string? ManTopic => "scripts";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => RegisterArgs(l, 0));
    }

    private void RegisterArgs(CommandNodeBuilder<CommandContext> node, int index)
    {
        if (index >= Spec.Args.Count)
        {
            node.Executes(ctx => Run(ctx.Source, ReadArgs(ctx)));
            return;
        }

        string arg = Spec.Args[index];
        node.ThenArgument(arg, Arguments.QuotableString(), a => RegisterArgs(a, index + 1));
    }

    private Dictionary<string, string> ReadArgs(ICommandContext<CommandContext> ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string arg in Spec.Args)
        {
            try
            {
                values[arg] = ctx.GetArgument<string>(arg) ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                values[arg] = string.Empty;
            }
        }

        return values;
    }

    private int Run(CommandContext ctx, IReadOnlyDictionary<string, string> args)
    {
        if (_runDispatch is { } run)
        {
            BeaconRunResult result = ctx.Run(ct => run(args, ct));
            string text = string.Join(Environment.NewLine, result.LocalOutput);
            if (!result.Success)
            {
                string failure = result.Error?.Message
                    ?? result.Diagnostics.FirstOrDefault(diagnostic =>
                        diagnostic.Severity == BeaconSeverity.Error)?.Message
                    ?? CommandStrings.ScriptsFailedNoDiagnostic(Spec.ScriptId);
                return ctx.Result.Fail(text.Length == 0 ? failure : text + Environment.NewLine + failure);
            }

            return ctx.Result.Ok(text.Length == 0 ? null : text);
        }

        string output = ctx.Run(ct => _dispatch!(args, ct));
        if (output.Length > 0)
            return ctx.Result.Ok(output);

        return ctx.Result.Set(CmdStatus.Done, null);
    }
}

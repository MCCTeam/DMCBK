using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Commands;
using Umpk.Data.Java;
using Umpk.Protocol.Java;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>set</c> command: set a <c>%variable%</c> from a <c>name=value</c> expression.
/// Mirrors legacy <c>Commands/Set.cs</c>: a missing <c>=</c> or an unusable name reports <c>cmd.set.format</c>.
/// Unlike legacy (Set.cs:47 returned <c>Status.Done</c> with NO message) a successful set says what it set: a silent success is indistinguishable from a typo that did nothing, and this command's whole output was that silence.
/// </summary>
public sealed class SetCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "set";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.set.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "set varname=value";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<name>=<value>", "set a %variable% usable in other commands"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["set home=150 80 380"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["reload"];

    /// <inheritdoc/>
    public override string? ManTopic => "variables";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenArgument("expression", Arguments.GreedyString(), h => h
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("expression"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, string expression)
    {
        // Legacy Set.cs:42 splits the trimmed expression on '=' and needs at least one separator; the name is whatever precedes the first '=' (an empty name is rejected by the store, as it was by AppVar).
        string trimmed = expression.Trim();
        int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
            return ctx.Result.Fail(McStrings.Get("cmd.set.format"));

        string name = trimmed[..equals];
        string value = trimmed[(equals + 1)..];
        return ctx.Variables.Set(name, value)
            ? ctx.Result.Ok(McStrings.Format("cmd.set.done", name.Trim(), value))
            : ctx.Result.Fail(McStrings.Get("cmd.set.format"));
    }
}

using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>log</c> command: echoes text back to the log.</summary>
public sealed class LogCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "log";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.log.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "log <text>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<text>", "print text to the console, without sending it"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["log checkpoint reached"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["send"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenArgument("text", Arguments.GreedyString(), a => a
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("text"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, string text) => ctx.Result.Ok(text);
}

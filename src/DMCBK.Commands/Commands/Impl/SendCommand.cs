using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>send</c> command: sends raw text/chat to the server.</summary>
public sealed class SendCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "send";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.send.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "send <text>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<text>", "send chat, or a server command starting with /"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["send hello everyone", "send /help"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["log"];

    /// <inheritdoc/>
    public override string? ManTopic => "chat";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        // "help send" exists, but "send" itself has NO "_help" child in legacy (Send.cs:21-24): its only child is the greedy argument, so "send _help" sends the text "_help" to the server.
        // Adding the literal back would shadow that, because a literal wins over an argument in the parse.
        RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .ThenArgument("text", Arguments.GreedyString(), a => a
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("text")))));
    }

    private int Run(CommandContext ctx, string text)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        ctx.Run(ct => ctx.Game.Chat.SendAsync(text, ct));

        // Legacy sets Done with no message (Send.cs:41), and the host only prints a Done result when it carries text (McClient.cs:1051), so a successful send says nothing.
        return ctx.Result.Set(CmdStatus.Done, null);
    }
}

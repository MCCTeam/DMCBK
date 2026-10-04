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
/// The <c>reco</c> command: reconnect to the current server, optionally switching account.
/// Mirrors legacy <c>Commands/Reco.cs</c>: the account name is trimmed and must resolve (<c>cmd.connect.unknown</c> otherwise), and the reconnect itself is silent (Reco.cs:51 returns <c>Status.Done</c> with no message).
/// </summary>
public sealed class RecoCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "reco";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.reco.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "reco [account]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Session;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "reconnect to the current server"),
        new("<account>", "reconnect as a different account"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["reco", "reco Alt"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["connect"];

    /// <inheritdoc/>
    public override string? ManTopic => "connecting";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source, null))
            .ThenArgument("account", DmcbkArguments.AccountNick(), h => h
                .Suggests(SessionCommandHelpers.SuggestAccounts)
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("account"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, string? accountName)
    {
        if (!SessionCommandHelpers.TryResolveAccount(ctx, accountName, out AccountSelection? account, out int failure))
            return failure;

        try
        {
            ctx.Run(ct => ctx.Client.ReconnectAsync(null, account, ct));

            // Legacy Reco.cs:51 is quiet on success; the host prints the Reconnecting/Reconnected lines.
            return ctx.Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }
}

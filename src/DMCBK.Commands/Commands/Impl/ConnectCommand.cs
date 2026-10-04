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
/// The <c>connect</c> command: switch to a configured server (and account) and reconnect.
/// <para>
/// Mirrors legacy <c>Commands/Connect.cs</c>: the account is resolved BEFORE the server (Connect.cs:45), an unresolvable account reports <c>cmd.connect.unknown</c>, an unresolvable server reports <c>cmd.connect.invalid_ip</c>, and a successful switch is silent (Connect.cs:51 returns <c>Status.Done</c> with no message; the restart/reconnect banner is the host's to print).
/// </para>
/// </summary>
public sealed class ConnectCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "connect";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.connect.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "connect <server> [account]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Session;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<server>", "a name from servers.toml, or host[:port]"),
        new("<server> <account>", "connect as a specific account"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["connect localhost", "connect play.example.net:25566 Alt"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["reco", "exit"];

    /// <inheritdoc/>
    public override string? ManTopic => "connecting";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // One greedy argument, split below.
        // A host:port carries a colon, which is outside Brigadier's unquoted set, so a two-argument grammar could never read the form this command documents.
        builder.Literal(CmdName, l => l
            .ThenArgument("server", Arguments.GreedyString(), h => h
                .Suggests(SuggestServerThenAccount)
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("server"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>Splits <c>&lt;server&gt; [account]</c> out of the greedy argument, quotes honoured.</summary>
    private int Run(CommandContext ctx, string arguments)
    {
        return CommandTokens.Split(arguments) switch
        {
            [{ } server] => Run(ctx, server, null),
            [{ } server, { } account] => Run(ctx, server, account),
            // A failure, not the usual Ok: a scripted run has to be able to tell an unreadable line from a connect that worked.
            _ => ctx.Result.Fail(BuildUsage(ctx)),
        };
    }

    /// <summary>
    /// Server names while the first token is being typed, account names after it.
    /// The greedy node sees the whole tail, so an account suggestion carries the server already typed with it.
    /// </summary>
    private static ValueTask SuggestServerThenAccount(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        int space = sink.Remaining.LastIndexOf(' ');
        if (space < 0)
            return SessionCommandHelpers.SuggestServers(ctx, sink);

        if (ctx.Source.Config is { } config)
        {
            string head = sink.Remaining[..(space + 1)];
            string tail = sink.Remaining[(space + 1)..];
            foreach (ConfiguredAccount account in config.Accounts.Accounts)
            {
                if (!string.IsNullOrEmpty(account.Name)
                    && account.Name.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(head + account.Name);
            }
        }

        return ValueTask.CompletedTask;
    }

    private int Run(CommandContext ctx, string serverName, string? accountName)
    {
        // Legacy order (Connect.cs:45-48): the account is validated first, so a bad account name reports "Unknown account" even when the server name is bad too.
        AccountSelection? account;
        if (!SessionCommandHelpers.TryResolveAccount(ctx, accountName, out account, out int accountFailure))
            return accountFailure;

        ConfiguredServer? server = ctx.Config is { } config
            ? SessionCommandHelpers.ResolveServer(config, serverName)
            : null;
        if (server is null)
            // Legacy Connect.cs:55: an address the client cannot turn into a server is "Invalid server IP".
            return ctx.Result.Fail(McStrings.Format("cmd.connect.invalid_ip", serverName));

        JavaVersion? pinnedVersion = SessionCommandHelpers.ResolveVersion(server.Version);

        if (server.Kind == ConfiguredServerKind.Realm)
            return RunRealm(ctx, server, account, pinnedVersion);

        var selection = new ServerSelection
        {
            Host = server.Host,
            Port = server.Port,
            Version = pinnedVersion,
        };

        try
        {
            ctx.Run(ct => ctx.Client.ReconnectAsync(selection, account, ct));

            // Legacy is quiet on success: Connect.cs:51 sets Done with no message and lets the restart machinery print.
            // The host announces the Reconnecting/Reconnected transitions itself.
            return ctx.Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }

    /// <summary>
    /// Resolves a <see cref="ConfiguredServerKind.Realm"/> entry to a live address through the client's Realms resolver, then feeds the resolved selection into the normal reconnect path.
    /// Realms needs a Microsoft session (retained as <see cref="Client.CurrentSession"/>) and a client version to report.
    /// Realms has no legacy counterpart in <c>Connect.cs</c>, so its progress lines keep their <see cref="CommandStrings"/> wording; each is printed once.
    /// </summary>
    private int RunRealm(CommandContext ctx, ConfiguredServer server, AccountSelection? account, JavaVersion? pinnedVersion)
    {
        string? clientVersion = pinnedVersion?.Version.Name ?? ctx.Client.NegotiatedVersion?.Version.Name;
        if (string.IsNullOrEmpty(clientVersion))
            return ctx.Result.Fail(CommandStrings.ConnectRealmNoVersion);

        string selector = string.IsNullOrWhiteSpace(server.RealmWorld) ? server.Name : server.RealmWorld;
        ctx.Output.WriteLine(CommandStrings.ConnectRealmResolving(selector));

        RealmConnectPlan plan;
        try
        {
            plan = ctx.Run(ct => RealmConnectPlanner.PlanAsync(
                ctx.Client.RealmsClientFactory, ctx.Client.CurrentSession, clientVersion, server, pinnedVersion, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        if (!plan.Success)
            return ctx.Result.Fail(plan.Error!);

        ServerSelection selection = plan.Selection!;
        ctx.Output.WriteLine(CommandStrings.ConnectRealmResolved(plan.WorldSelector, selection.Host, selection.Port));
        try
        {
            ctx.Run(ct => ctx.Client.ReconnectAsync(selection, account, ct));
            return ctx.Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }
}

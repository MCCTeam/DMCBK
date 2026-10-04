using System.Globalization;
using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>servers</c> command: write-back for <c>servers.toml</c>.
/// Today only <c>add</c> (saving is the requested operation; listing is <c>/connect</c>'s suggestion job and the file's own): it stores a named <c>host[:port]</c> and selects it, so the next start and the next bare prompt already know it.
/// </summary>
public sealed class ServersCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "servers";

    /// <inheritdoc/>
    public override string CmdDesc => ServersCommandText.Desc;

    /// <inheritdoc/>
    public override string CmdUsage => ServersCommandText.Usage;

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("add <name> <host[:port]>", "save a server to servers.toml and select it"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "servers add Home localhost:25565",
        "servers add Hypixel mc.hypixel.net",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["connect"];

    /// <inheritdoc/>
    public override string? ManTopic => "connecting";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenLiteral("add", h => h.ThenArgument("args", Arguments.GreedyString(), a => a
                .Executes(ctx => Add(ctx.Source, ctx.GetArgument<string>("args")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Add(CommandContext ctx, string rest)
    {
        // Greedy remainder, quotes honoured, so a multi-word name survives (connect it back quoted).
        if (CommandTokens.Split(rest) is not [{ } name, { } address])
            return ctx.Result.Fail(BuildUsage(ctx));

        if (ctx.Config is not { SourceFolder: { } folder })
            return ctx.Result.Fail(ServersCommandText.NoConfig);

        ConfiguredServer? parsed = SessionCommandHelpers.ResolveServer(ctx.Config, address);
        string bare = address.Trim();
        bool literal = parsed is not null
            && (string.Equals(parsed.Host, bare, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    $"{parsed.Host}:{parsed.Port.ToString(CultureInfo.InvariantCulture)}",
                    bare,
                    StringComparison.OrdinalIgnoreCase));
        if (parsed is null)
            return ctx.Result.Fail(ServersCommandText.InvalidAddress(address));

        if (!literal)
            return ctx.Result.Fail(ServersCommandText.LooksLikeSavedName(address));

        var entry = new ConfiguredServer { Name = name, Host = parsed.Host, Port = parsed.Port };
        try
        {
            ctx.Client.GetModule<IConfigurationStorage>()
                .SaveServer(entry, makeActive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.Result.Fail(ServersCommandText.SaveFailed(ex.Message));
        }

        // Live, so /connect <name> answers from this run, not just the next one (the LangCommand precedent: the snapshot the commands read moves with the file).
        var servers = ctx.Config.Servers.Servers
            .Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .Append(entry)
            .ToList();
        ctx.Commands.ReloadConfiguration(ctx.Config with
        {
            Servers = ctx.Config.Servers with { Servers = servers, ActiveServer = name },
        });

        return ctx.Result.Ok(ServersCommandText.Saved(
            name, entry.Host, entry.Port));
    }
}

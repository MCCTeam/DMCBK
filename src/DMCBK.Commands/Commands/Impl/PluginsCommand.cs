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
/// The <c>plugins</c> command: <c>list</c> / <c>enable</c> / <c>disable</c> / <c>unload</c> / <c>reload</c>, driving the attached <see cref="IPluginHost"/>.
/// Successor to legacy <c>Commands/Bots.cs</c> and keeps its wording: the listing is <c>cmd.bots.list</c>, an empty listing is <c>cmd.bots.noloaded</c>, an unknown id is <c>cmd.bots.notfound</c>, and <c>unload</c> accepts the legacy <c>all</c> form (<c>cmd.bots.unloaded_all</c>).
/// </summary>
public sealed class PluginsCommand : CommandBase
{
    private static readonly string[] HelpTopics =
    [
        "list", "load", "enable", "disable", "unload", "reload", "settings",
        "install", "uninstall", "update", "outdated", "pin", "unpin", "info", "deps",
        "marketplace", "market", "search", "new", "validate", "doctor", "ui",
    ];

    /// <inheritdoc/>
    public override string CmdName => "plugins";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.bots.desc");

    /// <inheritdoc/>
    public override string CmdUsage =>
        "plugins [list [outdated|local|enabled|disabled]|load <folder-or-cs>|enable|disable|unload|reload"
        + "|settings <id|all> regen|reset|install <what> [<version>] [yes]|uninstall <id> [purge]"
        + "|update [<id>|all] [yes]|outdated|pin <id> [<version>]|unpin <id>|info <id>|deps <id>"
        + "|search <text> [in <marketplace>]|marketplace [list|add <source> [as <name>]|remove <name>"
        + "|refresh [<name>]|auto-update <name> off|check|apply]|new <id>|validate [<folder>]|doctor]"
        + " [plugin id|all]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("list", "every discovered plugin and its state"),
        new("enable <id>", "enable one"),
        new("disable <id>", "disable one"),
        new("load <path>", "load a folder or a .cs file"),
        new("unload <id|all>", "unload"),
        new("reload [id]", "reload one, or all of them"),
        new("settings <id|all> regen", "rewrite settings.toml, keeping the values"),
        new("settings <id|all> reset", "rewrite settings.toml from the defaults"),
        new("list <filter>", "outdated, local, enabled or disabled"),
        new("install <what> [<version>] [yes]", "from owner/repo, a Git or archive URL, or a folder"),
        new("uninstall <id> [purge]", "remove it; purge deletes its settings and data too"),
        new("update [<id>|all] [yes]", "fetch newer versions of what MCC installed"),
        new("outdated", "what has a newer version, and what is held back"),
        new("pin <id> [<version>]", "hold a plugin where it is"),
        new("unpin <id>", "let it move again"),
        new("info <id>", "manifest, source, dependencies, languages"),
        new("deps <id>", "what it needs, and what needs it"),
        new("search <text> [in <marketplace>]", "look through the added marketplaces"),
        new("marketplace list", "which marketplaces are added"),
        new("marketplace add <source> [as <name>]", "from owner/repo, a URL, or a folder"),
        new("marketplace remove <name>", "forget one; its plugins stay installed"),
        new("marketplace refresh [<name>]", "re-fetch the catalogues"),
        new("marketplace auto-update <name> off|check|apply", "what it may do at startup"),
        new("new <id>", "scaffold a plugin folder that follows every convention"),
        new("validate [<folder>]", "check one folder, or every installed plugin"),
        new("doctor", "one report on everything wrong or worth knowing"),
        new("ui", "open the visual plugin manager (dialog hosts)"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "plugins list",
        "plugins reload farmer",
        "plugins settings all regen",
        "plugins install someone/mcc-xray@v0.3",
        "plugins install ./Downloads/my-plugin yes",
        "plugins outdated",
        "plugins update all",
        "plugins marketplace add MCCTeam/mcc-plugins",
        "plugins search discord",
        "plugins install discord-bridge@official",
        "plugins new hello-world",
        "plugins validate",
        "plugins doctor",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["lang"];

    /// <inheritdoc/>
    public override string? ManTopic => "plugins";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy mirrored a help node per subcommand (Bots.cs:21-24).
        foreach (string topic in HelpTopics)
            help.ThenLiteral(topic, h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => ListPlugins(ctx.Source))
            .ThenLiteral("list", h => h
                .Executes(ctx => ListPlugins(ctx.Source))
                .ThenLiteral(PluginsCommandText.FilterOutdated, f => f
                    .Executes(ctx => ListPlugins(ctx.Source, PluginsCommandText.FilterOutdated)))
                .ThenLiteral(PluginsCommandText.FilterLocal, f => f
                    .Executes(ctx => ListPlugins(ctx.Source, PluginsCommandText.FilterLocal)))
                .ThenLiteral(PluginsCommandText.FilterEnabled, f => f
                    .Executes(ctx => ListPlugins(ctx.Source, PluginsCommandText.FilterEnabled)))
                .ThenLiteral(PluginsCommandText.FilterDisabled, f => f
                    .Executes(ctx => ListPlugins(ctx.Source, PluginsCommandText.FilterDisabled))))
            .ThenLiteral("enable", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Act(ctx.Source, ctx.GetArgument<string>("id"), static (pluginHost, pluginId, token) => pluginHost.EnableAsync(pluginId, token)))))
            .ThenLiteral("disable", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Act(ctx.Source, ctx.GetArgument<string>("id"), static (pluginHost, pluginId, token) => pluginHost.DisableAsync(pluginId, token)))))
            .ThenLiteral("unload", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Unload(ctx.Source, ctx.GetArgument<string>("id")))))
            // Bare `reload` reloads every loaded plugin.
            // Without this the literal parsed but had no executable node behind it, so `plugins reload` answered "Unknown or incomplete command" while the host had had ReloadAllAsync all along.
            .ThenLiteral("reload", h => h
                .Executes(ctx => ReloadAll(ctx.Source))
                .ThenArgument("id", Arguments.QuotableString(), a => a
                    .Suggests(SessionCommandHelpers.SuggestPlugins)
                    .Executes(ctx => Act(ctx.Source, ctx.GetArgument<string>("id"), static (pluginHost, pluginId, token) => pluginHost.ReloadAsync(pluginId, token)))))
            // Absorbed from the removed `plugin load` command: one plugin verb, not two.
            .ThenLiteral("load", h => h.ThenArgument("path", Arguments.GreedyString(), a => a
                .Executes(ctx => Load(ctx.Source, ctx.GetArgument<string>("path")))))
            // Settings comments are frozen at generation time, the same rule client.toml follows.
            // These are the explicit user action that unfreezes them, so a language change can reach a file that was written before it.
            .ThenLiteral("settings", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SuggestPluginsOrAll)
                .ThenLiteral("regen", r => r.Executes(ctx => Settings(
                    ctx.Source,
                    ctx.GetArgument<string>("id"),
                    static (pluginHost, pluginId, token) => pluginHost.RegenerateSettingsAsync(pluginId, token))))
                .ThenLiteral("reset", r => r.Executes(ctx => Settings(
                    ctx.Source,
                    ctx.GetArgument<string>("id"),
                    static (pluginHost, pluginId, token) => pluginHost.ResetSettingsAsync(pluginId, token))))))
            // The tail of `install` arrives as one greedy argument and is split in `Install`.
            // A quotable string is Brigadier's `string()`, whose unquoted set is [0-9A-Za-z_.+-], so `./my-plugin`, `https://host/x.git#v1` and the range `^1` each stop the reader at their first slash, colon or caret and the parse fails on trailing data.
            .ThenLiteral("install", h => h.ThenArgument("what", Arguments.GreedyString(), a => a
                .Suggests(SuggestInstallable)
                .Executes(ctx => Install(ctx.Source, ctx.GetArgument<string>("what")))))
            .ThenLiteral("uninstall", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Uninstall(ctx.Source, ctx.GetArgument<string>("id"), false))
                .ThenLiteral(PluginsCommandText.Purge, p => p
                    .Executes(ctx => Uninstall(ctx.Source, ctx.GetArgument<string>("id"), true)))))
            .ThenLiteral("update", h => h
                .Executes(ctx => Update(ctx.Source, null, false))
                .ThenArgument("id", Arguments.QuotableString(), a => a
                    .Suggests(SuggestPluginsOrAll)
                    .Executes(ctx => Update(ctx.Source, ctx.GetArgument<string>("id"), false))
                    .ThenLiteral(PluginsCommandText.Yes, y => y
                        .Executes(ctx => Update(ctx.Source, ctx.GetArgument<string>("id"), true)))))
            .ThenLiteral("rollback", h => h.ThenArgument("transaction", Arguments.QuotableString(), a => a
                .Executes(ctx => Market(ctx.Source, market => market.RollbackAsync(ctx.GetArgument<string>("transaction"), default)))))
            .ThenLiteral("outdated", h => h.Executes(ctx => Outdated(ctx.Source)))
            .ThenLiteral("pin", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Market(
                    ctx.Source, market => market.PinAsync(ctx.GetArgument<string>("id"), null, default)))
                .ThenArgument("version", Arguments.QuotableString(), v => v
                    .Executes(ctx => Market(ctx.Source, market => market.PinAsync(
                        ctx.GetArgument<string>("id"), ctx.GetArgument<string>("version"), default))))))
            .ThenLiteral("unpin", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Market(
                    ctx.Source, market => market.UnpinAsync(ctx.GetArgument<string>("id"), default)))))
            .ThenLiteral("info", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Info(ctx.Source, ctx.GetArgument<string>("id")))))
            .ThenLiteral("deps", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Suggests(SessionCommandHelpers.SuggestPlugins)
                .Executes(ctx => Deps(ctx.Source, ctx.GetArgument<string>("id")))))
            .ThenLiteral("search", h => h.ThenArgument("query", Arguments.GreedyString(), a => a
                .Executes(ctx => Search(ctx.Source, ctx.GetArgument<string>("query")))))
            .ThenLiteral("new", h => h.ThenArgument("id", Arguments.QuotableString(), a => a
                .Executes(ctx => New(ctx.Source, ctx.GetArgument<string>("id")))))
            // A folder path holds slashes and dots, which a quotable string stops reading at.
            .ThenLiteral("validate", h => h
                .Executes(ctx => Validate(ctx.Source, null))
                .ThenArgument("folder", Arguments.GreedyString(), a => a
                    .Executes(ctx => Validate(ctx.Source, ctx.GetArgument<string>("folder")))))
            .ThenLiteral("doctor", h => h.Executes(ctx => Doctor(ctx.Source)))
            .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
            // Two spellings of one node rather than a redirect, so both complete the whole subtree.
            .ThenLiteral(PluginsCommandText.Marketplace, BuildMarketplaceNode)
            .ThenLiteral(PluginsCommandText.MarketAlias, BuildMarketplaceNode)
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Lists every DISCOVERED plugin, one per line, with its enabled state.
    /// </summary>
    /// <remarks>
    /// Legacy listed only the loaded extensions, joined with " ," on one line (Bots.cs:56, GetLoadedChatBots).
    /// That hid the two states a user actually needs to tell apart: a plugin that is switched off, and a plugin that is switched on but failed to load.
    /// Both are now visible, and a failure carries its own status text.
    /// </remarks>
    private int ListPlugins(CommandContext ctx, string? filter = null)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        IReadOnlyList<PluginInfo> plugins = host.List();
        if (filter is not null && !TryFilter(ctx, plugins, filter, out plugins, out int failure))
            return failure;

        if (plugins.Count == 0)
        {
            return ctx.Result.Ok(
                filter is null ? CommandStrings.PluginsNoneDiscovered : PluginsCommandText.ListFilteredNone(filter));
        }

        string enabledGlyph = ctx.Glyphs.PluginEnabled;
        string disabledGlyph = ctx.Glyphs.PluginDisabled;

        var sb = new StringBuilder();
        sb.Append(CommandStrings.PluginsHeader(plugins.Count));

        foreach (PluginInfo plugin in plugins.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            string entry = plugin.Entry == PluginEntryKind.Source
                ? CommandStrings.PluginsEntrySource
                : CommandStrings.PluginsEntryCompiled;

            sb.Append('\n').Append(CommandStrings.PluginsRow(
                plugin.Enabled ? enabledGlyph : disabledGlyph, plugin.Id, plugin.Version, entry));

            if (plugin.Offline)
                sb.Append(PluginsCommandText.ListOffline);

            // An enabled plugin that is NOT loaded is the only case the glyph cannot already express, so only that one carries a trailing note.
            // Appending the status unconditionally just restated the glyph ("loaded" next to a tick, "disabled" next to a cross) on every healthy row.
            if (plugin.Enabled && !plugin.Loaded)
            {
                sb.Append(" - ").Append(
                    string.IsNullOrWhiteSpace(plugin.Status) ? CommandStrings.PluginsNotLoaded : plugin.Status);
            }
        }

        return ctx.Result.Ok(sb.ToString());
    }

    /// <summary>
    /// Narrows the listing.
    /// <c>enabled</c> and <c>disabled</c> come from the host.
    /// <c>local</c> and <c>outdated</c> need the market, so without one they say so rather than answering something else.
    /// </summary>
    private static bool TryFilter(
        CommandContext ctx,
        IReadOnlyList<PluginInfo> plugins,
        string filter,
        out IReadOnlyList<PluginInfo> filtered,
        out int failure)
    {
        filtered = plugins;
        failure = 0;
        switch (filter)
        {
            case PluginsCommandText.FilterEnabled:
                filtered = [.. plugins.Where(static p => p.Enabled)];
                return true;

            case PluginsCommandText.FilterDisabled:
                filtered = [.. plugins.Where(static p => !p.Enabled)];
                return true;

            case PluginsCommandText.FilterLocal:
                {
                    if (ctx.Client.PluginMarket is not { } market)
                    {
                        failure = ctx.Result.Fail(PluginsCommandText.NoMarket);
                        return false;
                    }

                    var local = new List<PluginInfo>();
                    foreach (PluginInfo plugin in plugins)
                    {
                        if (ctx.Run(ct => market.InfoAsync(plugin.Id, ct)) is { IsLocal: true })
                            local.Add(plugin);
                    }

                    filtered = local;
                    return true;
                }

            case PluginsCommandText.FilterOutdated:
                {
                    if (ctx.Client.PluginMarket is not { } market)
                    {
                        failure = ctx.Result.Fail(PluginsCommandText.NoMarket);
                        return false;
                    }

                    IReadOnlyList<PluginUpdateInfo> outdated = ctx.Run(market.OutdatedAsync);
                    var ids = new HashSet<string>(outdated.Select(static o => o.Id), StringComparer.OrdinalIgnoreCase);
                    filtered = [.. plugins.Where(p => ids.Contains(p.Id))];
                    return true;
                }

            default:
                filtered = plugins;
                return true;
        }
    }

    /// <summary>
    /// <c>install &lt;what&gt; [&lt;version&gt;] [yes]</c>.
    /// Resolving, gating and confirming all happen in the market.
    /// This decides whether there is one and renders what comes back.
    /// </summary>
    private int Install(CommandContext ctx, string arguments)
    {
        if (!TryReadInstallArguments(arguments, out string what, out string? version, out bool assumeYes, out PluginInstallOptions options))
            return ctx.Result.Fail(PluginsCommandText.InstallUsage);

        return Market(ctx, market => market.InstallAsync(what, options, version, assumeYes, default));
    }

    /// <summary>
    /// Splits the one greedy argument behind <c>install</c> back into <c>&lt;what&gt;</c>, an optional version or range, and the <c>yes</c> keyword.
    /// Quotes are honoured, so a folder whose name contains a space is still installable.
    /// An unbalanced quote or a fourth token gets the usage line instead of being handed to the market as a source.
    /// </summary>
    internal static bool TryReadInstallArguments(string arguments, out string what, out string? version, out bool assumeYes)
        => TryReadInstallArguments(arguments, out what, out version, out assumeYes, out _);

    internal static bool TryReadInstallArguments(string arguments, out string what, out string? version, out bool assumeYes, out PluginInstallOptions options)
    {
        what = string.Empty; version = null; assumeYes = false; options = new();
        if (CommandTokens.Split(arguments) is not { Count: > 0 } tokens) return false;
        what = tokens[0]; bool source = false, fallback = false, prerelease = false;
        foreach (string token in tokens.Skip(1))
        {
            switch (token)
            {
                case "--source": if (source) return false; source = true; break;
                case "--source-fallback": if (fallback) return false; fallback = true; break;
                case "--prerelease": if (prerelease) return false; prerelease = true; break;
                case "yes": if (assumeYes) return false; assumeYes = true; break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal) || version is not null) return false;
                    version = token; break;
            }
        }
        options = new(source, fallback, prerelease); return true;
    }

    /// <summary>
    /// The <c>marketplace</c> subtree, built twice so <c>market</c> is the same grammar and not a shorter one.
    /// </summary>
    private void BuildMarketplaceNode(CommandNodeBuilder<CommandContext> node) => node
        .Executes(ctx => Marketplaces(ctx.Source))
        .ThenLiteral("list", l => l.Executes(ctx => Marketplaces(ctx.Source)))
        // The source carries slashes, colons and a possible '#', none of which Brigadier's unquoted set holds, so it arrives greedy and the 'as <name>' tail is split off here.
        .ThenLiteral("add", a => a.ThenArgument("source", Arguments.GreedyString(), s => s
            .Executes(ctx => MarketplaceAdd(ctx.Source, ctx.GetArgument<string>("source")))))
        .ThenLiteral("remove", r => r.ThenArgument("name", Arguments.QuotableString(), n => n
            .Suggests(SuggestMarketplaces)
            .Executes(ctx => Market(
                ctx.Source, market => market.MarketplaceRemoveAsync(ctx.GetArgument<string>("name"), default)))))
        .ThenLiteral("refresh", r => r
            .Executes(ctx => Market(ctx.Source, market => market.MarketplaceRefreshAsync(null, default)))
            .ThenArgument("name", Arguments.QuotableString(), n => n
                .Suggests(SuggestMarketplaces)
                .Executes(ctx => Market(
                    ctx.Source, market => market.MarketplaceRefreshAsync(ctx.GetArgument<string>("name"), default)))))
        .ThenLiteral("auto-update", u => u.ThenArgument("name", Arguments.QuotableString(), n => n
            .Suggests(SuggestMarketplaces)
            .ThenArgument("policy", Arguments.QuotableString(), p => p
                .Suggests(SuggestPolicies)
                .Executes(ctx => Market(ctx.Source, market => market.MarketplaceAutoUpdateAsync(
                    ctx.GetArgument<string>("name"), ctx.GetArgument<string>("policy"), default))))));

    /// <summary><c>marketplace add &lt;source&gt; [as &lt;name&gt;]</c>.</summary>
    private int MarketplaceAdd(CommandContext ctx, string arguments)
    {
        if (!TryReadAddArguments(arguments, out string source, out string? name))
            return ctx.Result.Fail(PluginsCommandText.MarketplaceAddUsage);

        return Market(ctx, market => market.MarketplaceAddAsync(source, name, default));
    }

    /// <summary>
    /// Splits <c>&lt;source&gt; [as &lt;name&gt;]</c> out of one greedy argument.
    /// Anything else gets the usage line rather than being handed to the market as a source.
    /// </summary>
    internal static bool TryReadAddArguments(string arguments, out string source, out string? name)
    {
        source = string.Empty;
        name = null;
        if (CommandTokens.Split(arguments) is not { Count: > 0 } tokens)
            return false;

        if (tokens.Count == 1)
        {
            source = tokens[0];
            return true;
        }

        if (tokens.Count != 3
            || !string.Equals(tokens[1], PluginsCommandText.As, StringComparison.OrdinalIgnoreCase))
            return false;

        source = tokens[0];
        name = tokens[2];
        return true;
    }

    /// <summary>Splits <c>&lt;text&gt; [in &lt;marketplace&gt;]</c> out of one greedy argument.</summary>
    internal static bool TryReadSearchArguments(string arguments, out string text, out string? marketplace)
    {
        text = string.Empty;
        marketplace = null;
        if (CommandTokens.Split(arguments) is not { Count: > 0 } tokens)
            return false;

        int end = tokens.Count;
        if (end >= 3 && string.Equals(tokens[end - 2], PluginsCommandText.In, StringComparison.OrdinalIgnoreCase))
        {
            marketplace = tokens[end - 1];
            end -= 2;
        }

        text = string.Join(' ', tokens.Take(end));
        return text.Length > 0;
    }

    private int Marketplaces(CommandContext ctx)
    {
        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        IReadOnlyList<MarketplaceInfo> rows;
        try
        {
            rows = ctx.Run(market.MarketplacesAsync);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        if (rows.Count == 0)
            return ctx.Result.Ok(PluginsCommandText.MarketplaceNone);

        var sb = new StringBuilder(PluginsCommandText.MarketplaceHeader(rows.Count));
        foreach (MarketplaceInfo row in rows)
        {
            sb.Append('\n').Append(PluginsCommandText.MarketplaceRow(
                row.Name, row.Source, row.PluginCount, row.AutoUpdate));
            sb.Append('\n').Append(row.Refreshed is { } when
                ? PluginsCommandText.MarketplaceRefreshedAt(when.ToString("u", CultureInfo.InvariantCulture))
                : PluginsCommandText.MarketplaceNeverRefreshed);
            if (row.Error is { Length: > 0 } error)
                sb.Append('\n').Append(PluginsCommandText.MarketplaceRowError(error));
        }

        return ctx.Result.Ok(sb.ToString());
    }

    private int Search(CommandContext ctx, string arguments)
    {
        if (!TryReadSearchArguments(arguments, out string text, out string? marketplace))
            return ctx.Result.Fail(PluginsCommandText.SearchUsage);

        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        IReadOnlyList<MarketplaceSearchResult> rows;
        try
        {
            rows = ctx.Run(ct => market.SearchAsync(text, marketplace, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        if (rows.Count == 0)
            return ctx.Result.Ok(PluginsCommandText.SearchNone(text));

        var sb = new StringBuilder(PluginsCommandText.SearchHeader(rows.Count));
        foreach (MarketplaceSearchResult row in rows)
        {
            sb.Append('\n').Append(PluginsCommandText.SearchRow(
                row.Id, row.Marketplace, row.Version, row.Description));
            if (row.Installed is { Length: > 0 } installed)
                sb.Append('\n').Append(PluginsCommandText.SearchInstalled(installed));

            // The compatibility marker: a version this client is too old for is never offered silently.
            if (row.Incompatibility is { Length: > 0 } blocked)
                sb.Append('\n').Append(PluginsCommandText.SearchBlocked(blocked));
        }

        return ctx.Result.Ok(sb.ToString());
    }

    private int Uninstall(CommandContext ctx, string id, bool purge)
        => Market(ctx, market => market.UninstallAsync(id, purge, default));

    private int Update(CommandContext ctx, string? id, bool assumeYes)
    {
        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        // An update asks the same confirmation an install does, and a scripted run cannot type an answer.
        // The callback is restored whatever happens.
        Func<InstallConfirmation, CancellationToken, ValueTask<bool>> confirm = market.Confirm;
        if (assumeYes)
            market.Confirm = static (_, _) => ValueTask.FromResult(true);

        try
        {
            PluginActionResult result = ctx.Run(ct => market.UpdateAsync(id, ct));
            return result.Success ? ctx.Result.Ok(result.Message) : ctx.Result.Fail(result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
        finally
        {
            market.Confirm = confirm;
        }
    }

    private int Outdated(CommandContext ctx)
    {
        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        IReadOnlyList<PluginUpdateInfo> rows;
        try
        {
            rows = ctx.Run(market.OutdatedAsync);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        if (rows.Count == 0)
            return ctx.Result.Ok(PluginsCommandText.OutdatedNone);

        var sb = new StringBuilder();
        sb.Append(PluginsCommandText.OutdatedHeader(rows.Count));
        foreach (PluginUpdateInfo row in rows)
        {
            if (row.Error is { Length: > 0 } error)
            {
                sb.Append('\n').Append(PluginsCommandText.OutdatedError(row.Id, error));
                continue;
            }

            sb.Append('\n').Append(PluginsCommandText.OutdatedRow(
                row.Id, row.InstalledVersion, row.AvailableVersion ?? "?"));

            // A version this client cannot take is reported with the reason, never as an update waiting.
            if (row.BlockedReason is { Length: > 0 } blocked)
                sb.Append('\n').Append(PluginsCommandText.OutdatedBlocked(blocked));

            if (row.Pinned)
                sb.Append('\n').Append(PluginsCommandText.OutdatedPinned(row.Id));
        }

        return ctx.Result.Ok(sb.ToString());
    }

    private int Info(CommandContext ctx, string id)
    {
        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        PluginMarketInfo? info;
        try
        {
            info = ctx.Run(ct => market.InfoAsync(id, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        if (info is null)
            return ctx.Result.Fail(McStrings.Format("cmd.bots.notfound", id));

        var sb = new StringBuilder(PluginsCommandText.InfoHeader(info.Id, info.Version));
        Row(PluginsCommandText.InfoLabelState, info.Loaded
            ? PluginsCommandText.InfoLoaded
            : info.Enabled ? info.Status ?? PluginsCommandText.InfoNotLoaded : PluginsCommandText.InfoDisabled);
        Row(PluginsCommandText.InfoLabelEntry, info.Entry == PluginEntryKind.Source
            ? CommandStrings.PluginsEntrySource
            : CommandStrings.PluginsEntryCompiled);
        Row(PluginsCommandText.InfoLabelFolder, info.Folder);
        Row(PluginsCommandText.InfoLabelSource, info.Source ?? (info.IsLocal ? PluginsCommandText.InfoLocal : null));
        Row(PluginsCommandText.InfoLabelMarket, info.Marketplace);
        Row(PluginsCommandText.InfoLabelInstalled, info.Installed?.ToString("u", CultureInfo.InvariantCulture));
        Row(PluginsCommandText.InfoLabelPinned, info.Pinned
            ? PluginsCommandText.InfoPinnedAt(info.PinnedVersion ?? info.Version)
            : null);
        Row(PluginsCommandText.InfoLabelDescription, info.Description);
        Row(PluginsCommandText.InfoLabelHomepage, info.Homepage);
        Row(PluginsCommandText.InfoLabelTags, Join(info.Tags));
        Row(PluginsCommandText.InfoLabelUses, Join(info.Uses));
        Row(PluginsCommandText.InfoLabelRequires, Join(info.Requires));
        Row(PluginsCommandText.InfoLabelOptional, Join(info.Optional));
        Row(PluginsCommandText.InfoLabelDependents, Join(info.Dependents));
        Row(PluginsCommandText.InfoLabelExports, Join(info.Exports));
        Row(PluginsCommandText.InfoLabelLanguages, Join(info.Languages));
        Row(PluginsCommandText.InfoLabelManual, Join(info.ManualTopics));
        Row(PluginsCommandText.InfoLabelOffline, info.Offline ? PluginsCommandText.InfoOffline : null);
        Row(PluginsCommandText.InfoLabelErrors, info.FaultCount == 0
            ? null
            : PluginsCommandText.InfoFaults(info.FaultCount, info.LastError ?? string.Empty));
        return ctx.Result.Ok(sb.ToString());

        void Row(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                sb.Append('\n').Append(PluginsCommandText.InfoRow(label, value));
        }

        static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(", ", values);
    }

    private int Deps(CommandContext ctx, string id)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        if (host.DependenciesOf(id) is not { } deps)
            return ctx.Result.Fail(McStrings.Format("cmd.bots.notfound", id));

        var sb = new StringBuilder(PluginsCommandText.DepsHeader(deps.Id));
        if (deps.Requires.Count == 0 && deps.Optional.Count == 0 && deps.Dependents.Count == 0)
            return ctx.Result.Ok(sb.Append('\n').Append(PluginsCommandText.DepsNone).ToString());

        if (deps.Requires.Count > 0)
            sb.Append('\n').Append(PluginsCommandText.DepsRequires(string.Join(", ", deps.Requires)));

        if (deps.Optional.Count > 0)
            sb.Append('\n').Append(PluginsCommandText.DepsOptional(string.Join(", ", deps.Optional)));

        if (deps.Dependents.Count > 0)
            sb.Append('\n').Append(PluginsCommandText.DepsDependents(string.Join(", ", deps.Dependents)));

        if (deps.Missing.Count > 0)
            sb.Append('\n').Append(PluginsCommandText.DepsMissing(string.Join(", ", deps.Missing)));

        return ctx.Result.Ok(sb.ToString());
    }

    /// <summary>
    /// <c>new &lt;id&gt;</c>.
    /// The scaffold is written disabled, so the author reads it before it runs.
    /// </summary>
    private int New(CommandContext ctx, string id)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        if (string.IsNullOrWhiteSpace(id))
            return ctx.Result.Fail(PluginsCommandText.NewUsage);

        PluginActionResult result = host.Scaffold(id);
        return result.Success ? ctx.Result.Ok(result.Message) : ctx.Result.Fail(result.Message);
    }

    /// <summary>
    /// <c>validate [&lt;folder&gt;]</c>.
    /// Nothing is loaded, so this answers for a plugin that is disabled, or refused, or not installed at all.
    /// </summary>
    private int Validate(CommandContext ctx, string? folder)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        IReadOnlyList<PluginCheckResult> results = host.Validate(folder?.Trim().Trim('"'));
        if (results.Count == 0)
            return ctx.Result.Ok(PluginsCommandText.ValidateNothing);

        int bad = results.Count(r => !r.IsValid);
        var sb = new StringBuilder(PluginsCommandText.ValidateHeader(results.Count, bad));
        foreach (PluginCheckResult result in results)
        {
            if (result.Problems.Count == 0)
            {
                sb.Append('\n').Append(PluginsCommandText.ValidateSubjectOk(result.Id));
                continue;
            }

            sb.Append('\n').Append(PluginsCommandText.ValidateSubject(result.Id));
            foreach (PluginCheckProblem problem in result.Problems)
            {
                sb.Append('\n').Append(PluginsCommandText.ValidateProblem(
                    problem.Error ? PluginsCommandText.SeverityError : PluginsCommandText.SeverityWarning,
                    PluginsCommandText.ValidateAt(problem.File, problem.Line),
                    problem.Message));
            }
        }

        return bad == 0 ? ctx.Result.Ok(sb.ToString()) : ctx.Result.Fail(sb.ToString());
    }

    /// <summary><c>doctor</c>. One pass over everything the host knows is wrong or worth saying.</summary>
    private int Doctor(CommandContext ctx)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        PluginDoctorReport report = host.Doctor();
        var sb = new StringBuilder(PluginsCommandText.DoctorHeader(report.Discovered, report.Loaded));
        if (report.Notes.Count == 0)
            return ctx.Result.Ok(sb.Append('\n').Append(PluginsCommandText.DoctorHealthy).ToString());

        foreach (PluginDoctorNote note in report.Notes)
        {
            sb.Append('\n').Append(PluginsCommandText.DoctorRow(
                note.Error ? PluginsCommandText.SeverityError : PluginsCommandText.DoctorNote,
                note.Id,
                note.Detail));
        }

        return report.IsHealthy ? ctx.Result.Ok(sb.ToString()) : ctx.Result.Fail(sb.ToString());
    }

    /// <summary>
    /// <c>ui</c>.
    /// Opens the host's visual plugin manager when it has one; anywhere else the text verbs above are the interface, so this says that instead of opening nothing.
    /// </summary>
    private int Ui(CommandContext ctx)
        => ctx.Ui?.TryOpenPluginManager() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(PluginsCommandText.UiNoDialog);

    /// <summary>The shared body of every verb that drives the market and renders one result line.</summary>
    private int Market(CommandContext ctx, Func<IPluginMarket, Task<PluginActionResult>> action)
    {
        if (ctx.Client.PluginMarket is not { } market)
            return ctx.Result.Fail(PluginsCommandText.NoMarket);

        try
        {
            PluginActionResult result = ctx.Run(_ => action(market));
            return result.Success ? ctx.Result.Ok(result.Message) : ctx.Result.Fail(result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }

    private int ReloadAll(CommandContext ctx)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        try
        {
            PluginActionResult result = ctx.Run(ct => host.ReloadAllAsync(ct));
            return result.Success ? ctx.Result.Ok(result.Message) : ctx.Result.Fail(result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }

    private int Load(CommandContext ctx, string path)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        try
        {
            PluginActionResult result = ctx.Run(ct => host.LoadAsync(path, ct));
            return result.Success ? ctx.Result.Ok(result.Message) : ctx.Result.Fail(result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }

    private int Unload(CommandContext ctx, string id)
    {
        // Legacy Bots.cs:74: "all" unloads every loaded extension, and refuses when none is loaded.
        if (!string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
            return Act(ctx, id, static (pluginHost, pluginId, token) => pluginHost.UnloadAsync(pluginId, token), McStrings.Format("cmd.bots.unloaded", id));

        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        List<PluginInfo> loaded = [];
        foreach (PluginInfo plugin in host.List())
        {
            if (plugin.Loaded)
                loaded.Add(plugin);
        }

        if (loaded.Count == 0)
            return ctx.Result.Fail(McStrings.Get("cmd.bots.noloaded"));

        try
        {
            foreach (PluginInfo plugin in loaded)
                ctx.Run(ct => host.UnloadAsync(plugin.Id, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        return ctx.Result.Ok(McStrings.Get("cmd.bots.unloaded_all"));
    }

    /// <summary>
    /// <c>settings &lt;id|all&gt; regen|reset</c>.
    /// The <c>all</c> form runs over every LOADED plugin, because an unloaded one has no settings type and therefore no schema to write a file from; it reports each result on its own line rather than collapsing them, since one plugin failing must not read as all of them failing.
    /// </summary>
    private int Settings(
        CommandContext ctx,
        string id,
        Func<IPluginHost, string, CancellationToken, Task<PluginActionResult>> action)
    {
        if (!string.Equals(id, PluginsCommandText.All, StringComparison.OrdinalIgnoreCase))
            return Act(ctx, id, action);

        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        List<PluginInfo> loaded = [.. host.List().Where(static p => p.Loaded)];
        if (loaded.Count == 0)
            return ctx.Result.Fail(PluginsCommandText.SettingsNoneLoaded);

        var sb = new StringBuilder();
        sb.Append(PluginsCommandText.SettingsHeader(loaded.Count));
        bool allSucceeded = true;
        try
        {
            foreach (PluginInfo plugin in loaded.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                PluginActionResult result = ctx.Run(ct => action(host, plugin.Id, ct));
                allSucceeded &= result.Success;
                sb.Append('\n').Append(PluginsCommandText.SettingsRow(result.Message));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }

        return allSucceeded ? ctx.Result.Ok(sb.ToString()) : ctx.Result.Fail(sb.ToString());
    }

    /// <summary>Suggests the added marketplaces by name.</summary>
    private static async ValueTask SuggestMarketplaces(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        if (ctx.Source.Client.PluginMarket is not { } market)
            return;

        foreach (MarketplaceInfo row in await market.MarketplacesAsync().ConfigureAwait(false))
        {
            if (row.Name.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(row.Name);
        }
    }

    /// <summary>Suggests the three auto-update policies.</summary>
    private static ValueTask SuggestPolicies(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (string policy in AutoUpdatePolicy.All)
        {
            if (policy.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(policy);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Suggests what <c>install</c> can take by name: the plugin ids the added catalogues hold, qualified when two marketplaces offer the same id.
    /// Paths and URLs are typed, not completed.
    /// </summary>
    private static async ValueTask SuggestInstallable(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        if (ctx.Source.Client.PluginMarket is not { } market)
            return;

        IReadOnlyList<MarketplaceSearchResult> entries =
            await market.SearchAsync(string.Empty).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MarketplaceSearchResult entry in entries)
            counts[entry.Id] = counts.GetValueOrDefault(entry.Id) + 1;

        foreach (MarketplaceSearchResult entry in entries)
        {
            string suggestion = counts[entry.Id] > 1 ? $"{entry.Id}@{entry.Marketplace}" : entry.Id;
            if (suggestion.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(suggestion);
        }
    }

    /// <summary>Suggests every discovered plugin id, plus the <c>all</c> keyword the settings verbs take.</summary>
    private static async ValueTask SuggestPluginsOrAll(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        await SessionCommandHelpers.SuggestPlugins(ctx, sink).ConfigureAwait(false);
        if (PluginsCommandText.All.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
            sink.Suggest(PluginsCommandText.All);
    }

    private int Act(
        CommandContext ctx,
        string id,
        Func<IPluginHost, string, CancellationToken, Task<PluginActionResult>> action,
        string? successMessage = null)
    {
        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Fail(CommandStrings.PluginsNoHost);

        // Legacy Bots.cs:88 reported a typo'd name itself rather than letting the subsystem answer.
        if (!SessionCommandHelpers.IsKnownPlugin(host, id))
            return ctx.Result.Fail(McStrings.Format("cmd.bots.notfound", id));

        try
        {
            PluginActionResult result = ctx.Run(ct => action(host, id, ct));
            return result.Success
                ? ctx.Result.Ok(successMessage ?? result.Message)
                : ctx.Result.Fail(result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }
}

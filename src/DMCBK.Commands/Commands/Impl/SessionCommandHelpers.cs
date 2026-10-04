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

/// <summary>Shared helpers for the connect/reco config-backed selection and the plugin management commands.</summary>
internal static class SessionCommandHelpers
{
    public static ConfiguredServer? FindServer(DmcbkConfiguration config, string name)
    {
        foreach (ConfiguredServer server in config.Servers.Servers)
        {
            if (string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))
                return server;
        }

        return null;
    }

    /// <summary>
    /// The server <c>connect</c> was given: a name from servers.toml, or a bare <c>host</c> or <c>host:port</c>.
    /// The command has documented the address form since it was written and only ever matched names, so an address answered "Invalid server IP" no matter how well formed it was.
    /// </summary>
    public static ConfiguredServer? ResolveServer(DmcbkConfiguration config, string value)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (FindServer(config, value) is { } configured)
            return configured;

        string address = (value ?? string.Empty).Trim();
        if (address.Length == 0 || address.Any(char.IsWhiteSpace))
            return null;

        int colon = address.LastIndexOf(':');
        if (colon < 0)
            return new ConfiguredServer { Name = address, Host = address };

        if (colon == 0 || colon == address.Length - 1
            || !ushort.TryParse(address[(colon + 1)..], out ushort port))
            return null;

        return new ConfiguredServer { Name = address, Host = address[..colon], Port = port };
    }

    public static ConfiguredAccount? FindAccount(DmcbkConfiguration config, string name)
    {
        foreach (ConfiguredAccount account in config.Accounts.Accounts)
        {
            if (string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase))
                return account;
        }

        return null;
    }

    /// <summary>
    /// Resolves the optional account argument shared by <c>connect</c> and <c>reco</c>.
    /// A blank argument means "keep the current account" (legacy Connect.cs:45 / Reco.cs:44); anything else must name a configured account or the command fails with the legacy <c>cmd.connect.unknown</c> line.
    /// </summary>
    public static bool TryResolveAccount(
        CommandContext ctx, string? accountName, out AccountSelection? account, out int failure)
    {
        account = null;
        failure = 0;

        if (string.IsNullOrWhiteSpace(accountName))
            return true;

        string name = accountName.Trim();
        ConfiguredAccount? configured = ctx.Config is { } config ? FindAccount(config, name) : null;
        if (configured is null)
        {
            failure = ctx.Result.Fail(McStrings.Format("cmd.connect.unknown", name));
            return false;
        }

        account = new AccountSelection { Account = ToAccount(configured) };
        return true;
    }

    public static DmcbkAccount ToAccount(ConfiguredAccount account)
    {
        string user = string.IsNullOrWhiteSpace(account.Login) ? account.Name : account.Login;
        if (account.Kind == DmcbkAccountKind.Offline)
            return DmcbkAccount.Offline(user);

        Uri? authServer = null;
        if (!string.IsNullOrWhiteSpace(account.AuthServer))
        {
            authServer = Uri.TryCreate(account.AuthServer, UriKind.Absolute, out Uri? absolute)
                ? absolute
                : new Uri($"https://{account.AuthServer}");
        }

        return new DmcbkAccount { Kind = account.Kind, User = user, AuthServerBaseUrl = authServer };
    }

    public static JavaVersion? ResolveVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "auto", StringComparison.OrdinalIgnoreCase))
            return null;

        return JavaVersions.TryGetByName(version, out JavaVersion resolved) ? resolved : null;
    }

    /// <summary>True when the host has discovered a plugin with that id (case-insensitive, as legacy matched bot names).</summary>
    public static bool IsKnownPlugin(IPluginHost host, string id)
    {
        foreach (PluginInfo plugin in host.List())
        {
            if (string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Suggests configured server names (servers.toml), preserving the legacy <c>ServerNickArgumentType.ListSuggestions</c> behavior now that UMPK separates parsing from suggesting.
    /// </summary>
    public static ValueTask SuggestServers(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        if (ctx.Source.Config is { } config)
        {
            foreach (ConfiguredServer server in config.Servers.Servers)
            {
                if (!string.IsNullOrEmpty(server.Name)
                    && server.Name.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(server.Name);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Suggests configured account names (accounts.toml), preserving the legacy <c>AccountNickArgumentType.ListSuggestions</c> behavior now that UMPK separates parsing from suggesting.
    /// </summary>
    public static ValueTask SuggestAccounts(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        if (ctx.Source.Config is { } config)
        {
            foreach (ConfiguredAccount account in config.Accounts.Accounts)
            {
                if (!string.IsNullOrEmpty(account.Name)
                    && account.Name.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(account.Name);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Suggests discovered plugin ids, the successor to legacy <c>BotNameArgumentType.ListSuggestions</c>.
    /// Unloaded and disabled plugins are included so <c>plugins enable</c> can complete them.
    /// </summary>
    public static ValueTask SuggestPlugins(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        if (ctx.Source.Client.PluginHost is { } host)
        {
            foreach (PluginInfo plugin in host.List())
            {
                if (!string.IsNullOrEmpty(plugin.Id)
                    && plugin.Id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(plugin.Id);
            }
        }

        return ValueTask.CompletedTask;
    }
}

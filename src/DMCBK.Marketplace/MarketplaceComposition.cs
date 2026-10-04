using System.Globalization;
using DMCBK.Core.Plugins;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;

namespace DMCBK.Core;

/// <summary>Explicit marketplace storage and source selection.</summary>
public sealed record MarketplaceOptions(string PluginRoot, string RegistryPath)
{
    /// <summary>Host timing and connectivity for unattended checks.</summary>
    public AutomaticUpdateOptions AutomaticUpdates { get; init; } = new();
    /// <summary>The source selection policy.</summary>
    public AssetPolicy Assets { get; init; } = new();
    /// <summary>Whether ordinary requests include prereleases.</summary>
    public bool IncludePrerelease { get; init; }
    /// <summary>Diagnostic culture.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentUICulture;
    /// <summary>Optional externally owned HTTP transport.</summary>
    public HttpClient? HttpClient { get; init; }
    /// <summary>Host confirmation handler for reviewed installation plans.</summary>
    public Func<InstallConfirmation, CancellationToken, ValueTask<bool>>? Confirm { get; init; }
    /// <summary>Obtains the previously composed installation runtime.</summary>
    public required Func<Client, IPluginInstallationHost> Runtime { get; init; }
}

/// <summary>Composes the optional marketplace without referencing runtime implementation assemblies.</summary>
public static class MarketplaceComposition
{
    /// <summary>Adds a per-client marketplace and its transport lifetime.</summary>
    public static ClientBuilder UseMarketplace(this ClientBuilder builder, MarketplaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseModule(client =>
        {
            var service = new MarketplaceService(client, options.PluginRoot, options.RegistryPath,
                options.HttpClient ?? new HttpClient(), options.Runtime(client))
            {
                AutomaticUpdates = options.AutomaticUpdates,
                Assets = options.Assets,
                IncludePrerelease = options.IncludePrerelease,
                Culture = options.Culture,
                OwnsHttpClient = options.HttpClient is null,
            };
            if (options.Confirm is not null) service.Confirm = options.Confirm;
            return service;
        }, "marketplace");
    }
}

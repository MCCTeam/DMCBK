namespace DMCBK.PluginSdk;

/// <summary>A validated immutable package and its mutable host-selected user paths.</summary>
/// <param name="Manifest">The validated package manifest.</param>
/// <param name="PackageDirectory">The immutable package directory.</param>
/// <param name="UserDirectory">The settings and data directory outside the package.</param>
/// <param name="Enabled">Whether the package should be activated.</param>
public sealed record PluginInstallation(PluginManifest Manifest, string PackageDirectory, string UserDirectory, bool Enabled);

/// <summary>The runtime seam used by marketplace transactions without a runtime assembly dependency.</summary>
public interface IPluginInstallationHost
{
    /// <summary>Validates or compiles staged packages before any active plugin is stopped.</summary>
    ValueTask PrepareAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken);
    /// <summary>Stops the specified plugins in the supplied reverse dependency order.</summary>
    ValueTask DeactivateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
    /// <summary>Activates enabled packages in the supplied dependency-first order.</summary>
    ValueTask ActivateAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken);
}

/// <summary>Optional automatic wiring between a runtime and its installation controller.</summary>
public interface IManagedPluginHost : IPluginInstallationHost
{
    /// <summary>Reads exact packages after transaction recovery at startup.</summary>
    Func<CancellationToken, Task<IReadOnlyList<PluginInstallation>>>? InstallationSource { get; set; }
    /// <summary>Persists enabled state through installation transactions.</summary>
    Func<string, bool, CancellationToken, Task<DMCBK.Core.Plugins.PluginActionResult>>? ChangeEnabled { get; set; }
}

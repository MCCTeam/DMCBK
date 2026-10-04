using DMCBK.PluginSdk;

namespace DMCBK.Marketplace;

/// <summary>Explicit source preference and mixed-release fallback policy.</summary>
/// <param name="PreferSource">Selects source before compiled assets.</param>
/// <param name="AllowSourceFallback">Allows a mixed release to use source when compiled assets do not match.</param>
public sealed record AssetPolicy(bool PreferSource = false, bool AllowSourceFallback = false);

/// <summary>Selects exactly one archive for the running process.</summary>
public static class AssetSelector
{
    /// <summary>Chooses exact compiled, portable compiled, then permitted source assets.</summary>
    public static ReleaseAsset Select(PluginRelease release, string target, AssetPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!PluginTargets.IsSupported(target) || target == "any")
            throw new MarketplaceException("compatibility.process-target-invalid", target);
        policy ??= new();
        ReleaseAsset? source = Find("source");
        if (policy.PreferSource)
            return source ?? throw new MarketplaceException("compatibility.source-unavailable", release.Version, target);
        ReleaseAsset? compiled = Find("compiled");
        if (compiled is not null) return compiled;
        if (source is not null && (policy.AllowSourceFallback || release.Assets.All(asset => asset.Kind == "source")))
            return source;
        throw new MarketplaceException(source is null ? "compatibility.asset-unavailable" : "compatibility.source-fallback-required",
            release.Version, target);

        ReleaseAsset? Find(string kind) => release.Assets.FirstOrDefault(asset => asset.Kind == kind && asset.Target == target)
            ?? release.Assets.FirstOrDefault(asset => asset.Kind == kind && asset.Target == "any");
    }
}

/// <summary>Shared release compatibility checks used by resolution and activation.</summary>
public static class ReleaseCompatibility
{
    /// <summary>Returns all failed host gates without downloading payloads.</summary>
    public static IReadOnlyList<MarketplaceException> Check(PluginRelease release, HostInfo host)
    {
        return PluginCompatibility.Check(release.ApiVersion, release.Dmcbk, release.Umpk, release.Framework,
            release.Needs, release.Hosts, host).Select(failure => new MarketplaceException(failure.Code, failure.Values)).ToArray();
    }
}

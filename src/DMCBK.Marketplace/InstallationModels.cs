using DMCBK.PluginSdk;
using Tomlet;
using Tomlet.Attributes;

namespace DMCBK.Marketplace;

/// <summary>The exact active selections and user policy, separate from immutable manifests.</summary>
public sealed class InstallationLock
{
    /// <summary>The lock schema.</summary>
    [TomlProperty("schema-version")] public int SchemaVersion { get; set; } = 2;
    /// <summary>The monotonically increasing revision used to reject stale plans.</summary>
    [TomlProperty("revision")] public long Revision { get; set; }
    /// <summary>Selections in dependency-first activation order.</summary>
    [TomlProperty("plugins")] public List<LockedPlugin> Plugins { get; set; } = [];

    /// <summary>Parses a lock and rejects unsafe or ambiguous selections.</summary>
    public static InstallationLock Parse(string text)
    {
        if (!new TomlParser().Parse(text).Entries.ContainsKey("schema-version"))
            throw new MarketplaceException("catalogue.schema-required");
        InstallationLock value = TomletMain.To<InstallationLock>(text);
        if (value.SchemaVersion != 2 || value.Revision < 0
            || value.Plugins.Select(plugin => plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Plugins.Count)
            throw new MarketplaceException("installation.lock-invalid");
        var preceding = new Dictionary<string, LockedPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (LockedPlugin plugin in value.Plugins)
        {
            plugin.Release.Validate(plugin.Id);
            plugin.Asset.Validate();
            if (!PluginManifest.IsUsableId(plugin.Id) || plugin.Id != plugin.Id.ToLowerInvariant()
                || !PluginManifest.IsUsableId(plugin.MarketplaceId)
                || plugin.UpdatePolicy is not ("off" or "inherit" or "manual" or "automatic")
                || !plugin.Release.Assets.Any(asset => SameAsset(asset, plugin.Asset)))
                throw new MarketplaceException("installation.lock-invalid", plugin.Id);
            foreach ((string id, string range) in plugin.Release.Requires)
                if (!preceding.TryGetValue(id, out LockedPlugin? provider)
                    || !SemVerRange.Parse(range).Satisfies(SemVer.Parse(provider.Release.Version), includePrerelease: true)
                    || (plugin.Enabled && !provider.Enabled))
                    throw new MarketplaceException("installation.lock-dependency-invalid", plugin.Id, id);
            preceding.Add(plugin.Id, plugin);
        }
        return value;
    }

    internal static bool SameAsset(ReleaseAsset left, ReleaseAsset right) => left.Kind == right.Kind
        && left.Target == right.Target && left.Url == right.Url && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One exact package selection with settings that survive updates.</summary>
public sealed class LockedPlugin
{
    /// <summary>The plugin identity.</summary>
    [TomlProperty("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The explicit publisher binding.</summary>
    [TomlProperty("marketplace")] public string MarketplaceId { get; set; } = string.Empty;
    /// <summary>The exact compatible release metadata.</summary>
    [TomlProperty("release")] public PluginRelease Release { get; set; } = new();
    /// <summary>The single selected asset.</summary>
    [TomlProperty("asset")] public ReleaseAsset Asset { get; set; } = new();
    /// <summary>The user's enabled state.</summary>
    [TomlProperty("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Whether updates must preserve this version.</summary>
    [TomlProperty("pinned")] public bool Pinned { get; set; }
    /// <summary>Inherited, off, manual or automatic update policy.</summary>
    [TomlProperty("update-policy")] public string UpdatePolicy { get; set; } = "inherit";
    /// <summary>The explicit direct source, when imported outside a marketplace.</summary>
    [TomlProperty("source")] public string? SourceLocation { get; set; }
    /// <summary>The immutable Git commit or direct payload digest.</summary>
    [TomlProperty("source-revision")] public string? SourceRevision { get; set; }
    /// <summary>The package-relative immutable version location.</summary>
    [TomlNonSerialized]
    public string PackagePath => $"versions/{Id}/{Release.Version}/{Asset.Target}/{Asset.Sha256.ToLowerInvariant()}";
}

/// <summary>A reviewable change, with exact selection information.</summary>
/// <param name="Id">The plugin identity.</param>
/// <param name="PreviousVersion">The previous version, if installed.</param>
/// <param name="Version">The selected version.</param>
/// <param name="Kind">Source or compiled.</param>
/// <param name="Target">The selected process target.</param>
/// <param name="Sha256">The immutable asset digest.</param>
/// <param name="Reason">The resolver's explanation identifier.</param>
public sealed record PluginChange(string Id, string? PreviousVersion, string Version, string Kind, string Target, string Sha256, string Reason);

/// <summary>An immutable graph proposal tied to one installation root and lock snapshot.</summary>
public sealed class InstallPlan
{
    internal InstallPlan(string owner, string previous, string next, IEnumerable<PluginChange> changes)
    { Owner = owner; Previous = previous; Next = next; Changes = Array.AsReadOnly(changes.ToArray()); }
    internal string Owner { get; }
    internal string Previous { get; }
    internal string Next { get; }
    /// <summary>Every changed package, including required dependencies.</summary>
    public IReadOnlyList<PluginChange> Changes { get; }
}

/// <summary>The committed transaction identity and resulting selection.</summary>
/// <param name="TransactionId">The local history identity used for cached rollback.</param>
/// <param name="Selection">The committed exact lock.</param>
public sealed record PluginOperationResult(string TransactionId, InstallationLock Selection);

/// <summary>Archive limits enforced before package activation.</summary>
/// <param name="DownloadBytes">Maximum compressed asset size.</param>
/// <param name="ExpandedBytes">Maximum total uncompressed size.</param>
/// <param name="FileCount">Maximum archive entry count.</param>
public sealed record PackageLimits(long DownloadBytes = 128 * 1024 * 1024,
    long ExpandedBytes = 512 * 1024 * 1024, int FileCount = 10000);

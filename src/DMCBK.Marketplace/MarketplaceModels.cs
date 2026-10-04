using DMCBK.PluginSdk;
using Tomlet;
using Tomlet.Attributes;

namespace DMCBK.Marketplace;

/// <summary>The schema-v2 identity index for one marketplace.</summary>
public sealed class MarketplaceIndex
{
    /// <summary>The index schema.</summary>
    [TomlProperty("schema-version")] public int SchemaVersion { get; set; }
    /// <summary>The marketplace identity.</summary>
    [TomlProperty("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The displayed marketplace name.</summary>
    [TomlProperty("name")] public string Name { get; set; } = string.Empty;
    /// <summary>The plugin identities and catalogue locations.</summary>
    [TomlProperty("plugins")] public List<PluginIdentity> Plugins { get; set; } = [];

    /// <summary>Parses an index and rejects legacy formats or ambiguous identities.</summary>
    public static MarketplaceIndex Parse(string text)
    {
        MarketplaceIndex index = TomletMain.To<MarketplaceIndex>(text);
        if (index.SchemaVersion != 2 || !PluginManifest.IsUsableId(index.Id) || string.IsNullOrWhiteSpace(index.Name)
            || index.Plugins.Any(plugin => !PluginManifest.IsUsableId(plugin.Id) || !PluginManifest.IsPackagePath(plugin.Releases))
            || index.Plugins.Select(plugin => plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Plugins.Count)
            throw new MarketplaceException("catalogue.index-invalid");
        return index;
    }
}

/// <summary>One searchable identity, independent of its historical releases.</summary>
public sealed class PluginIdentity
{
    /// <summary>The stable plugin identifier.</summary>
    [TomlProperty("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The plugin description.</summary>
    [TomlProperty("description")] public string Description { get; set; } = string.Empty;
    /// <summary>The search tags.</summary>
    [TomlProperty("tags")] public List<string> Tags { get; set; } = [];
    /// <summary>The catalogue path relative to the index.</summary>
    [TomlProperty("releases")] public string Releases { get; set; } = string.Empty;
}

/// <summary>All immutable published releases of one plugin.</summary>
public sealed class ReleaseCatalogue
{
    /// <summary>The catalogue schema.</summary>
    [TomlProperty("schema-version")] public int SchemaVersion { get; set; }
    /// <summary>The plugin identity.</summary>
    [TomlProperty("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The release history.</summary>
    [TomlProperty("releases")] public List<PluginRelease> Releases { get; set; } = [];

    /// <summary>Parses a catalogue and validates every compatibility constraint and asset.</summary>
    public static ReleaseCatalogue Parse(string text)
    {
        ReleaseCatalogue catalogue = TomletMain.To<ReleaseCatalogue>(text);
        if (catalogue.SchemaVersion != 2 || !PluginManifest.IsUsableId(catalogue.Id)
            || catalogue.Releases.Select(release => SemVer.Parse(release.Version)).Distinct().Count() != catalogue.Releases.Count)
            throw new MarketplaceException("catalogue.release-invalid", catalogue.Id);
        foreach (PluginRelease release in catalogue.Releases)
            release.Validate(catalogue.Id);
        return catalogue;
    }
}

/// <summary>Compatibility and dependency metadata shared by all assets of one version.</summary>
public sealed class PluginRelease
{
    /// <summary>The exact release version.</summary>
    [TomlProperty("version")] public string Version { get; set; } = string.Empty;
    /// <summary>The required plugin API version.</summary>
    [TomlProperty("api-version")] public string ApiVersion { get; set; } = string.Empty;
    /// <summary>The supported DMCBK range.</summary>
    [TomlProperty("dmcbk")] public string Dmcbk { get; set; } = string.Empty;
    /// <summary>The supported UMPK range.</summary>
    [TomlProperty("umpk")] public string Umpk { get; set; } = string.Empty;
    /// <summary>The required target framework.</summary>
    [TomlProperty("framework")] public string Framework { get; set; } = string.Empty;
    /// <summary>The required host capabilities.</summary>
    [TomlProperty("needs")] public List<string> Needs { get; set; } = [];
    /// <summary>Whether normal installation and updates exclude this release.</summary>
    [TomlProperty("yanked")] public bool Yanked { get; set; }
    /// <summary>The independently downloadable platform assets.</summary>
    [TomlProperty("assets")] public List<ReleaseAsset> Assets { get; set; } = [];
    /// <summary>The required plugin version ranges.</summary>
    [TomlProperty("requires")] public Dictionary<string, string> Requires { get; set; } = [];
    /// <summary>The optional plugin version ranges.</summary>
    [TomlProperty("optional")] public Dictionary<string, string> Optional { get; set; } = [];
    /// <summary>The allowed application identities and version ranges.</summary>
    [TomlProperty("hosts")] public Dictionary<string, string> Hosts { get; set; } = [];

    /// <summary>Validates metadata before it enters resolution.</summary>
    public void Validate(string id)
    {
        if (!SemVer.TryParse(Version, out SemVer version) || version.ToString() != Version
            || !PluginApiVersion.TryParse(ApiVersion, out int major, out int minor) || major < 0 || minor < 0
            || Framework != "net10.0" || Needs.Any(string.IsNullOrWhiteSpace)
            || !SemVerRange.TryParse(Dmcbk, out _) || !SemVerRange.TryParse(Umpk, out _)
            || Requires.Concat(Optional).Concat(Hosts).Any(pair => !PluginManifest.IsUsableId(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value) || !SemVerRange.TryParse(pair.Value, out _))
            || Requires.Keys.Any(dependency => string.Equals(dependency, id, StringComparison.OrdinalIgnoreCase))
            || Assets.Count == 0 || Assets.Select(asset => (asset.Kind, asset.Target)).Distinct().Count() != Assets.Count)
            throw new MarketplaceException("catalogue.release-invalid", id, Version);
        foreach (ReleaseAsset asset in Assets) asset.Validate();
    }
}

/// <summary>One immutable release payload.</summary>
public sealed class ReleaseAsset
{
    /// <summary>Source or compiled.</summary>
    [TomlProperty("kind")] public string Kind { get; set; } = string.Empty;
    /// <summary>A supported process target or any.</summary>
    [TomlProperty("target")] public string Target { get; set; } = string.Empty;
    /// <summary>The asset location.</summary>
    [TomlProperty("url")] public string Url { get; set; } = string.Empty;
    /// <summary>The archive's SHA-256 digest.</summary>
    [TomlProperty("sha256")] public string Sha256 { get; set; } = string.Empty;

    /// <summary>Rejects unsupported kinds, targets, URLs or digests.</summary>
    public void Validate()
    {
        if (Kind is not ("compiled" or "source") || !PluginTargets.IsSupported(Target)
            || !Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")
            || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit))
            throw new MarketplaceException("catalogue.asset-invalid", Kind, Target);
    }
}

/// <summary>A structured marketplace failure, rendered by the host's localization layer.</summary>
public sealed class MarketplaceException : Exception
{
    /// <summary>Creates a failure with a stable diagnostic identifier and context values.</summary>
    public MarketplaceException(string code, params string[] values) : base(code)
    { Code = code; Values = values; }
    /// <summary>The diagnostic identifier.</summary>
    public string Code { get; }
    /// <summary>The diagnostic context.</summary>
    public IReadOnlyList<string> Values { get; }
}

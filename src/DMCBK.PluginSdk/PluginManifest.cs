using Tomlet;
using Tomlet.Attributes;

namespace DMCBK.PluginSdk;

/// <summary>
/// The parsed <c>plugin.toml</c> manifest (id, version, entry, api-version, optional deps, enabled).
/// The entry names either a compiled <c>.dll</c> or a single <c>.cs</c> file inside the plugin folder.
/// <c>deps</c> lists extra assembly paths (relative to the folder) the <c>.cs</c> compile references, which covers what the <c>//dll</c> directive covered.
/// <para>
/// Two axes are easy to confuse.
/// <c>deps</c> are assemblies shipped beside the plugin, while <c>[requires]</c> and <c>[optional]</c> name other plugins by id.
/// <c>[exports]</c> is what lets a required plugin's assemblies be linked against at all.
/// </para>
/// </summary>
public sealed class PluginManifest
{
    /// <summary>The supported package schema.</summary>
    [TomlProperty("schema-version")]
    public int SchemaVersion { get; set; }

    /// <summary>The asset kind: source or compiled.</summary>
    [TomlProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The declared process target or any.</summary>
    [TomlProperty("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>The required target framework.</summary>
    [TomlProperty("framework")]
    public string Framework { get; set; } = string.Empty;

    /// <summary>The host capabilities required for activation.</summary>
    [TomlProperty("needs")]
    public List<string> Needs { get; set; } = [];

    /// <summary>Allowed applications and their version constraints. Empty admits all hosts.</summary>
    [TomlProperty("hosts")]
    public Dictionary<string, string> Hosts { get; set; } = [];

    /// <summary>The plugin id (stable identifier). Required.</summary>
    [TomlProperty("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The plugin version string (informational).</summary>
    [TomlProperty("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>The entry file name inside the plugin folder (<c>Name.dll</c> or <c>Name.cs</c>). Required.</summary>
    [TomlProperty("entry")]
    public string Entry { get; set; } = string.Empty;

    /// <summary>
    /// The SDK <c>major.minor</c> the plugin targets (accepts <c>"2"</c>, read as <c>2.0</c>, or <c>"2.1"</c>).
    /// The host loads the plugin when the major matches and its own minor is at least the declared one.
    /// </summary>
    [TomlProperty("api-version")]
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    /// An optional npm-syntax version range the running MCC must satisfy, for example <c>"&gt;=2.0.0"</c> or <c>"^2.1"</c>.
    /// Empty means any.
    /// Compared against <c>DMCBK.Core.DmcbkVersion.Current</c>.
    /// </summary>
    [TomlProperty("dmcbk")]
    public string Dmcbk { get; set; } = string.Empty;

    /// <summary>
    /// An optional npm-syntax version range the running UMPK engine must satisfy, for example <c>"&gt;=0.9"</c>.
    /// Empty means any.
    /// Compared against <see cref="UmpkVersionProbe.Current"/>.
    /// </summary>
    [TomlProperty("umpk")]
    public string Umpk { get; set; } = string.Empty;

    /// <summary>Extra assembly paths (relative to the folder) the <c>.cs</c> compile references.</summary>
    [TomlProperty("deps")]
    public List<string> Deps { get; set; } = [];

    /// <summary>
    /// Manual topics this plugin contributes, as bare topic ids.
    /// A page lives at <c>man/&lt;lang&gt;/&lt;topic&gt;.md</c> inside the plugin folder, with <c>man/&lt;topic&gt;.md</c> accepted as the English-only form.
    /// They join <c>/man</c> while the plugin is loaded and leave when it unloads.
    /// </summary>
    [TomlProperty("man")]
    public List<string> Man { get; set; } = [];

    /// <summary>
    /// The optional <c>[meta]</c> table: catalogue-facing description, homepage, tags and the capabilities the plugin uses.
    /// Informational to the loader; the marketplace reads it so a search result can be rendered without fetching the plugin.
    /// </summary>
    [TomlProperty("meta")]
    public PluginMeta Meta { get; set; } = new();

    /// <summary>
    /// The optional <c>[requires]</c> table: other plugin ids (not assemblies, which are <see cref="Deps"/>) mapped to the npm-syntax range this plugin needs of them.
    /// The host loads them first and refuses this plugin when one is missing, disabled, failed or out of range.
    /// </summary>
    [TomlProperty("requires")]
    public Dictionary<string, string> Requires { get; set; } = [];

    /// <summary>
    /// The optional <c>[optional]</c> table, same shape as <see cref="Requires"/> but ordering only: when the named plugin is present it loads first, and when it is absent or out of range nothing fails.
    /// </summary>
    [TomlProperty("optional")]
    public Dictionary<string, string> Optional { get; set; } = [];

    /// <summary>
    /// The optional <c>[exports]</c> table: the assemblies this plugin lets its dependents compile and link against.
    /// See <see cref="PluginExports.Assemblies"/>.
    /// </summary>
    [TomlProperty("exports")]
    public PluginExports Exports { get; set; } = new();

    /// <summary>
    /// The optional <c>[services]</c> table: what the plugin is useful for.
    /// Informational, read by <c>plugins list</c>, <c>plugins info</c> and the idle banner.
    /// </summary>
    [TomlProperty("services")]
    public PluginServicesDeclaration Services { get; set; } = new();

    /// <summary>The fixed manifest file name.</summary>
    public const string FileName = "plugin.toml";

    /// <summary>The parsed <c>mcc</c> range, or <see cref="SemVerRange.Any"/> when the key is absent.</summary>
    [TomlNonSerialized]
    public SemVerRange DmcbkRange { get; private set; } = SemVerRange.Any;

    /// <summary>The parsed <c>umpk</c> range, or <see cref="SemVerRange.Any"/> when the key is absent.</summary>
    [TomlNonSerialized]
    public SemVerRange UmpkRange { get; private set; } = SemVerRange.Any;

    /// <summary>The parsed <c>[requires]</c> ranges, keyed by plugin id (case-insensitive).</summary>
    [TomlNonSerialized]
    public IReadOnlyDictionary<string, SemVerRange> RequiredRanges { get; private set; }
        = new Dictionary<string, SemVerRange>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parsed <c>[optional]</c> ranges, keyed by plugin id (case-insensitive).</summary>
    [TomlNonSerialized]
    public IReadOnlyDictionary<string, SemVerRange> OptionalRanges { get; private set; }
        = new Dictionary<string, SemVerRange>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the parsed ranges from a freshly read manifest, so a reload that re-reads <c>plugin.toml</c> into the record's existing instance also picks up a changed <c>mcc</c>, <c>umpk</c> or dependency gate.
    /// </summary>
    internal void AdoptRanges(PluginManifest parsed)
    {
        DmcbkRange = parsed.DmcbkRange;
        UmpkRange = parsed.UmpkRange;
        RequiredRanges = parsed.RequiredRanges;
        OptionalRanges = parsed.OptionalRanges;
    }

    /// <summary>Whether the entry names a single-file <c>.cs</c> source (else a compiled <c>.dll</c>).</summary>
    [TomlNonSerialized]
    public bool IsSourceEntry => Entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the manifest at <paramref name="tomlText"/>.
    /// Returns false with a localized <paramref name="error"/> when the TOML is malformed or a required field is missing.
    /// </summary>
    public static bool TryParse(string tomlText, out PluginManifest manifest, out string? error)
    {
        manifest = new PluginManifest();
        error = null;
        try
        {
            manifest = TomletMain.To<PluginManifest>(tomlText);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = PluginStrings.ManifestInvalid(ex.Message);
            return false;
        }

        if (manifest.SchemaVersion != 2
            || !SemVer.TryParse(manifest.Version, out SemVer version)
            || version.ToString() != manifest.Version
            || manifest.Kind is not ("compiled" or "source")
            || !PluginTargets.IsSupported(manifest.Target)
            || manifest.Framework != "net10.0"
            || !PluginApiVersion.TryParse(manifest.ApiVersion, out _, out _)
            || string.IsNullOrWhiteSpace(manifest.Dmcbk) || string.IsNullOrWhiteSpace(manifest.Umpk)
            || manifest.Needs.Any(string.IsNullOrWhiteSpace)
            || manifest.Hosts.Any(pair => !IsUsableId(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || !SemVerRange.TryParse(pair.Value, out _)))
        {
            error = PluginStrings.ManifestInvalid("schema-version, version, kind, target, framework, api-version, needs or hosts");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            error = PluginStrings.ManifestInvalid("missing 'id'");
            return false;
        }

        // The id names a folder under plugins/ and the parking folder an uninstall writes to, so a value holding a separator or a leading dot would reach outside either one.
        if (!IsUsableId(manifest.Id))
        {
            error = PluginStrings.ManifestIdInvalid(manifest.Id);
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            error = PluginStrings.ManifestInvalid("missing 'entry'");
            return false;
        }

        if (!IsPackagePath(manifest.Entry)
            || (manifest.Kind == "source") != manifest.IsSourceEntry
            || (manifest.Kind == "compiled" && !manifest.Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            || manifest.Deps.Any(path => !IsPackagePath(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            || manifest.Exports.Assemblies.Any(path => path != PluginExports.EntryToken && !IsPackagePath(path)))
        {
            error = PluginStrings.ManifestInvalid("entry, deps or exports");
            return false;
        }

        // An unparsable range is a manifest error, not a silent "any": a typo in a gate that is supposed to refuse the load must not turn the gate off.
        if (!SemVerRange.TryParse(manifest.Dmcbk, out SemVerRange? mcc) || mcc is null)
        {
            error = PluginStrings.ManifestInvalid(PluginStrings.ManifestRangeInvalid("dmcbk", manifest.Dmcbk));
            return false;
        }

        if (!SemVerRange.TryParse(manifest.Umpk, out SemVerRange? umpk) || umpk is null)
        {
            error = PluginStrings.ManifestInvalid(PluginStrings.ManifestRangeInvalid("umpk", manifest.Umpk));
            return false;
        }

        if (!TryParseDependencyTable(manifest.Requires, "requires", out Dictionary<string, SemVerRange>? requires, out error)
            || !TryParseDependencyTable(manifest.Optional, "optional", out Dictionary<string, SemVerRange>? optional, out error))
            return false;

        manifest.DmcbkRange = mcc;
        manifest.UmpkRange = umpk;
        manifest.RequiredRanges = requires;
        manifest.OptionalRanges = optional;
        return true;
    }

    /// <summary>
    /// Whether an id is safe to use as one path segment: letters, digits, dot, dash and underscore, and not starting with a dot, which also rules out <c>.</c> and <c>..</c>.
    /// </summary>
    internal static bool IsUsableId(string? id)
        => !string.IsNullOrWhiteSpace(id)
            && !id.StartsWith('.')
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Parses one dependency table into ranges.
    /// An empty range is a manifest error, not a silent "any": <c>map = ""</c> would claim something the author did not write.
    /// <c>"*"</c> is how you say any.
    /// </summary>
    /// <summary>Checks that a package-relative path cannot leave its archive.</summary>
    public static bool IsPackagePath(string path)
        => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            && !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0')
            && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static bool TryParseDependencyTable(
        Dictionary<string, string> table,
        string tableName,
        out Dictionary<string, SemVerRange> ranges,
        out string? error)
    {
        ranges = new Dictionary<string, SemVerRange>(StringComparer.OrdinalIgnoreCase);
        error = null;
        foreach ((string id, string text) in table)
        {
            if (!IsUsableId(id))
            {
                error = PluginStrings.ManifestInvalid(PluginStrings.ManifestDependencyIdEmpty(tableName));
                return false;
            }

            if (string.IsNullOrWhiteSpace(text)
                || !SemVerRange.TryParse(text, out SemVerRange? range)
                || range is null)
            {
                error = PluginStrings.ManifestInvalid(
                    PluginStrings.ManifestRangeInvalid($"{tableName}.{id}", text));
                return false;
            }

            ranges[id] = range;
        }

        return true;
    }
}

/// <summary>
/// The <c>[exports]</c> table of <c>plugin.toml</c>: which of this plugin's assemblies its dependents may link against.
/// Without it a plugin's types stay private to its own collectible load context, and two plugins can exchange nothing but BCL and SDK types.
/// </summary>
public sealed class PluginExports
{
    /// <summary>
    /// Assembly file names shipped in the plugin folder (<c>"Map.Api.dll"</c>), or the literal <c>"entry"</c> for the plugin's own entry assembly, which is all a single-file <c>.cs</c> plugin has to export.
    /// A dependent's load context resolves these names into this plugin's context, and the Roslyn compiler references them when it compiles the dependent.
    /// </summary>
    [TomlProperty("assemblies")]
    public List<string> Assemblies { get; set; } = [];

    /// <summary>The token in <see cref="Assemblies"/> that means "the plugin's own entry assembly".</summary>
    public const string EntryToken = "entry";
}

/// <summary>
/// The <c>[services]</c> table of <c>plugin.toml</c>: what the plugin claims it can do.
/// Nothing here is enforced or gated; it is what the client shows a user who is deciding what to run.
/// </summary>
public sealed class PluginServicesDeclaration
{
    /// <summary>
    /// True when the plugin does useful work with no server connected.
    /// The idle banner names the running plugins that declare it, so a client sitting at the prompt says what is still working.
    /// </summary>
    [TomlProperty("offline")]
    public bool Offline { get; set; }
}

/// <summary>
/// The <c>[meta]</c> table of <c>plugin.toml</c>: what a catalogue entry shows about a plugin before anyone downloads it.
/// Nothing here changes whether the plugin loads.
/// </summary>
public sealed class PluginMeta
{
    /// <summary>One line describing what the plugin does.</summary>
    [TomlProperty("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The project or documentation URL.</summary>
    [TomlProperty("homepage")]
    public string Homepage { get; set; } = string.Empty;

    /// <summary>Free-form tags a marketplace search matches against.</summary>
    [TomlProperty("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// What the plugin uses, as free-form capability words (<c>"network"</c>, <c>"files"</c>, <c>"movement"</c>).
    /// Disclosure for the install confirmation, never enforced: plugins run in-process with no sandbox.
    /// </summary>
    [TomlProperty("uses")]
    public List<string> Uses { get; set; } = [];
}

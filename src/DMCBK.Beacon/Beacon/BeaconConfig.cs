using DMCBK.Core.Configuration;
using Tomlet;
using Tomlet.Models;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Mutable, TOML-facing model of <c>beacon.toml</c>.
/// Deserialized by Tomlet, then folded into the immutable <see cref="BeaconConfig"/> snapshot by <see cref="BeaconConfigLoader"/>.
/// Mirrors the core loader's conventions: PascalCase keys like the core files, warn-and-ignore unknown keys, validate-and-derive after.
/// </summary>
internal sealed class BeaconTomlFile
{
    /// <summary>The network allowlist table.</summary>
    public NetTable Net { get; set; } = new();

    /// <summary>The <c>[net]</c> table: bare HTTPS host names, no scheme/port/path.</summary>
    internal sealed class NetTable
    {
        /// <summary>Bare HTTPS host names Beacon scripts may fetch; empty means no network.</summary>
        public List<string> AllowedHosts { get; set; } = [];
    }
}

/// <summary>The immutable network snapshot: which HTTPS hosts scripts may fetch.</summary>
public sealed record BeaconNetConfig(IReadOnlyList<string> AllowedHosts);

/// <summary>The immutable Beacon configuration snapshot (one per load; reloads replace it).</summary>
public sealed record BeaconConfig(BeaconNetConfig Net)
{
    /// <summary>The default: no network access.</summary>
    public static BeaconConfig Default { get; } = new(new BeaconNetConfig([]));
}

/// <summary>The result of a Beacon config load: snapshot, recoverable warnings, generation flag.</summary>
public sealed record BeaconConfigLoadResult(
    BeaconConfig Config,
    IReadOnlyList<ConfigurationWarning> Warnings,
    bool Generated);

/// <summary>
/// Loads <c>configurations/beacon.toml</c> with the core loader's pipeline semantics: deserialize (warn on unknown keys) then validate/derive into an immutable <see cref="BeaconConfig"/> snapshot.
/// First-run generation writes the file with commented defaults; a plain load of an existing file writes nothing; there is no implicit write-back (explicit-write-only).
/// Instance-scoped.
/// Scripts never reach secrets through this loader: it reads exactly one non-secret file.
/// </summary>
public sealed class BeaconConfigLoader
{
    private const string DefaultFileText =
        "# Beacon script configuration.\n"
        + "# Generated with commented defaults on first run; a plain load never rewrites this file.\n"
        + "# Unknown keys are warned about and ignored. Scripts never read accounts.toml or token\n"
        + "# caches through this file: their persisted state lives only under configurations/beacon/.\n"
        + "\n"
        + "[Net]\n"
        + "# HTTPS hosts Beacon scripts may fetch. Empty means no network access.\n"
        + "# Each entry is a bare host name: no scheme, no port, no path (example: \"example.com\").\n"
        + "AllowedHosts = []\n";

    private readonly string _folder;

    /// <summary>Builds a loader for a configurations folder.</summary>
    public BeaconConfigLoader(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        _folder = Path.GetFullPath(configurationsFolder);
    }

    /// <summary>The resolved configurations folder.</summary>
    public string Folder => _folder;

    /// <summary>The most recently produced snapshot, or null before the first load.</summary>
    public BeaconConfig? Current { get; private set; }

    /// <summary>
    /// Loads the configuration.
    /// When <paramref name="generateMissing"/> is true (the default), a missing <c>beacon.toml</c> is generated with commented defaults before reading.
    /// Existing files are never rewritten by a load.
    /// </summary>
    public BeaconConfigLoadResult Load(bool generateMissing = true)
    {
        var warnings = new List<ConfigurationWarning>();
        bool generated = generateMissing && GenerateMissingFiles();

        string path = ConfigurationPaths.BeaconFile(_folder);
        BeaconTomlFile file = ReadFile(path, warnings);
        List<string> hosts = ValidateHosts(file.Net?.AllowedHosts ?? [], warnings);

        var config = new BeaconConfig(new BeaconNetConfig(hosts));
        Current = config;
        return new BeaconConfigLoadResult(config, warnings, generated);
    }

    /// <summary>Generates a missing <c>beacon.toml</c> with commented defaults. Returns true if written.</summary>
    public bool GenerateMissingFiles()
    {
        Directory.CreateDirectory(_folder);
        string path = ConfigurationPaths.BeaconFile(_folder);
        if (File.Exists(path))
            return false;

        File.WriteAllText(path, DefaultFileText);
        return true;
    }

    /// <summary>
    /// Validates raw allowlist entries into bare lowercase host names, warning-and-dropping anything with a scheme, port, path, whitespace, or illegal characters.
    /// Pure and independently testable.
    /// </summary>
    public static List<string> ValidateHosts(IEnumerable<string> candidates, List<ConfigurationWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(warnings);
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? raw in candidates)
        {
            string entry = (raw ?? string.Empty).Trim();
            if (entry.Length == 0)
            {
                warnings.Add(new ConfigurationWarning(
                    "Beacon net allowlist entry is empty; ignored. Only bare HTTPS host names are allowed."));
                continue;
            }

            string lowered = entry.ToLowerInvariant();
            if (!IsBareHost(lowered))
            {
                warnings.Add(new ConfigurationWarning(
                    $"Beacon net allowlist entry '{entry}' is not a bare HTTPS host name "
                    + "(no scheme, port, path, or spaces); ignored."));
                continue;
            }

            if (seen.Add(lowered))
                kept.Add(lowered);
        }

        return kept;
    }

    private static bool IsBareHost(string host)
    {
        if (host.Length == 0 || host.Length > 253 || host.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (char c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
                return false;
        }

        if (!char.IsAsciiLetterOrDigit(host[0]) || !char.IsAsciiLetterOrDigit(host[^1]))
            return false;

        return host.Split('.').All(label => label.Length is > 0 and <= 63);
    }

    private static BeaconTomlFile ReadFile(string path, List<ConfigurationWarning> warnings)
    {
        if (!File.Exists(path))
            return new BeaconTomlFile();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new ConfigurationWarning($"Could not read '{path}': {ex.Message}. Using defaults."));
            return new BeaconTomlFile();
        }

        try
        {
            TomlDocument document = new TomlParser().Parse(text);
            TomlUnknownKeyScanner.Scan(document, typeof(BeaconTomlFile), string.Empty, warnings);
            return TomletMain.To<BeaconTomlFile>(document);
        }
        catch (Exception ex)
        {
            warnings.Add(new ConfigurationWarning($"Could not parse '{path}': {ex.Message}. Using defaults."));
            return new BeaconTomlFile();
        }
    }
}

/// <summary>One discovered script: its id (file name without extension) and full path.</summary>
public sealed record BeaconScriptFile(string ScriptId, string Path);

/// <summary>
/// Discovers Beacon scripts: the <c>scripts/</c> folder beside the configurations folder, one file one script (own globals per script id).
/// Only top-level <c>*.bcn</c> files count; anything else (notes, subfolders) is ignored.
/// A missing folder discovers as empty, never throws.
/// </summary>
public static class BeaconScriptDiscovery
{
    /// <summary>The only file extension discovered as a script.</summary>
    public const string ScriptExtension = ".bcn";

    /// <summary>Lists scripts sorted by id.</summary>
    public static IReadOnlyList<BeaconScriptFile> Discover(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        string dir = ConfigurationPaths.ScriptsDir(configurationsFolder);
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "*" + ScriptExtension, SearchOption.TopDirectoryOnly)
            .Select(path => new BeaconScriptFile(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path)))
            .Where(script => script.ScriptId.Length > 0)
            .OrderBy(script => script.ScriptId, StringComparer.Ordinal)
            .ToList();
    }
}

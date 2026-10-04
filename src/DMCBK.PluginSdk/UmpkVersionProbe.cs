using System.Reflection;
using Microsoft.Extensions.Logging;

namespace DMCBK.PluginSdk;

/// <summary>
/// Reads the engine version off the assembly that carries <see cref="Umpk.Client.UmpkClient"/>, so a plugin's <c>umpk</c> manifest range has something to compare against.
/// UMPK is referenced from an on-disk checkout rather than a package, and an unversioned build stamps only the SDK's own <c>1.0.0</c>, so the probe never fails the load: an informational version it cannot parse degrades to <c>0.0.0</c> and logs once, and a <c>0.0.0</c> engine satisfies no range but blocks nothing that declares none.
/// </summary>
public static class UmpkVersionProbe
{
    static UmpkVersionProbe()
    {
        Assembly engine = typeof(Umpk.Client.UmpkClient).Assembly;
        Informational = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? engine.GetName().Version?.ToString()
            ?? string.Empty;

        if (SemVer.TryParse(StripMetadata(Informational), out SemVer parsed))
        {
            Version = parsed;
            Resolved = true;
        }
        else
        {
            Version = new SemVer(0, 0, 0);
            Resolved = false;
        }
    }

    /// <summary>The parsed engine version, or <c>0.0.0</c> when the assembly carries none this can read.</summary>
    public static SemVer Version { get; }

    /// <summary>The engine version as a bare <c>major.minor.patch</c> string.</summary>
    public static string Current => Version.ToString();

    /// <summary>The engine's full informational version, as stamped, including any build metadata.</summary>
    public static string Informational { get; }

    /// <summary>False when the informational version could not be parsed and <c>0.0.0</c> was substituted.</summary>
    public static bool Resolved { get; }

    /// <summary>The major component of <see cref="Version"/>.</summary>
    public static int Major => Version.Major;

    /// <summary>The minor component of <see cref="Version"/>.</summary>
    public static int Minor => Version.Minor;

    /// <summary>The patch component of <see cref="Version"/>.</summary>
    public static int Patch => Version.Patch;

    /// <summary>
    /// Logs the fallback once per host, at construction, so a client running an unversioned engine says so before a plugin's <c>umpk</c> range is refused against <c>0.0.0</c>.
    /// </summary>
    internal static void LogIfUnresolved(ILogger logger)
    {
        if (Resolved)
            return;

        logger.LogWarning("{Message}", PluginStrings.UmpkVersionUnknown(Informational));
    }

    /// <summary>
    /// Trims a build-metadata suffix the parser would reject.
    /// The .NET SDK appends <c>+&lt;sha&gt;</c>, which is valid semver, but a four-part <c>AssemblyName.Version</c> fallback (<c>1.0.0.0</c>) is not.
    /// </summary>
    private static string StripMetadata(string informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return string.Empty;

        string value = informational.Trim();
        int plus = value.IndexOf('+');
        if (plus >= 0)
            value = value[..plus];

        // "1.0.0.0" from AssemblyName.Version: keep the first three components, drop the revision.
        int dash = value.IndexOf('-');
        string core = dash >= 0 ? value[..dash] : value;
        string[] parts = core.Split('.');
        if (parts.Length == 4)
        {
            core = string.Join('.', parts[0], parts[1], parts[2]);
            value = dash >= 0 ? core + value[dash..] : core;
        }

        return value;
    }
}

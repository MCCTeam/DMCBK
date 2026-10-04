using System.Globalization;
using System.Reflection;

namespace DMCBK.Core;

/// <summary>
/// The MCC application version, read once from this assembly's <see cref="AssemblyInformationalVersionAttribute"/>.
/// MSBuild stamps that attribute from the <c>Version</c> property in <c>Mcc/Directory.Build.props</c> and appends <c>+&lt;sha&gt;</c> for a build out of a git checkout, so <see cref="Informational"/> reads like <c>2.0.0+e893691e</c> while <see cref="Current"/> is the bare <c>2.0.0</c> that version ranges compare against.
/// <para>
/// This is one of the three numbers a plugin can depend on (SDK <c>api-version</c>, <c>mcc</c>, <c>umpk</c>).
/// Build metadata and prerelease tags are never part of a range comparison, so a local <c>2.0.0-dev+sha</c> build still satisfies <c>&gt;=2.0.0</c>.
/// </para>
/// </summary>
public static class DmcbkVersion
{
    static DmcbkVersion()
    {
        Informational = typeof(DmcbkVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DmcbkVersion).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        Current = Informational.Split('+')[0];
        (Major, Minor, Patch) = ParseParts(CoreOf(Informational));
    }

    /// <summary>The version as a bare <c>major.minor.patch</c> string, with no prerelease or build metadata.</summary>
    public static string Current { get; }

    /// <summary>The full informational version, including any prerelease tag and build metadata.</summary>
    public static string Informational { get; }

    /// <summary>The major component of <see cref="Current"/>.</summary>
    public static int Major { get; }

    /// <summary>The minor component of <see cref="Current"/>.</summary>
    public static int Minor { get; }

    /// <summary>The patch component of <see cref="Current"/>.</summary>
    public static int Patch { get; }

    /// <summary>
    /// Strips the prerelease tag and the build metadata from an informational version, leaving the <c>major.minor.patch</c> core.
    /// Anything unparsable collapses to <c>0.0.0</c>.
    /// </summary>
    internal static string CoreOf(string informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";

        string value = informational.Trim();
        int cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0)
            value = value[..cut];

        (int major, int minor, int patch) = ParseParts(value);
        return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}");
    }

    private static (int Major, int Minor, int Patch) ParseParts(string core)
    {
        string[] parts = core.Split('.');
        int major = Part(parts, 0);
        int minor = Part(parts, 1);
        int patch = Part(parts, 2);
        return (major, minor, patch);

        static int Part(string[] parts, int index)
            => index < parts.Length
                && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
    }
}

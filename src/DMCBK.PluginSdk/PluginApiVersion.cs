using System.Globalization;

namespace DMCBK.PluginSdk;

/// <summary>
/// The plugin SDK contract version.
/// A plugin declares the SDK <c>major.minor</c> it targets in its <c>plugin.toml</c> (<c>api-version</c>), and the host loads it when the major matches and the host's minor is at least the declared one: a plugin written against <c>2.1</c> runs on <c>2.1</c>, <c>2.2</c> and <c>2.9</c>, and is refused on <c>2.0</c> (the members it needs are not there yet) and on <c>3.0</c> (a breaking change).
/// A bare major, <c>"2"</c>, is read as <c>2.0</c>.
/// <para>
/// The SDK therefore commits to: additive changes bump <see cref="Minor"/>, breaking changes bump <see cref="Major"/>, and <c>SDK-CHANGELOG.md</c> beside this file records what each minor added.
/// Plugins run in-process with no sandbox; this check is a compatibility gate, not a security boundary.
/// </para>
/// </summary>
public static class PluginApiVersion
{
    /// <summary>The current SDK major version. Bump on any breaking change to the plugin-author surface.</summary>
    public const int Major = 1;

    /// <summary>The current SDK minor version. Bump on additive changes.</summary>
    public const int Minor = 0;

    /// <summary>The current version as a <c>major.minor</c> string.</summary>
    public static string Current => $"{Major}.{Minor}";

    /// <summary>
    /// Parses the major component of a declared <c>api-version</c> string (accepts <c>"1"</c> or <c>"1.3"</c>).
    /// Returns false when the value is missing or not a number.
    /// </summary>
    public static bool TryParseMajor(string? declared, out int major)
        => TryParse(declared, out major, out _);

    /// <summary>
    /// Parses a declared <c>api-version</c> into its major and minor components.
    /// A bare major yields minor 0 Returns false when the value is missing or either component is not a number.
    /// </summary>
    public static bool TryParse(string? declared, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(declared))
            return false;

        string value = declared.Trim();
        int dot = value.IndexOf('.');
        string head = dot >= 0 ? value[..dot] : value;
        string tail = dot >= 0 ? value[(dot + 1)..] : string.Empty;

        if (!int.TryParse(head.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            return false;

        if (tail.Length == 0)
            // "2" is "2.0": a manifest that names only the major targets none of that major's later additions.
            return true;

        return int.TryParse(tail.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>
    /// True when a plugin declaring <paramref name="declared"/> is compatible with this SDK: the same major, and this SDK's <see cref="Minor"/> at least the declared minor.
    /// </summary>
    public static bool IsCompatible(string? declared)
        => TryParse(declared, out int major, out int minor) && major == Major && minor >= 0 && minor <= Minor;
}

using System.Globalization;

namespace DMCBK.PluginSdk;

/// <summary>
/// A semantic version, <c>X.Y.Z[-prerelease][+build]</c>.
/// Parsing is lenient about missing trailing components (<c>"2"</c> and <c>"2.0"</c> both read as <c>2.0.0</c>) because manifests and ranges are written by hand; everything else follows semver 2.0.0, including prerelease precedence.
/// <para>
/// Ordering is full semver precedence: build metadata is ignored, and a prerelease sorts below the release it precedes.
/// Range checks use the same precedence: <see cref="SemVerRange.Satisfies(SemVer, bool)"/> respects prerelease bounds and ignores build metadata.
/// </para>
/// </summary>
public readonly struct SemVer : IEquatable<SemVer>, IComparable<SemVer>
{
    /// <summary>Creates a version from its core triple, with an optional prerelease tag and build metadata.</summary>
    public SemVer(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
        Build = string.IsNullOrEmpty(build) ? null : build;
    }

    /// <summary>The major component.</summary>
    public int Major { get; }

    /// <summary>The minor component.</summary>
    public int Minor { get; }

    /// <summary>The patch component.</summary>
    public int Patch { get; }

    /// <summary>The prerelease tag without its leading <c>-</c>, or null when this is a release.</summary>
    public string? Prerelease { get; }

    /// <summary>The build metadata without its leading <c>+</c>, or null. Never affects comparison.</summary>
    public string? Build { get; }

    /// <summary>True when a prerelease tag is present.</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>The <c>major.minor.patch</c> core, with no prerelease tag and no build metadata.</summary>
    public string Core => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>
    /// Parses <c>X[.Y[.Z]][-prerelease][+build]</c>.
    /// Returns false for an empty, non-numeric or otherwise malformed value; never throws.
    /// </summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> value = text.AsSpan().Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
            value = value[1..];

        string? build = null;
        int plus = value.IndexOf('+');
        if (plus >= 0)
        {
            build = value[(plus + 1)..].ToString();
            value = value[..plus];
            if (!IsValidTag(build))
                return false;
        }

        string? prerelease = null;
        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].ToString();
            value = value[..dash];
            if (!IsValidTag(prerelease, prerelease: true))
                return false;
        }

        if (!TryParseCore(value, out int major, out int minor, out int patch))
            return false;

        version = new SemVer(major, minor, patch, prerelease, build);
        return true;
    }

    /// <summary>Parses a version, throwing <see cref="FormatException"/> when the text is not one.</summary>
    public static SemVer Parse(string text)
        => TryParse(text, out SemVer version)
            ? version
            : throw new FormatException(PluginStrings.SemVerInvalid(text));

    /// <summary>
    /// Compares the <c>major.minor.patch</c> triple only, ignoring the prerelease tag and the build metadata.
    /// This is the comparison every range check uses.
    /// </summary>
    public int CompareCore(SemVer other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
            return result;

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    /// <summary>Full semver precedence: the core triple, then prerelease ordering. Build metadata is ignored.</summary>
    public int CompareTo(SemVer other)
    {
        int core = CompareCore(other);
        if (core != 0)
            return core;

        if (Prerelease is null && other.Prerelease is null)
            return 0;

        // A release outranks any prerelease of the same core triple.
        if (Prerelease is null)
            return 1;

        if (other.Prerelease is null)
            return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Equality by full precedence: build metadata is not compared.</summary>
    public bool Equals(SemVer other) => CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <summary>The version as it would be written, including any prerelease tag and build metadata.</summary>
    public override string ToString()
    {
        string text = Core;
        if (Prerelease is not null)
            text += "-" + Prerelease;

        return Build is null ? text : text + "+" + Build;
    }

#pragma warning disable CS1591 // The comparison operators are self-describing.
    public static bool operator ==(SemVer left, SemVer right) => left.Equals(right);

    public static bool operator !=(SemVer left, SemVer right) => !left.Equals(right);

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
#pragma warning restore CS1591

    private static bool TryParseCore(ReadOnlySpan<char> value, out int major, out int minor, out int patch)
    {
        major = 0;
        minor = 0;
        patch = 0;
        if (value.IsEmpty)
            return false;

        Span<int> parts = [0, 0, 0];
        int index = 0;
        foreach (Range segment in value.Split('.'))
        {
            if (index > 2)
                return false;

            ReadOnlySpan<char> part = value[segment];
            if (part.IsEmpty || (part.Length > 1 && part[0] == '0')
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                return false;

            parts[index++] = parsed;
        }

        major = parts[0];
        minor = parts[1];
        patch = parts[2];
        return true;
    }

    /// <summary>A prerelease or build tag: dot-separated identifiers of ASCII alphanumerics and hyphens.</summary>
    private static bool IsValidTag(string tag, bool prerelease = false)
    {
        if (tag.Length == 0)
            return false;

        foreach (Range segment in tag.AsSpan().Split('.'))
        {
            ReadOnlySpan<char> identifier = tag.AsSpan()[segment];
            if (identifier.IsEmpty || (prerelease && identifier.Length > 1 && identifier[0] == '0' && identifier.ToString().All(char.IsAsciiDigit)))
                return false;

            foreach (char c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return false;
            }
        }

        return true;
    }

    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
        {
            int result = CompareIdentifier(a[i], b[i]);
            if (result != 0)
                return result;
        }

        // "1.0.0-alpha" precedes "1.0.0-alpha.1": fewer identifiers is lower when all shared ones match.
        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit);
        bool rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            int length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.CompareOrdinal(left, right);
        }

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (leftNumeric)
            return -1;

        if (rightNumeric)
            return 1;

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string identifier, out int value)
        => int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

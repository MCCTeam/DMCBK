using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DMCBK.Core.Configuration;

namespace DMCBK.Core.Localization;

/// <summary>
/// Turns the configured <c>Localization.Language</c> into a per-client <see cref="CultureInfo"/>: the library string corpus, the config-comment corpus, plugin <c>lang/</c> tables, plugin settings comments and the manual all read from it.
/// <para>
/// Minecraft writes language tags as <c>pt_br</c> and .NET writes them as <c>pt-BR</c>; both are accepted, and <see cref="ToMinecraftTag"/> converts back for the locale announced to the server.
/// The value <c>auto</c> (the default) means the operating system's UI culture, which is what the retired <c>LoadMccTranslation = true</c> did by accident of leaving <c>McStrings.Culture</c> null.
/// </para>
/// </summary>
public static class UiCulture
{
    /// <summary>The configured value that means "follow the operating system".</summary>
    public const string Auto = "auto";

    /// <summary>
    /// Puts a configured language tag into force for the current execution context: the string corpus, the manual, and the formatting of every argument a message carries.
    /// Returns the culture it resolved to.
    /// </summary>
    public static CultureInfo Apply(string? language)
    {
        CultureInfo culture = Resolve(language);
        McStrings.Culture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }

    /// <summary>Applies a culture only to the current execution context and restores it on disposal.</summary>
    public static IDisposable Enter(CultureInfo culture) => new CultureScope(culture);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _previousResources = McStrings.Culture;
        public CultureScope(CultureInfo culture)
        { McStrings.Culture = culture; CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = culture; }
        public void Dispose()
        { McStrings.Culture = _previousResources; CultureInfo.CurrentCulture = _previous; CultureInfo.CurrentUICulture = _previousUi; }
    }

    /// <summary>True when a configured language tag means "follow the operating system".</summary>
    public static bool IsAuto(string? language)
        => string.IsNullOrWhiteSpace(language)
            || string.Equals(language.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the UI culture for a run.
    /// <c>auto</c>, an empty value and a tag that names no culture all resolve to <see cref="CultureInfo.CurrentUICulture"/>, so this never throws and never leaves the client with no language at all.
    /// The validator is what warns about a bad tag; by the time the culture is resolved the run has to continue regardless.
    /// </summary>
    public static CultureInfo Resolve(LocalizationConfig localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return Resolve(localization.Language);
    }

    /// <summary>Resolves a single configured language tag. See <see cref="Resolve(LocalizationConfig)"/>.</summary>
    public static CultureInfo Resolve(string? language)
        => IsAuto(language) || !TryParseTag(language, out CultureInfo? culture)
            ? CultureInfo.CurrentUICulture
            : culture;

    /// <summary>
    /// Parses a language tag in Minecraft form (<c>pt_br</c>) or BCP-47 form (<c>pt-BR</c>) into a culture that actually exists.
    /// Returns false for <c>auto</c>, for an empty value, and for a tag no installed culture matches, so a typo is caught at validation rather than silently becoming a made-up culture.
    /// </summary>
    public static bool TryParseTag(string? tag, [NotNullWhen(true)] out CultureInfo? culture)
    {
        culture = null;
        if (string.IsNullOrWhiteSpace(tag) || IsAuto(tag))
            return false;

        string name = tag.Trim().Replace('_', '-');
        try
        {
            // predefinedOnly, because without it .NET happily manufactures a culture for any well-formed tag and "en-ZZ" would silently become a language nothing has strings for.
            culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The culture as Minecraft writes it (<c>pt_br</c>), which is what goes on the wire in the client information packet.
    /// The invariant culture has no tag of its own and maps to <c>en_us</c>.
    /// </summary>
    public static string ToMinecraftTag(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string name = culture.Name;
        return name.Length == 0 ? "en_us" : name.Replace('-', '_').ToLowerInvariant();
    }
}

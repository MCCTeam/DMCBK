using System.Globalization;

namespace DMCBK.PluginSdk;

/// <summary>
/// The localization a plugin gets when it ships no <c>lang/</c> folder: every key resolves to itself.
/// <para>
/// Returning the key rather than null or empty is deliberate, and it is what MCC's own corpus does.
/// A plugin that starts using <see cref="PluginContext.Strings"/> before it has written any language files still shows readable output, and a key that never got translated shows up on screen as its own name instead of disappearing.
/// </para>
/// </summary>
internal sealed class EmptyPluginLocalization : IPluginLocalization
{
    /// <summary>The shared instance.</summary>
    public static EmptyPluginLocalization Instance { get; } = new();

    private EmptyPluginLocalization()
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Languages => [];

    /// <inheritdoc/>
    public string Get(string key) => key;

    /// <inheritdoc/>
    public string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, key, args ?? []);

    /// <inheritdoc/>
    public PluginLocalizationReport Report(CultureInfo culture) => new([], null, []);
}

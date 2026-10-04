using System.Globalization;
using System.Resources;

namespace DMCBK.Core.Configuration;

/// <summary>
/// The default comment source: reads the ConfigComments corpus carried over from the legacy tree and now embedded in DMCBK.Core (<c>Configuration/Resources/ConfigComments.resx</c>).
/// Owning the corpus here keeps
/// generated files documented and localizable (drop a <c>ConfigComments.&lt;culture&gt;.resx</c> satellite to
/// translate) without referencing the legacy assembly.
/// Instance-scoped: no static state.
/// </summary>
public sealed class ConfigCommentSource : IConfigCommentSource
{
    private const string BaseName = "DMCBK.Configuration.Resources.ConfigComments";

    private readonly ResourceManager _resources;
    private readonly CultureInfo? _culture;

    /// <summary>Creates a comment source for the given culture (null uses the current UI culture).</summary>
    public ConfigCommentSource(CultureInfo? culture = null)
    {
        _resources = new ResourceManager(BaseName, typeof(ConfigCommentSource).Assembly);
        _culture = culture;
    }

    /// <inheritdoc />
    public string? GetComment(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string? value = _culture is null
            ? _resources.GetString(key)
            : _resources.GetString(key, _culture);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

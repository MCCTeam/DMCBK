using System.Globalization;
using System.Resources;
namespace DMCBK.Marketplace;
/// <summary>Localizes structured marketplace diagnostics without embedding UI text in runtime code.</summary>
public static class MarketplaceDiagnostics
{
    private static readonly ResourceManager Resources = new("DMCBK.Marketplace.Resources.MarketplaceStrings", typeof(MarketplaceDiagnostics).Assembly);
    /// <summary>Renders a diagnostic using the requested host culture.</summary>
    public static string Format(MarketplaceException failure, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        string? template = Resources.GetString(failure.Code, culture);
        return template is null ? Text("diagnostic.generic", culture, failure.Code, string.Join(", ", failure.Values))
            : string.Format(culture, template, failure.Values.Cast<object?>().ToArray());
    }
    /// <summary>Formats a library-owned resource using a client-specific culture.</summary>
    public static string Text(string key, CultureInfo culture, params object?[] values)
        => string.Format(culture, Resources.GetString(key, culture) ?? key, values);
}

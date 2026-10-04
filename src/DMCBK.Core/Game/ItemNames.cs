using System.Globalization;
using System.Text;
using Umpk.Text;
namespace DMCBK.Core;
/// <summary>Resolves item names using the client translation source.</summary>
public static class ItemNames
{
    /// <summary>Returns the translated item name or its readable identifier.</summary>
    public static string TypeName(ITranslationSource translations, string itemId)
    {
        ArgumentNullException.ThrowIfNull(translations);
        if (string.IsNullOrEmpty(itemId))
            return string.Empty;

        string path = itemId;
        string ns = "minecraft";
        int colon = itemId.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            ns = itemId[..colon];
            path = itemId[(colon + 1)..];
        }

        if (TryTranslate(translations, $"item.{ns}.{path}", out string? item))
            return item;

        if (TryTranslate(translations, $"block.{ns}.{path}", out string? block))
            return block;

        return Pascal(path);
    }

    private static string Pascal(string path)
    {
        var sb = new StringBuilder(path.Length);
        bool upper = true;
        foreach (char c in path)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    private static bool TryTranslate(ITranslationSource translations, string key, out string value)
    {
        string resolved = Component.Translatable(key).ToPlainText(translations);
        if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, key, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = resolved;
        return true;
    }

}

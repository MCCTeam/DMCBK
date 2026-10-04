using System.Text;
using Umpk;
using Umpk.Text;

namespace DMCBK.Core;

/// <summary>Shared entity naming policy for text commands and rich hosts.</summary>
public static class EntityPresentation
{
    /// <summary>Resolves a namespaced entity type through vanilla translations, then falls back to PascalCase.</summary>
    public static string TypeName(ITranslationSource translations, string typeId)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        string ns = Identifier.DefaultNamespace;
        string path = typeId;
        int colon = typeId.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            ns = typeId[..colon];
            path = typeId[(colon + 1)..];
        }

        string key = $"entity.{ns}.{path}";
        string resolved = Component.Translatable(key).ToPlainText(translations);
        return string.IsNullOrEmpty(resolved) || string.Equals(resolved, key, StringComparison.Ordinal)
            ? Pascal(path)
            : resolved;
    }

    private static string Pascal(string path)
    {
        var text = new StringBuilder(path.Length);
        bool upper = true;
        foreach (char c in path)
        {
            if (c is '_' or '-' or '.')
            {
                upper = true;
                continue;
            }

            text.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return text.ToString();
    }
}

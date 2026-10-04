using System.Globalization;
using DMCBK.Core.Commands;
using Umpk.Text;

namespace DMCBK.Core;

/// <summary>Pure presentation and hierarchy helpers shared by advancement commands and hosts.</summary>
public static class AdvancementPresentation
{
    /// <summary>Whether UMPK decodes structured advancement data for a protocol.</summary>
    public static bool StructuredDataAvailable(int protocol)
        => protocol is >= 770 and <= 773 or 776 or 777;

    /// <summary>Whether every criterion in an advancement is complete.</summary>
    public static bool IsComplete(AdvancementInfo advancement)
        => advancement.CriteriaCompleted >= advancement.CriteriaCount;

    /// <summary>The id without the vanilla namespace.</summary>
    public static string ShortId(string id)
    {
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && id.AsSpan(0, colon).SequenceEqual("minecraft") ? id[(colon + 1)..] : id;
    }

    /// <summary>The top-level branch used by the scope picker.</summary>
    public static string Scope(string id)
    {
        string path = ShortId(id);
        int slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>Resolves the display title, including generated recipe advancements.</summary>
    public static string Title(Umpk.Text.ITranslationSource translations, AdvancementInfo advancement)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(advancement);
        string? vanillaKey = VanillaTitleKey(advancement.Id);
        if (advancement.Title is { Length: > 0 } title
            && !string.Equals(title, vanillaKey, StringComparison.Ordinal))
            return title;

        if (vanillaKey is not null && TryTranslate(translations, vanillaKey, out string resolved))
            return resolved;

        string shortId = ShortId(advancement.Id);
        if (shortId.StartsWith("recipes/", StringComparison.Ordinal))
        {
            int slash = shortId.LastIndexOf('/');
            string result = slash >= 0 ? shortId[(slash + 1)..] : shortId;
            if (result.Length > 0 && result != "root")
                return ItemNames.TypeName(translations, "minecraft:" + result);
        }

        return shortId;
    }

    /// <summary>Vanilla frame name, a numeric fallback, or an empty string when no display exists.</summary>
    public static string FrameName(int? frame) => frame switch
    {
        0 => "Task",
        1 => "Challenge",
        2 => "Goal",
        int value => value.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    /// <summary>Computes a stable hierarchy depth from parent ids, guarding cycles and missing parents.</summary>
    public static int Depth(AdvancementInfo advancement, IReadOnlyDictionary<string, AdvancementInfo> byId)
    {
        int depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? parent = advancement.ParentId;
        while (parent is not null && depth < 32 && seen.Add(parent) && byId.TryGetValue(parent, out AdvancementInfo? node))
        {
            depth++;
            parent = node.ParentId;
        }
        return depth;
    }

    /// <summary>A stable parent-first key that keeps every root before its indented descendants.</summary>
    public static string HierarchyKey(
        AdvancementInfo advancement,
        IReadOnlyDictionary<string, AdvancementInfo> byId)
    {
        var path = new List<string> { ShortId(advancement.Id) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { advancement.Id };
        string? parent = advancement.ParentId;
        while (parent is not null && path.Count < 32 && seen.Add(parent)
            && byId.TryGetValue(parent, out AdvancementInfo? node))
        {
            path.Add(ShortId(node.Id));
            parent = node.ParentId;
        }
        path.Reverse();
        return string.Join('\u001F', path);
    }

    private static string? VanillaTitleKey(string id)
    {
        string path = id;
        int colon = id.IndexOf(':');
        if (colon >= 0)
        {
            if (!id.AsSpan(0, colon).SequenceEqual("minecraft"))
                return null;
            path = id[(colon + 1)..];
        }
        return path.Length == 0 ? null : $"advancements.{path.Replace('/', '.')}.title";
    }

    private static bool TryTranslate(Umpk.Text.ITranslationSource translations, string key, out string value)
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

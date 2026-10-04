namespace DMCBK.Core.Presentation;

/// <summary>Host-supplied labels for command text. Hosts choose visual styling and text width.</summary>
public sealed record GlyphSet
{
    /// <summary>Portable text labels used when a host supplies no vocabulary.</summary>
    public static GlyphSet Ascii { get; } = new();

    /// <summary>Whether the host supplied its emoji style.</summary>
    public bool IsEmoji { get; init; }

    /// <summary>Host-measured width used by diagnostic caret output.</summary>
    public int StatusWidth { get; init; } = 3;

    /// <summary>The host label for Ok.</summary>
    public string Ok { get; init; } = "[+]";

    /// <summary>The host label for Fail.</summary>
    public string Fail { get; init; } = "[x]";

    /// <summary>The host label for Blocked.</summary>
    public string Blocked { get; init; } = "[-]";

    /// <summary>The host label for Pending.</summary>
    public string Pending { get; init; } = "[~]";

    /// <summary>The host label for Info.</summary>
    public string Info { get; init; } = "[i]";

    /// <summary>The host label for Warn.</summary>
    public string Warn { get; init; } = "[!]";

    /// <summary>The host label for Empty.</summary>
    public string Empty { get; init; } = "·";

    /// <summary>The host label for ChunkUnloaded.</summary>
    public string ChunkUnloaded { get; init; } = "□";

    /// <summary>The host label for ChunkLoading.</summary>
    public string ChunkLoading { get; init; } = "▣";

    /// <summary>The host label for ChunkLoaded.</summary>
    public string ChunkLoaded { get; init; } = "■";

    /// <summary>The host label for PluginEnabled.</summary>
    public string PluginEnabled { get; init; } = "[+]";

    /// <summary>The host label for PluginDisabled.</summary>
    public string PluginDisabled { get; init; } = "[-]";

    /// <summary>The host label for Health.</summary>
    public string Health { get; init; } = "HP";

    /// <summary>The host label for Food.</summary>
    public string Food { get; init; } = "Food";

    /// <summary>The host label for Experience.</summary>
    public string Experience { get; init; } = "XP";

    /// <summary>The host label for AdvancementDone.</summary>
    public string AdvancementDone { get; init; } = "[DONE]";

    /// <summary>The host label for AdvancementTodo.</summary>
    public string AdvancementTodo { get; init; } = "[TODO]";

    /// <summary>The host label for CheckboxOn.</summary>
    public string CheckboxOn { get; init; } = "[x]";

    /// <summary>The host label for CheckboxOff.</summary>
    public string CheckboxOff { get; init; } = "[ ]";

    /// <summary>Labels for unloaded, loading and loaded chunks.</summary>
    public string[] ChunkStatus => [ChunkUnloaded, ChunkLoading, ChunkLoaded];
}

using DMCBK.Core.Localization;

namespace DMCBK.Core.Manual;

/// <summary>Which part of the manual a topic belongs to.</summary>
public enum ManualGroup
{
    /// <summary>Getting a client connected and talking.</summary>
    Basics,

    /// <summary>Playing: moving, carrying things, dealing with other entities.</summary>
    Gameplay,

    /// <summary>The client itself: configuration, console, TUI.</summary>
    Client,

    /// <summary>Extending MCC.</summary>
    Extending,

    /// <summary>Contributed by a plugin.</summary>
    Plugins,
}

/// <summary>
/// One manual page: an id, the group it is listed under, and a one-line summary for the index.
/// </summary>
/// <param name="Id">The topic name as typed (<c>/man movement</c>).</param>
/// <param name="Group">Where it is listed.</param>
/// <param name="SummaryText">
/// The index summary.
/// Null means "look it up in the string corpus under <c>man.summary.&lt;id&gt;</c>", which is what MCC's own topics do.
/// A plugin passes its own text, because a plugin's strings are not in MCC's corpus.
/// </param>
public sealed record ManualTopic(string Id, ManualGroup Group, string? SummaryText = null)
{
    /// <summary>The one-line summary shown beside the topic in <c>/man</c>'s index.</summary>
    public string Summary => SummaryText ?? McStrings.Get($"man.summary.{Id}");
}

/// <summary>
/// MCC's own table of contents.
/// <para>
/// Declared here rather than discovered from the embedded resources, for two reasons.
/// The order is editorial (a beginner should meet <c>getting-started</c> first, not <c>accounts</c> first because the alphabet says so), and a declared set is what lets a test assert that every locale carries every page: a translation that silently loses a topic would otherwise just vanish from the index.
/// </para>
/// <para>
/// Plugin topics are not here.
/// They arrive through <see cref="ManualCatalog.Register"/> and leave when the plugin unloads.
/// </para>
/// </summary>
public static class ManualTopics
{
    /// <summary>MCC's own topics, in the order the index prints them.</summary>
    public static IReadOnlyList<ManualTopic> All { get; } =
    [
        new("getting-started", ManualGroup.Basics),
        new("connecting", ManualGroup.Basics),
        new("accounts", ManualGroup.Basics),
        new("chat", ManualGroup.Basics),

        new("movement", ManualGroup.Gameplay),
        new("pathfinding", ManualGroup.Gameplay),
        new("inventory", ManualGroup.Gameplay),
        new("entities", ManualGroup.Gameplay),

        new("config", ManualGroup.Client),
        new("variables", ManualGroup.Client),
        new("console", ManualGroup.Client),
        new("tui", ManualGroup.Client),

        new("plugins", ManualGroup.Extending),
        new("writing-plugins", ManualGroup.Extending),
        new("scripts", ManualGroup.Extending),
    ];

    /// <summary>The groups, in index order.</summary>
    public static IReadOnlyList<ManualGroup> Groups { get; } =
    [
        ManualGroup.Basics,
        ManualGroup.Gameplay,
        ManualGroup.Client,
        ManualGroup.Extending,
        ManualGroup.Plugins,
    ];

    /// <summary>The localized heading for a group.</summary>
    public static string GroupName(ManualGroup group) => group switch
    {
        ManualGroup.Basics => McStrings.Get("man.group.basics"),
        ManualGroup.Gameplay => McStrings.Get("man.group.gameplay"),
        ManualGroup.Client => McStrings.Get("man.group.client"),
        ManualGroup.Extending => McStrings.Get("man.group.extending"),
        _ => McStrings.Get("cmd.category.plugins"),
    };

    /// <summary>Finds one of MCC's own topics by id. Plugin topics live in <see cref="ManualCatalog"/>.</summary>
    public static ManualTopic? Find(string? id)
        => id is null
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

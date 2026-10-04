using DMCBK.Core.Localization;

namespace DMCBK.Core.Commands;

/// <summary>
/// What a command is FOR, which is how <c>/help</c> groups its index.
/// <para>
/// The listing used to be one flat alphabetical run of 44 grammars, so <c>/bed</c> sat between <c>/animation</c> and <c>/blockinfo</c> and nothing on screen told a world command from a console one.
/// Alphabetical order answers "where is the command called X", which is the question a user who already knows the name asks; a beginner is asking "what can this thing do", and only grouping answers that.
/// </para>
/// </summary>
public enum CommandCategory
{
    /// <summary>Connecting, reconnecting, accounts, the client's own lifecycle.</summary>
    Session,

    /// <summary>Moving through the world and acting on blocks.</summary>
    World,

    /// <summary>Carrying, wearing, dropping and crafting items.</summary>
    Inventory,

    /// <summary>Other players and other entities.</summary>
    Entities,

    /// <summary>Interacting with the things a server puts in front of you: books, beds, dialogs, menus.</summary>
    Interaction,

    /// <summary>The client itself: console, config, diagnostics.</summary>
    Client,

    /// <summary>Contributed by a plugin. The default for anything that does not say otherwise.</summary>
    Plugins,
}

/// <summary>
/// Which gameplay subsystems a command needs switched on.
/// Rendered as the <c>REQUIRES</c> block of a help page against the LIVE configuration, so a user whose <c>Pathfinding = false</c> sees that before running the command rather than getting a generic refusal after.
/// </summary>
[Flags]
public enum CommandFeature
{
    /// <summary>Needs nothing in particular.</summary>
    None = 0,

    /// <summary>Needs <c>client.toml [Gameplay] Terrain</c>.</summary>
    Terrain = 1 << 0,

    /// <summary>Needs <c>client.toml [Gameplay] Inventory</c>.</summary>
    Inventory = 1 << 1,

    /// <summary>Needs <c>client.toml [Gameplay] Entity</c>.</summary>
    Entity = 1 << 2,

    /// <summary>Needs <c>client.toml [Gameplay] Physics</c>.</summary>
    Physics = 1 << 3,

    /// <summary>Needs <c>client.toml [Gameplay] Pathfinding</c>.</summary>
    Pathfinding = 1 << 4,
}

/// <summary>
/// One authored row of a command's <c>USAGE</c> block: a syntax fragment and what it does.
/// <para>
/// The alternative is deriving every row from the Brigadier tree, which is what the old page did, and it produces one line per BRANCH rather than one line per idea: <c>/move</c> listed its six directions twice each (with and without <c>-f</c>) for twelve of its twenty-four lines, none of them glossed.
/// A command that authors these gets a page a beginner can read; one that authors none still gets the derived listing, so nothing is forced to opt in.
/// </para>
/// </summary>
/// <param name="Syntax">The syntax fragment, without the command name or prefix (for example <c>&lt;x&gt; &lt;y&gt; &lt;z&gt;</c>).</param>
/// <param name="Description">What that form does. Already localized. Null renders the syntax alone.</param>
public readonly record struct UsageLine(string Syntax, string? Description = null);

/// <summary>
/// One authored flag of a command, lifted out of the usage rows so it is stated once instead of doubling the row count.
/// </summary>
/// <param name="Name">The flag as typed, for example <c>-f</c>.</param>
/// <param name="Description">What it does. Already localized.</param>
public readonly record struct UsageFlag(string Name, string Description);

/// <summary>Localized display names for <see cref="CommandCategory"/>.</summary>
public static class CommandCategoryNames
{
    /// <summary>The heading a category is listed under.</summary>
    public static string Of(CommandCategory category) => category switch
    {
        CommandCategory.Session => McStrings.Get("cmd.category.session"),
        CommandCategory.World => McStrings.Get("cmd.category.world"),
        CommandCategory.Inventory => McStrings.Get("cmd.category.inventory"),
        CommandCategory.Entities => McStrings.Get("cmd.category.entities"),
        CommandCategory.Interaction => McStrings.Get("cmd.category.interaction"),
        CommandCategory.Client => McStrings.Get("cmd.category.client"),
        _ => McStrings.Get("cmd.category.plugins"),
    };

    /// <summary>The order categories are printed in: what a new user needs first, first.</summary>
    public static readonly CommandCategory[] Order =
    [
        CommandCategory.Session,
        CommandCategory.World,
        CommandCategory.Inventory,
        CommandCategory.Entities,
        CommandCategory.Interaction,
        CommandCategory.Client,
        CommandCategory.Plugins,
    ];
}

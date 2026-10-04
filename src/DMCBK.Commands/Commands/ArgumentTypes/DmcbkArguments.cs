using Umpk;
using Umpk.Commands;
using Umpk.Game.Registries;

namespace DMCBK.Core.Commands.ArgumentTypes;

/// <summary>
/// Thin forwarder onto <see cref="Arguments"/> (UMPK's library argument types) and the registry ids they suggest from.
/// Kept as a named surface, rather than inlined at each of the ~30 call sites, so the command classes read the same as before the dispatcher swap: only the factory bodies changed.
/// A bare rename (<see cref="Location"/>, <see cref="Tuple"/>, <see cref="ItemType"/>, <see cref="EntityType"/>, <see cref="InventoryId"/>, <see cref="InventorySlot"/>) carries no suggestion-behavior loss because none of the legacy Brigadier argument types they replace offered any either.
/// <see cref="HotbarSlot"/>, <see cref="ServerNick"/> and <see cref="AccountNick"/> DID offer live suggestions (nine slot numbers; configured server/account names); UMPK separates parsing from suggesting, so those three stay as bare type factories here and each call site attaches its own <c>.Suggests(...)</c> provider (see <c>ChangeSlotCommand</c> and <c>SessionCommandHelpers.SuggestServers</c>/<c>SuggestAccounts</c>).
/// </summary>
public static class DmcbkArguments
{
    /// <summary>
    /// A vanilla coordinate triple: absolute, player-relative (<c>~</c>), or local (<c>^</c>, new with this swap - the legacy MCC parser never supported it).
    /// Centre-correction stays OFF to preserve today's <c>/move</c>/<c>/dig</c>/etc. semantics (vanilla defaults it on for most commands; MCC's own grammar never centre-corrected, so this keeps that rather than silently changing where a bare-integer command lands).
    /// </summary>
    public static IArgumentType<CommandLocation> Location() => Arguments.Location(centerCorrect: false);

    /// <summary>A chunk coordinate pair.</summary>
    public static IArgumentType<Umpk.Geometry.ChunkPos> Tuple() => Arguments.ChunkPos();

    /// <summary>An entity type id (namespaced or bare), suggested from the live registry.</summary>
    public static IArgumentType<Identifier> EntityType() => Arguments.RegistryId(RegistryIds.EntityType);

    /// <summary>An item type id (namespaced or bare), suggested from the live registry.</summary>
    public static IArgumentType<Identifier> ItemType() => Arguments.RegistryId(RegistryIds.Item);

    /// <summary>An open-inventory window id.</summary>
    public static IArgumentType<int> InventoryId() => Arguments.Integer();

    /// <summary>An inventory slot index.</summary>
    public static IArgumentType<int> InventorySlot() => Arguments.Integer();

    /// <summary>A hotbar slot (1-9); attach <c>.Suggests</c> at the call site for the nine slot numbers.</summary>
    public static IArgumentType<int> HotbarSlot() => Arguments.Integer(1, 9);

    /// <summary>A player name (no live suggestions; the legacy type had none either).</summary>
    public static IArgumentType<string> PlayerName() => Arguments.QuotableString();

    /// <summary>A configured server name; attach <c>.Suggests</c> at the call site (from servers.toml).</summary>
    public static IArgumentType<string> ServerNick() => Arguments.QuotableString();

    /// <summary>A configured account name; attach <c>.Suggests</c> at the call site (from accounts.toml).</summary>
    public static IArgumentType<string> AccountNick() => Arguments.QuotableString();
}

/// <summary>
/// An inventory click action supported by the <c>inventory click</c> command.
/// Kept from the legacy <c>SimpleArgumentTypes.cs</c> the dispatcher swap deleted; the keyword-to-enum mapping that used to live in <c>InventoryActionArgumentType.Parse</c> moved into <c>InventoryCommand</c>'s command body (UMPK's <see cref="Arguments.Word"/> parses any word, so an unrecognized keyword is now a command-level failure rather than a parse failure - see <c>InventoryCommand.ParseAction</c>).
/// </summary>
public enum InventoryClickAction
{
    /// <summary>Left click (pickup / place).</summary>
    Left,

    /// <summary>Right click (half pickup / place one).</summary>
    Right,

    /// <summary>Middle click (clone in creative).</summary>
    Middle,

    /// <summary>Shift left click (quick move).</summary>
    Shift,

    /// <summary>Shift right click (quick move).</summary>
    ShiftRight,
}

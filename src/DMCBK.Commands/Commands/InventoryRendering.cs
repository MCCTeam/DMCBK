using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Text;

namespace DMCBK.Core.Commands;

/// <summary>
/// Renders inventory slots the way the legacy client did, so a listing is indistinguishable from the old one.
/// The shapes here are ports of <c>MinecraftClient/Inventory/Item.cs</c> (<c>ToString</c>/<c>ToFullString</c>, lines 210-272) and <c>Container.IsHotbar</c> (Inventory/Container.cs:253-269).
/// <para>
/// The type is public for one member only: <see cref="TypeName"/>, which a host outside this assembly (the TUI's container cells) has to resolve names through so a slot reads the same in the grid as in the text listing.
/// Everything else stays internal, because it is this assembly's own listing format.
/// </para>
/// </summary>
public static class InventoryRendering
{
    /// <summary>The legacy title of the player's own window (McClient.cs:3015).</summary>
    internal const string PlayerWindowTitle = "Player Inventory";

    /// <summary>
    /// The item name, resolved the way legacy did (Item.cs:197-208): try the vanilla lang key <c>item.minecraft.&lt;snake&gt;</c>, then <c>block.minecraft.&lt;snake&gt;</c>, then fall back to the PascalCase form of the id.
    /// UMPK ships those vanilla tables per protocol, so this resolves through the same translation source the chat renderer uses rather than needing a table of its own.
    /// </summary>
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

    /// <summary>
    /// One slot line's item text: <c>x{count,-2} {name}</c>, then the custom name, damage, and every enchantment, in the legacy order and with the legacy separators.
    /// </summary>
    internal static string Describe(ITranslationSource translations, ItemStackInfo stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.CurrentCulture, $"x{stack.Count,-2} {TypeName(translations, stack.ItemId)}");

        if (!string.IsNullOrEmpty(stack.CustomName))
            // The trailing section-8 is legacy's (Item.cs:266): it drops the rest of the line back to grey after a custom name that may have carried its own colours.
            sb.Append(CultureInfo.CurrentCulture, $" - {stack.CustomName}§8");

        if (stack.Damage != 0)
            sb.Append(CultureInfo.CurrentCulture, $" | {McStrings.Get("cmd.inventory.damage")}: {stack.Damage}");

        foreach (ItemEnchantmentInfo enchantment in stack.Enchantments)
        {
            sb.Append(CultureInfo.CurrentCulture,
                $" | {EnchantmentName(translations, enchantment.EnchantmentId)} {Roman(enchantment.Level)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The hotbar column legacy prints to the left of every slot line: the 1-based hotbar index for a hotbar slot, a space otherwise, prefixed with '&gt;' for the slot currently held.
    /// Returns null when the window has no hotbar (only the player's own window does).
    /// </summary>
    internal static string HotbarColumn(int slot, int slotCount, bool isPlayerWindow, int heldSlot)
    {
        if (!IsHotbar(slot, slotCount, isPlayerWindow, out int hotbar))
            return " ";

        string text = (hotbar + 1).ToString(CultureInfo.InvariantCulture);
        return hotbar == heldSlot ? ">" + text : text;
    }

    /// <summary>
    /// Legacy's Container.IsHotbar (Inventory/Container.cs:253-269): the hotbar is the last nine slots, and the player's own window has one more slot after them (the offhand) which must not shift the count.
    /// </summary>
    internal static bool IsHotbar(int slot, int slotCount, bool isPlayerWindow, out int hotbar)
    {
        int start = slotCount - 9;
        if (isPlayerWindow)
            start--;

        if (slot >= start && slot < start + 9)
        {
            hotbar = slot - start;
            return true;
        }

        hotbar = -1;
        return false;
    }

    private static string EnchantmentName(ITranslationSource translations, string id)
    {
        string key = "enchantment." + id.Replace(':', '.');
        return TryTranslate(translations, key, out string? name) ? name : id;
    }

    /// <summary>Roman numerals for enchantment levels, as legacy's EnchantmentMapping did.</summary>
    internal static string Roman(int level)
    {
        if (level is <= 0 or > 3999)
            return level.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];

        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            while (level >= values[i])
            {
                sb.Append(symbols[i]);
                level -= values[i];
            }
        }

        return sb.ToString();
    }

    // A vanilla table returns the key itself when it has no entry, so "resolved" means "came back different".
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
}

using Umpk.Game.Items;

namespace DMCBK.Core;

/// <summary>
/// A host-facing, immutable view of one item stack: the registry id, count, and the higher-level facts UMPK decodes from the unified component map (custom name, durability, enchantments), plus the raw list of present component ids for hosts that want the full picture.
/// Air/empty stacks report <see cref="IsEmpty"/> and an empty <see cref="ItemId"/>.
/// </summary>
/// <param name="ItemId">The namespaced item id (for example <c>minecraft:diamond_sword</c>); empty for air.</param>
/// <param name="Count">The stack count.</param>
/// <param name="IsEmpty">True when the stack is air or has a non-positive count.</param>
/// <param name="CustomName">The rendered custom display name, or null when unset.</param>
/// <param name="Damage">The accumulated durability damage (0 when undamaged or not damageable).</param>
/// <param name="MaxDamage">The maximum durability (0 when the item is not damageable).</param>
/// <param name="Enchantments">The applied enchantments as (id, level) pairs.</param>
/// <param name="ComponentIds">The namespaced ids of every data component present on the stack.</param>
public sealed record ItemStackInfo(
    string ItemId,
    int Count,
    bool IsEmpty,
    string? CustomName,
    int Damage,
    int MaxDamage,
    IReadOnlyList<ItemEnchantmentInfo> Enchantments,
    IReadOnlyList<string> ComponentIds)
{
    /// <summary>
    /// The decoded book content when the stack is a written or writable book, else null.
    /// Era-neutral: UMPK reads it from the 1.20.5+ structured components or the pre-1.20.5 NBT compound, so a caller never branches on the item era.
    /// Declared outside the positional parameter list so the record's constructor stays source-compatible for hosts that build a view by hand.
    /// </summary>
    public BookContentInfo? Book { get; init; }

    /// <summary>The shared empty (air) stack view.</summary>
    public static ItemStackInfo Empty { get; } =
        new(string.Empty, 0, true, null, 0, 0, [], []);

    /// <summary>Projects a UMPK <see cref="ItemStack"/> into a host-facing view, rendering names via translations.</summary>
    internal static ItemStackInfo From(ItemStack stack, Umpk.Text.ITranslationSource? translations)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (stack.IsEmpty)
            return Empty;

        var enchantments = new List<ItemEnchantmentInfo>();
        foreach (EnchantmentInstance enchantment in stack.Enchantments)
            enchantments.Add(new ItemEnchantmentInfo(enchantment.Enchantment.Id.ToString(), enchantment.Level));

        var componentIds = new List<string>();
        foreach (DataComponentEntry entry in stack.Components.Effective)
            componentIds.Add(entry.Type.Id.ToString());

        return new ItemStackInfo(
            stack.Item.Id.ToString(),
            stack.Count,
            false,
            stack.CustomName?.ToPlainText(translations),
            stack.Damage,
            stack.MaxDamage,
            enchantments,
            componentIds)
        {
            Book = ProjectBook(stack.Book, translations),
        };
    }

    private static BookContentInfo? ProjectBook(BookContent? book, Umpk.Text.ITranslationSource? translations)
    {
        if (book is null)
            return null;

        var pages = new List<string>(book.Pages.Count);
        foreach (BookContentPage page in book.Pages)
            pages.Add(page.Text.ToPlainText(translations));

        return new BookContentInfo(
            book.IsSigned, book.Title, book.Author, book.Generation, book.Resolved, pages);
    }
}

/// <summary>
/// The decoded content of a book stack, flattened to plain text for a text host.
/// Covers both item eras: 1.20.5+ written/writable book components and the pre-1.20.5 <c>pages</c>/<c>title</c>/<c>author</c> NBT.
/// </summary>
/// <param name="IsSigned">True for a written (signed) book, false for a book and quill.</param>
/// <param name="Title">The title, or null when the book is unsigned.</param>
/// <param name="Author">The author, or null when the book is unsigned.</param>
/// <param name="Generation">The copy generation (0 original, 1 copy, 2 copy of a copy, 3 tattered).</param>
/// <param name="Resolved">Whether the server has resolved the pages.</param>
/// <param name="Pages">The pages in order, flattened to plain text.</param>
public sealed record BookContentInfo(
    bool IsSigned,
    string? Title,
    string? Author,
    int Generation,
    bool Resolved,
    IReadOnlyList<string> Pages);

/// <summary>One enchantment on an item stack.</summary>
/// <param name="EnchantmentId">The namespaced enchantment id (for example <c>minecraft:sharpness</c>).</param>
/// <param name="Level">The enchantment level (1-based).</param>
public sealed record ItemEnchantmentInfo(string EnchantmentId, int Level);

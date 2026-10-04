using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Game.Inventory;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>changeslot</c> command: select a hotbar slot (1-9).</summary>
public sealed class ChangeSlotCommand : CommandBase
{
    private static readonly string[] SlotNumbers =
        [.. Enumerable.Range(1, 9).Select(static i => i.ToString(CultureInfo.InvariantCulture))];

    /// <inheritdoc/>
    public override string CmdName => "changeslot";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.ChangeSlotDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "changeslot <1-9>|<item>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<1-9>", "select a hotbar slot by number"),
        new("<item>", "select the slot holding an item"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["changeslot 3", "changeslot diamond_pickaxe"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory", "useitem"];

    /// <inheritdoc/>
    public override string? ManTopic => "inventory";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            // ORDER IS LOAD-BEARING, for the reason DropItemCommand spells out: an item id is a resource location and a bare integer is a legal one, so the item node would swallow "changeslot 3" as "select whatever slot holds minecraft:3".
            // The slot number is registered first so the numeric form keeps winning, and a non-numeric token still reaches the item node because the slot parse fails on it.
            .ThenArgument("slot", DmcbkArguments.HotbarSlot(), h => h
                .Suggests(SuggestSlots)
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<int>("slot"))))
            .ThenArgument("item", DmcbkArguments.ItemType(), h => h
                .Executes(ctx => RunByItem(ctx.Source, ctx.GetArgument<Identifier>("item"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>The nine slot numbers, preserved from the legacy <c>HotbarSlotArgumentType.ListSuggestions</c>.</summary>
    private static ValueTask SuggestSlots(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (string slot in SlotNumbers)
        {
            if (slot.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(slot);
        }

        return ValueTask.CompletedTask;
    }

    private int Run(CommandContext ctx, int slot)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (slot is < 1 or > 9)
            return ctx.Result.Fail(CommandStrings.ChangeSlotOutOfRange);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        ctx.Run(ct => ctx.Game.Inventory.SelectHeldSlotAsync(slot - 1, ct));
        return ctx.Result.Ok(CommandStrings.ChangeSlotChanged(slot));
    }

    /// <summary>
    /// <c>changeslot &lt;item&gt;</c>: select the hotbar slot holding that item, and CYCLE on repeat.
    /// <para>
    /// The hotbar only.
    /// Selecting is what the held-slot packet does and it addresses one of nine slots, so an item sitting in the backpack has no slot to select; saying "no compass in the hotbar" is the difference between a user moving it down and a user hunting for a bug.
    /// </para>
    /// <para>
    /// Cycling is derived from where the hand IS rather than from a remembered index: the next match strictly after the selected slot, wrapping.
    /// That needs no state between invocations, which means it cannot go stale when the hotbar is rearranged, when another command moves the selection, or across a reconnect, and repeating the command still walks the matches in order.
    /// With one match it re-selects that slot, which is the honest answer to "switch to my compass" when there is exactly one compass.
    /// </para>
    /// </summary>
    private int RunByItem(CommandContext ctx, Identifier item)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        // A number that is not 1-9 lands HERE, because the slot argument is bounded at 1..9 and a bare integer is a legal resource location, so "changeslot 0" parses as the item minecraft:0.
        // Answering "No minecraft:0 in the hotbar" would be technically true and useless; the user typed a slot number and wants to be told which numbers exist.
        if (LooksLikeASlotNumber(item))
            return ctx.Result.Fail(CommandStrings.ChangeSlotOutOfRange);

        PlayerInventorySnapshot player = ctx.Run(ct => ctx.Game.Inventory.GetPlayerInventoryAsync(ct));
        int held = ctx.Run(ct => ctx.Game.Inventory.GetHeldSlotAsync(ct));
        string needle = item.ToString();

        var matches = new List<(int Hotbar, ItemStackInfo Stack)>();
        for (int slot = 0; slot < player.Slots.Count; slot++)
        {
            ItemStackInfo stack = player.Slots[slot];
            if (stack.IsEmpty
                || !InventoryRendering.IsHotbar(slot, player.Slots.Count, isPlayerWindow: true, out int position)
                || !ItemMatches(stack.ItemId, needle))
                continue;

            matches.Add((position, stack));
        }

        if (matches.Count == 0)
            return ctx.Result.Fail(CommandStrings.ChangeSlotNoSuchItem(needle));

        (int hotbar, ItemStackInfo chosen) = NextAfter(matches, held);
        ctx.Run(ct => ctx.Game.Inventory.SelectHeldSlotAsync(hotbar, ct));
        return ctx.Result.Ok(CommandStrings.ChangeSlotChangedTo(
            hotbar + 1, InventoryRendering.Describe(ctx.Translations, chosen)));
    }

    /// <summary>
    /// The first match strictly after <paramref name="held"/>, wrapping to the first.
    /// Pure, so the cycling rule is testable without an inventory: the matches arrive in hotbar order.
    /// </summary>
    internal static (int Hotbar, ItemStackInfo Stack) NextAfter(
        IReadOnlyList<(int Hotbar, ItemStackInfo Stack)> matches, int held)
    {
        ArgumentNullException.ThrowIfNull(matches);
        foreach ((int hotbar, ItemStackInfo stack) in matches)
        {
            if (hotbar > held)
                return (hotbar, stack);
        }

        return matches[0];
    }

    /// <summary>
    /// Whether an item id is really a mistyped slot number.
    /// No vanilla item has an all-digit path, so a digits-only id in the default namespace can only have come from someone typing a number.
    /// </summary>
    internal static bool LooksLikeASlotNumber(Identifier item)
    {
        if (!string.Equals(item.Namespace, "minecraft", StringComparison.Ordinal) || item.Path.Length == 0)
            return false;

        foreach (char c in item.Path)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    private static bool ItemMatches(string itemId, string needle)
        => Identifier.TryParse(itemId, out Identifier id) && IdentifierMatch.Matches(id, needle);
}

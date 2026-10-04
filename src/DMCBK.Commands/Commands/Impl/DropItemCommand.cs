using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Game.Inventory;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>dropitem</c> command.
/// The legacy grammar is preserved exactly: <c>dropitem &lt;item&gt;</c> drops every slot holding a matching item and reports it with legacy's own wording.
/// Two forms are ADDED at the user's request, because legacy had no way to throw what you are holding: a bare <c>dropitem</c> drops the whole stack in the SELECTED hotbar slot, and a trailing count drops that many items instead of the lot (<c>dropitem &lt;count&gt;</c> and <c>dropitem &lt;item&gt; &lt;count&gt;</c>).
/// <para>
/// Fixes the legacy DropItem.cs:48 bug that returned <c>FailNeedTerrain</c> for an inventory gate; it now returns <c>FailNeedInventory</c>.
/// Also restores the window choice the first port lost: legacy acted on the single OPEN CONTAINER when one was open and on the player window otherwise (DropItem.cs:37-45), while the port always used the player window.
/// That is a correctness bug and not cosmetics, because a click carries the ACTIVE window's slot indices (UMPK <c>InventoryActions.ClickAsync</c> sends <c>Inventory.OpenWindowId</c>), so player-window indices sent while a chest is open throw from the wrong slots.
/// UMPK exposes at most one open container, which is exactly legacy's "one container, use it" case.
/// </para>
/// </summary>
public sealed class DropItemCommand : CommandBase
{
    /// <summary>The player's own window, which is what legacy fell back to (DropItem.cs:45, :57).</summary>
    private const int PlayerWindowId = 0;

    /// <inheritdoc/>
    public override string CmdName => "dropitem";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.dropItem.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "dropitem [<item>] [<count>]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "drop the selected stack"),
        new("<count>", "drop that many from the selected slot"),
        new("<item> [count]", "drop items by name"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["dropitem", "dropitem cobblestone 64"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "inventory";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => DropHeld(ctx.Source, null))

            // ORDER IS LOAD-BEARING and was measured against this dispatcher, not assumed.
            // An item id is a resource location, and "5" is a perfectly legal one (minecraft:5), so the item node parses a bare integer too.
            // Brigadier keeps every child that parsed and prefers the first one registered when several consume the whole line, so with <item> first "dropitem 5" silently became "drop every minecraft:5".
            // Registering <count> first gives the integer form the win, and a non-integer token still falls through to <item> because the count parse fails on it.
            .ThenArgument("count", Count(), h => h
                .Executes(ctx => DropHeld(ctx.Source, ctx.GetArgument<int>("count"))))
            .ThenArgument("item", MccArguments.ItemType(), h => h
                .Executes(ctx => DropMatching(ctx.Source, ctx.GetArgument<Identifier>("item"), null))
                .ThenArgument("count", Count(), c => c
                    .Executes(ctx => DropMatching(
                        ctx.Source, ctx.GetArgument<Identifier>("item"), ctx.GetArgument<int>("count")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// How many items to throw.
    /// Bounded at the ARGUMENT so a nonsense count never needs a refusal message of its own: 0 and negatives simply do not parse as a count, and fall through to the item form.
    /// </summary>
    private static IArgumentType<int> Count() => Arguments.Integer(1, int.MaxValue);

    /// <summary>The window a drop acts on, plus the slots it holds.</summary>
    private readonly record struct DropTarget(int WindowId, IReadOnlyList<ItemStackInfo> Slots, bool IsPlayerWindow);

    private static DropTarget Target(CommandContext ctx)
    {
        OpenContainerSnapshot? container = ctx.Run(ct => ctx.Game.Inventory.GetOpenContainerAsync(ct));
        if (container is not null)
            return new DropTarget(container.WindowId, container.Slots, false);

        PlayerInventorySnapshot player = ctx.Run(ct => ctx.Game.Inventory.GetPlayerInventoryAsync(ct));
        return new DropTarget(PlayerWindowId, player.Slots, true);
    }

    /// <summary>
    /// Where the selected hotbar slot sits IN THE TARGET WINDOW.
    /// The hotbar is the last nine slots, and the player's own window carries the offhand after them, which must not shift the count: that is <see cref="InventoryRendering.IsHotbar"/>, the port of legacy <c>Container.IsHotbar</c>, and the index derived here is handed back to it for confirmation so the two can never drift apart.
    /// The window matters: the player's own 46-slot window puts the hotbar at 36-44, but with a chest open the same hotbar is at the end of the CHEST window instead (54-62 of its 63 slots), and that is the index the click has to carry.
    /// </summary>
    private static bool TryHotbarSlot(DropTarget target, int heldSlot, out int slot)
    {
        slot = target.Slots.Count - 9 - (target.IsPlayerWindow ? 1 : 0) + heldSlot;
        return heldSlot is >= 0 and <= 8
            && slot >= 0
            && slot < target.Slots.Count
            && InventoryRendering.IsHotbar(slot, target.Slots.Count, target.IsPlayerWindow, out int hotbar)
            && hotbar == heldSlot;
    }

    /// <summary>
    /// <c>dropitem</c> and <c>dropitem &lt;count&gt;</c>: throw from the SELECTED hotbar slot.
    /// A null count means the whole stack.
    /// An empty selected slot is refused with legacy's own empty-slot wording rather than reported as a success, which is what a "dropped it" line over an empty hand would be.
    /// </summary>
    private int DropHeld(CommandContext ctx, int? count)
    {
        // Bug fix: an inventory gate must report FailNeedInventory (legacy wrongly returned FailNeedTerrain).
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        DropTarget target = Target(ctx);
        int heldSlot = ctx.Run(ct => ctx.Game.Inventory.GetHeldSlotAsync(ct));
        if (!TryHotbarSlot(target, heldSlot, out int slot))
            // Defensive: every real window carries the 36 player slots, so a window with no room for a hotbar cannot occur.
            // It must still not be answered with a made-up slot index.
            return ctx.Result.Fail(McStrings.Format("cmd.inventory.not_exist", target.WindowId));

        ItemStackInfo stack = target.Slots[slot];
        if (stack.IsEmpty)
            return ctx.Result.Fail(McStrings.Format("cmd.inventory.no_item", slot));

        int wanted = count ?? stack.Count;
        if (wanted >= stack.Count)
        {
            // One throw-stack click, the same action legacy used for a whole slot.
            ctx.Run(ct => ctx.Game.Inventory.DropAsync(slot, wholeStack: true, ct));
            return ctx.Result.Ok(McStrings.Format("cmd.inventory.drop_stack", slot));
        }

        for (int i = 0; i < wanted; i++)
            ctx.Run(ct => ctx.Game.Inventory.DropAsync(slot, wholeStack: false, ct));

        return ctx.Result.Ok(wanted == 1
            ? McStrings.Format("cmd.inventory.drop", slot)
            : McStrings.Format("cmd.dropItem.dropped", Batch(wanted, stack.ItemId), target.WindowId));
    }

    /// <summary>
    /// <c>dropitem &lt;item&gt;</c> (legacy) and <c>dropitem &lt;item&gt; &lt;count&gt;</c> (new): throw matching items from the target window, in slot order.
    /// With no count this is legacy's behaviour unchanged, one throw-stack click per matching slot; with a count it stops as soon as that many items have gone, taking whole stacks while it can and single items for the remainder.
    /// </summary>
    private int DropMatching(CommandContext ctx, Identifier item, int? count)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        DropTarget target = Target(ctx);
        string needle = item.ToString();

        int remaining = count ?? int.MaxValue;
        int dropped = 0;
        for (int slot = 0; slot < target.Slots.Count && remaining > 0; slot++)
        {
            ItemStackInfo stack = target.Slots[slot];
            if (stack.IsEmpty || !ItemMatches(stack.ItemId, needle))
                continue;

            int captured = slot;
            int take = Math.Min(remaining, stack.Count);
            if (take >= stack.Count)
                ctx.Run(ct => ctx.Game.Inventory.DropAsync(captured, wholeStack: true, ct));
            else
            {
                for (int i = 0; i < take; i++)
                    ctx.Run(ct => ctx.Game.Inventory.DropAsync(captured, wholeStack: false, ct));
            }

            dropped += take;
            remaining -= take;
        }

        if (dropped == 0)
            // Legacy printed its "dropped all" line even when it had found nothing at all; saying so is the one divergence kept here, and the first port already made it.
            return ctx.Result.Ok(CommandStrings.DropItemNone(needle));

        // "remaining > 0" after the loop means the window ran out of matches before the count did, so every matching item went and legacy's own "dropped all <item>" line is the true one.
        return ctx.Result.Ok(count is null || remaining > 0
            ? McStrings.Format("cmd.dropItem.dropped", needle, target.WindowId)
            : McStrings.Format("cmd.dropItem.dropped", Batch(dropped, needle), target.WindowId));
    }

    /// <summary>
    /// The subject of a PARTIAL drop, for the one message the legacy corpus cannot express on its own: it has "dropped all &lt;item&gt;" but nothing that carries a count of items.
    /// The count is folded into the subject with the corpus's own quantity marker (see <c>cmd.inventory.creative_done</c>, "{0} x{1}") so the line stays entirely corpus text, and it reads "Dropped all 3 x minecraft:stone from inventory #0": all of the batch that was asked for, which is what happened.
    /// </summary>
    private static string Batch(int count, string itemId)
        => string.Create(CultureInfo.CurrentCulture, $"{count} x {itemId}");

    private static bool ItemMatches(string itemId, string needle)
        => Identifier.TryParse(itemId, out Identifier id) && IdentifierMatch.Matches(id, needle);
}

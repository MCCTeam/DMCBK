using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Commands;
using Umpk.Game.Inventory;
using Umpk.Game.Items;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>inventory</c> command, restored to the legacy grammar and output.
/// <para>
/// The legacy command is built around ADDRESSING a window: <c>/inventory &lt;player|container|&lt;id&gt;&gt; &lt;action&gt;</c> (MinecraftClient/Commands/Inventory.cs:47-113).
/// The first port dropped that entirely, so <c>inventory player list</c>, <c>inventory 0 list</c> and <c>inventory &lt;id&gt; open</c> all fell through to the server as chat, <c>inventory inventories</c> printed nothing, and <c>search</c> looked only in the player window and omitted the item name.
/// Every one of those is a thing the user hit.
/// </para>
/// <para>
/// UMPK models at most one open container at a time, which is what vanilla allows, so the legacy "dictionary of inventories" is exactly {0: player} plus the open container when there is one.
/// That is what <see cref="Windows"/> materialises, and every action then addresses a window out of it.
/// </para>
/// </summary>
public sealed class InventoryCommand : CommandBase
{
    /// <summary>The player's own window. Vanilla's player inventory is always window 0.</summary>
    private const int PlayerWindowId = 0;

    private static readonly string[] ActionKeywords = ["left", "right", "middle", "shift", "shiftright"];

    /// <inheritdoc/>
    public override string CmdName => "inventory";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.inventory.desc");

    /// <inheritdoc/>
    /// <remarks>
    /// A GRAMMAR, not a sentence.
    /// <see cref="CommandBase.BuildUsage"/> prefixes this with the command char and suffixes it with the description, so the legacy sentence form rendered as <c>/Basic usage: /inventory &lt;player|container|&lt;id&gt;&gt; &lt;action&gt;.: Inventory command</c>.
    /// The sentence still exists as <see cref="BasicUsageLine"/> for the bare listing, which is the one place it was actually wanted.
    /// </remarks>
    public override string CmdUsage => "inventory <player|container|<id>> <action>";

    /// <summary>
    /// The prose one-liner the bare <c>/inventory</c> listing prints under the window ids (legacy's <c>GetBasicUsage</c>, Inventory.cs:466-469).
    /// Kept separate from <see cref="CmdUsage"/> because the two have different jobs: this is a sentence for a listing, that is a grammar for a help header.
    /// </summary>
    private static string BasicUsageLine =>
        $"{McStrings.Get("cmd.inventory.help.basic")}: /inventory <player|container|<id>> <action>.";

    /// <summary>One addressable window, in the shape the listing and the actions need.</summary>
    private sealed record Window(int Id, string Title, IReadOnlyList<ItemStackInfo> Slots, bool IsPlayer, string? MenuType);

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list open windows"),
        new("inventories", "list open windows by id"),
        new("player <action>", "act on your own inventory"),
        new("container <action>", "act on the open container"),
        new("<id> <action>", "act on a window by id"),
        new("search <item> [count]", "find an item you are carrying"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["inventory", "inventory player list", "inventory container click 3"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["changeslot", "dropitem", "recipebook"];

    /// <inheritdoc/>
    public override string? ManTopic => "inventory";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l =>
        {
            l.Executes(ctx => ListAllInventories(ctx.Source));

            l.ThenLiteral("inventories", h => h.Executes(ctx => ListAvailableInventories(ctx.Source)));

            l.ThenLiteral("search", h => h
                .ThenArgument("item", MccArguments.ItemType(), a => a
                    .Executes(ctx => SearchItem(ctx.Source, ctx.GetArgument<Identifier>("item"), null))
                    .ThenArgument("count", Arguments.Integer(1, 64), b => b
                        .Executes(ctx => SearchItem(
                            ctx.Source, ctx.GetArgument<Identifier>("item"), ctx.GetArgument<int>("count"))))));

            l.ThenLiteral("creativegive", h => h
                .ThenArgument("slot", MccArguments.InventorySlot(), a => a
                    .ThenArgument("item", MccArguments.ItemType(), b => b
                        .Executes(ctx => CreativeGive(ctx.Source, ctx.GetArgument<int>("slot"), ctx.GetArgument<Identifier>("item"), 1))
                        .ThenArgument("count", Arguments.Integer(1, 64), c => c
                            .Executes(ctx => CreativeGive(ctx.Source, ctx.GetArgument<int>("slot"), ctx.GetArgument<Identifier>("item"), ctx.GetArgument<int>("count")))))));

            l.ThenLiteral("creativedelete", h => h
                .ThenArgument("slot", MccArguments.InventorySlot(), a => a
                    .Executes(ctx => CreativeDelete(ctx.Source, ctx.GetArgument<int>("slot")))));

            // The three ways legacy lets you name a window.
            // 'player'/'p' is window 0, 'container'/'c' is the foreground container, and a bare integer is an explicit id.
            foreach (string alias in (string[])["player", "p"])
                l.ThenLiteral(alias, h => RegisterActions(h, _ => WindowSelector.Player));

            foreach (string alias in (string[])["container", "c"])
                l.ThenLiteral(alias, h => RegisterActions(h, _ => WindowSelector.Container));

            l.ThenArgument("id", MccArguments.InventoryId(), a =>
            {
                // Bare "/inventory <id>" is legacy's DoOpenOrList (Inventory.cs:64-65).
                a.Executes(ctx => OpenOrList(ctx.Source, WindowSelector.ById(ctx.GetArgument<int>("id"))));
                RegisterActions(a, ctx => WindowSelector.ById(ctx.GetArgument<int>("id")));
            });

            l.ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help));
        });
    }

    /// <summary>How a command line named the window it wants to act on.</summary>
    private readonly record struct WindowSelector(int? Id, bool ForceContainer)
    {
        public static WindowSelector Player => new(PlayerWindowId, false);

        /// <summary>The foreground container: legacy's GetMaximumInventoryId (Inventory.cs:135-142).</summary>
        public static WindowSelector Container => new(null, true);

        public static WindowSelector ById(int id) => new(id, false);
    }

    // The action subtree legacy hangs off every window selector (Inventory.cs:66-109).
    // Registered three times over so 'player', 'container' and '<id>' all accept the same verbs.
    private void RegisterActions(
        CommandNodeBuilder<CommandContext> node,
        Func<ICommandContext<CommandContext>, WindowSelector> selector)
    {
        node.ThenLiteral("list", h => h.Executes(ctx => ListWindow(ctx.Source, selector(ctx))));
        node.ThenLiteral("open", h => h.Executes(ctx => OpenWindow(ctx.Source, selector(ctx))));
        node.ThenLiteral("close", h => h.Executes(ctx => CloseWindow(ctx.Source, selector(ctx))));
        node.ThenLiteral("click", h => h
            .ThenArgument("slot", MccArguments.InventorySlot(), a => a
                .Executes(ctx => Click(ctx.Source, selector(ctx), ctx.GetArgument<int>("slot"), InventoryClickAction.Left))
                .ThenArgument("action", Arguments.Word(), b => b
                    .Suggests(SuggestActions)
                    .Executes(ctx => Click(
                        ctx.Source, selector(ctx), ctx.GetArgument<int>("slot"),
                        ParseAction(ctx.GetArgument<string>("action")))))));
        node.ThenLiteral("drop", h => h
            .ThenArgument("slot", MccArguments.InventorySlot(), a => a
                .Executes(ctx => Drop(ctx.Source, selector(ctx), ctx.GetArgument<int>("slot"), false))
                .ThenLiteral("all", b => b.Executes(ctx => Drop(ctx.Source, selector(ctx), ctx.GetArgument<int>("slot"), true)))));
    }

    /// <summary>
    /// Maps an inventory-action keyword to the enum.
    /// Ported from the legacy <c>InventoryActionArgumentType.Parse</c>.
    /// </summary>
    internal static InventoryClickAction ParseAction(string token) => token.ToLowerInvariant() switch
    {
        "left" or "leftclick" => InventoryClickAction.Left,
        "right" or "rightclick" => InventoryClickAction.Right,
        "mid" or "middle" or "middleclick" => InventoryClickAction.Middle,
        "shift" or "shiftclick" => InventoryClickAction.Shift,
        "shiftright" or "shiftrightclick" => InventoryClickAction.ShiftRight,
        _ => throw new ArgumentException(McStrings.Get("cmd.inventory.help.unknown"), nameof(token)),
    };

    private static ValueTask SuggestActions(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (string action in ActionKeywords)
        {
            if (action.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(action);
        }

        return ValueTask.CompletedTask;
    }

    private int Guard(CommandContext ctx)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        return int.MinValue; // continue
    }

    /// <summary>
    /// Every addressable window: the player's own, plus the open container when there is one.
    /// This is the new client's equivalent of legacy's <c>handler.GetInventories()</c>.
    /// </summary>
    private static List<Window> Windows(CommandContext ctx)
    {
        PlayerInventorySnapshot player = ctx.Run(ct => ctx.Game.Inventory.GetPlayerInventoryAsync(ct));
        var windows = new List<Window>
        {
            new(PlayerWindowId, InventoryRendering.PlayerWindowTitle, player.Slots, true, null),
        };

        OpenContainerSnapshot? container = ctx.Run(ct => ctx.Game.Inventory.GetOpenContainerAsync(ct));
        if (container is not null)
        {
            windows.Add(new Window(
                container.WindowId,
                container.Title ?? McStrings.Get("cmd.inventory.inventory"),
                container.Slots,
                false,
                container.MenuType ?? container.LegacyWindowType));
        }

        return windows;
    }

    private static Window? Resolve(CommandContext ctx, WindowSelector selector, out string? error)
    {
        List<Window> windows = Windows(ctx);
        error = null;

        if (selector.ForceContainer || selector.Id is null)
        {
            // Legacy takes the HIGHEST id as the foreground container and refuses when that is still the player window, i.e. nothing is open (Inventory.cs:135-142, :272-277).
            Window top = windows[^1];
            if (top.Id == PlayerWindowId)
            {
                error = McStrings.Get("cmd.inventory.container_not_found");
                return null;
            }

            return top;
        }

        foreach (Window w in windows)
        {
            if (w.Id == selector.Id.Value)
                return w;
        }

        error = McStrings.Format("cmd.inventory.not_exist", selector.Id.Value);
        return null;
    }

    /// <summary>
    /// The shared guard plus window lookup every window action runs before its own work.
    /// Returns the resolved window, or null with the failure result in <paramref name="failure"/>.
    /// The feature gate still runs before the session check inside <see cref="Guard"/>, the window is still resolved exactly once, and no exception is caught here, so snapshot failures and cancellation propagate exactly as before.
    /// </summary>
    private Window? TryResolveWindow(CommandContext ctx, WindowSelector selector, out int failure)
    {
        failure = Guard(ctx);
        if (failure != int.MinValue)
            return null;

        Window? window = Resolve(ctx, selector, out string? error);
        if (window is null)
        {
            failure = ctx.Result.Fail(error!);
            return null;
        }

        return window;
    }

    /// <summary>
    /// Bare <c>/inventory</c>: the SUMMARY, not a dump.
    /// Legacy prints the id and title of every window then the basic usage line (Inventory.cs:144-159).
    /// The first port dumped every slot of every window here, which is a different command.
    /// </summary>
    private int ListAllInventories(CommandContext ctx)
    {
        int guard = Guard(ctx);
        if (guard != int.MinValue)
            return guard;

        var sb = new StringBuilder();
        sb.Append(McStrings.Get("cmd.inventory.inventories")).Append(":\n");
        foreach (Window w in Windows(ctx))
            sb.Append(CultureInfo.CurrentCulture, $" #{w.Id}: {w.Title}§8\n");

        sb.Append(BasicUsageLine);
        ctx.Output.WriteLine(sb.ToString());
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// <c>/inventory inventories</c> (Inventory.cs:199-214).
    /// This printed nothing at all before: it returned the ids as a result message the host then had no reason to render.
    /// </summary>
    private int ListAvailableInventories(CommandContext ctx)
    {
        int guard = Guard(ctx);
        if (guard != int.MinValue)
            return guard;

        var sb = new StringBuilder();
        sb.Append(McStrings.Get("cmd.inventory.inventories_available")).Append('\n');
        foreach (Window w in Windows(ctx))
            sb.Append(CultureInfo.CurrentCulture, $" #{w.Id} - {w.Title}§8\n");

        ctx.Output.WriteLine(sb.ToString().TrimEnd('\n'));
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>Legacy's DoOpenOrList (Inventory.cs:64-65): the player window lists, a container opens.</summary>
    private int OpenOrList(CommandContext ctx, WindowSelector selector)
        => selector.Id == PlayerWindowId ? ListWindow(ctx, selector) : OpenWindow(ctx, selector);

    /// <summary>
    /// <c>/inventory &lt;id&gt; list</c> (Inventory.cs:294-334): the header, the container-layout diagram, one line per non-empty slot with its hotbar column, and the selected-hotbar footer for the player window.
    /// </summary>
    private int ListWindow(CommandContext ctx, WindowSelector selector)
    {
        Window? window = TryResolveWindow(ctx, selector, out int failure);
        if (window is null)
            return failure;

        int heldSlot = ctx.Run(ct => ctx.Game.Inventory.GetHeldSlotAsync(ct));

        var sb = new StringBuilder();
        sb.Append(McStrings.Get("cmd.inventory.inventory"));
        sb.Append(CultureInfo.CurrentCulture, $" #{window.Id} - {window.Title}§8\n");

        string? art = ctx.Ui?.GetInventoryLayout(window.MenuType, window.IsPlayer);
        if (art is not null)
            sb.Append(art).Append('\n');

        for (int slot = 0; slot < window.Slots.Count; slot++)
        {
            ItemStackInfo stack = window.Slots[slot];
            if (stack.IsEmpty)
                continue;

            string hotbar = InventoryRendering.HotbarColumn(slot, window.Slots.Count, window.IsPlayer, heldSlot);
            sb.Append(CultureInfo.CurrentCulture,
                $"{hotbar,2} | #{slot,-2}: {InventoryRendering.Describe(ctx.Translations, stack)}\n");
        }

        if (window.IsPlayer)
            sb.Append(McStrings.Format("cmd.inventory.hotbar", heldSlot + 1));

        ctx.Output.WriteLine(sb.ToString().TrimEnd('\n'));
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// <c>/inventory &lt;id&gt; open</c>: hands the window to the host's interactive viewer.
    /// This takes a WINDOW now.
    /// Before, <c>open</c> took no argument and bailed out unless a container was already open, so the player's own inventory could never be opened in the TUI at all.
    /// </summary>
    private int OpenWindow(CommandContext ctx, WindowSelector selector)
    {
        Window? window = TryResolveWindow(ctx, selector, out int failure);
        if (window is null)
            return failure;

        if (ctx.Ui is null || !ctx.Ui.TryOpenContainerView(window.Id))
            // No rich host (classic console), so say which command does work here rather than failing mute.
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.tui_only"));

        ctx.Output.WriteLine(McStrings.Format("cmd.inventory.tui_opening", window.Id));
        return ctx.Result.Ok(McStrings.Get("cmd.inventory.tui_opened"));
    }

    private int CloseWindow(CommandContext ctx, WindowSelector selector)
    {
        Window? window = TryResolveWindow(ctx, selector, out int failure);
        if (window is null)
            return failure;

        try
        {
            ctx.Run(ct => ctx.Game.Inventory.CloseAsync(ct));
            return ctx.Result.Ok(McStrings.Format("cmd.inventory.close", window.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(McStrings.Format("cmd.inventory.close_fail", window.Id));
        }
    }

    private int Click(CommandContext ctx, WindowSelector selector, int slot, InventoryClickAction action)
    {
        Window? window = TryResolveWindow(ctx, selector, out int failure);
        if (window is null)
            return failure;

        try
        {
            switch (action)
            {
                case InventoryClickAction.Left:
                    ctx.Run(ct => ctx.Game.Inventory.PickupAsync(slot, MouseButton.Left, ct));
                    break;
                case InventoryClickAction.Right:
                    ctx.Run(ct => ctx.Game.Inventory.PickupAsync(slot, MouseButton.Right, ct));
                    break;
                case InventoryClickAction.Middle:
                    ctx.Run(ct => ctx.Game.Inventory.PickupAsync(slot, MouseButton.Middle, ct));
                    break;
                default:
                    ctx.Run(ct => ctx.Game.Inventory.QuickMoveAsync(slot, ct));
                    break;
            }

            return ctx.Result.Ok(McStrings.Format(
                "cmd.inventory.clicking", ActionName(action), slot, window.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.shiftclick_fail"));
        }
    }

    private static string ActionName(InventoryClickAction action) => action switch
    {
        InventoryClickAction.Left => McStrings.Get("cmd.inventory.left"),
        InventoryClickAction.Right => McStrings.Get("cmd.inventory.right"),
        InventoryClickAction.Middle => McStrings.Get("cmd.inventory.middle"),
        InventoryClickAction.Shift => McStrings.Get("cmd.inventory.shiftclick"),
        _ => McStrings.Get("cmd.inventory.shiftrightclick"),
    };

    /// <summary>
    /// <c>/inventory &lt;id&gt; drop &lt;slot&gt; [all]</c>.
    /// Legacy refuses when the slot is empty (Inventory.cs:386-389); the first port dropped that check and reported success either way.
    /// </summary>
    private int Drop(CommandContext ctx, WindowSelector selector, int slot, bool wholeStack)
    {
        Window? window = TryResolveWindow(ctx, selector, out int failure);
        if (window is null)
            return failure;

        if (slot < 0 || slot >= window.Slots.Count || window.Slots[slot].IsEmpty)
            return ctx.Result.Fail(McStrings.Format("cmd.inventory.no_item", slot));

        ctx.Run(ct => ctx.Game.Inventory.DropAsync(slot, wholeStack, ct));
        return ctx.Result.Ok(McStrings.Format(
            wholeStack ? "cmd.inventory.drop_stack" : "cmd.inventory.drop", slot));
    }

    /// <summary>
    /// <c>/inventory search &lt;item&gt; [count]</c> (Inventory.cs:216-269).
    /// Searches EVERY window, groups hits by window, and prints the item itself.
    /// Before, it searched only the player window, hardcoded the window id to 0, and printed a line with no item name in it at all.
    /// </summary>
    private int SearchItem(CommandContext ctx, Identifier item, int? count)
    {
        int guard = Guard(ctx);
        if (guard != int.MinValue)
            return guard;

        string needle = item.ToString();
        var hits = new List<(Window Window, List<(int Slot, ItemStackInfo Stack)> Items)>();

        foreach (Window w in Windows(ctx))
        {
            // Indexed, not iterated: a hit is only actionable if you know WHICH slot it is in, and the slot index is what every other inventory subcommand takes.
            // Reporting the item alone left the reader to run `inventory <id> list` and find it again by eye.
            var found = new List<(int Slot, ItemStackInfo Stack)>();
            for (int slot = 0; slot < w.Slots.Count; slot++)
            {
                ItemStackInfo stack = w.Slots[slot];
                if (!stack.IsEmpty && Matches(stack.ItemId, needle) && (count is null || stack.Count == count))
                    found.Add((slot, stack));
            }

            if (found.Count > 0)
                hits.Add((w, found));
        }

        if (hits.Count == 0)
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.no_found_items"));

        int heldSlot = ctx.Run(ct => ctx.Game.Inventory.GetHeldSlotAsync(ct));

        var sb = new StringBuilder();
        sb.Append(McStrings.Get("cmd.inventory.found_items")).Append(":\n");
        foreach ((Window w, List<(int Slot, ItemStackInfo Stack)> items) in hits)
        {
            sb.Append(CultureInfo.CurrentCulture, $"{w.Title} (#{w.Id}):\n");
            foreach ((int slot, ItemStackInfo stack) in items)
            {
                // The same columns `inventory <id> list` prints, so a hit here can be read against that listing without translating between two layouts: the hotbar marker (with '>' on the selected slot), then the slot index, then the item.
                string hotbar = InventoryRendering.HotbarColumn(slot, w.Slots.Count, w.IsPlayer, heldSlot);
                sb.Append(CultureInfo.CurrentCulture,
                    $"{hotbar,2} | #{slot,-2}: {InventoryRendering.Describe(ctx.Translations, stack)}\n");
            }

            sb.Append(" \n");
        }

        ctx.Output.WriteLine(sb.ToString().TrimEnd('\n'));
        return ctx.Result.Set(CmdStatus.Done);
    }

    private int CreativeGive(CommandContext ctx, int slot, Identifier item, int count)
    {
        int guard = Guard(ctx);
        if (guard != int.MinValue)
            return guard;

        PlayerStatus status = ctx.Run(ct => ctx.Game.Player.GetStatusAsync(ct));
        if (!string.Equals(status.GameMode, "Creative", StringComparison.OrdinalIgnoreCase))
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.need_creative"));

        string itemId = item.ToString();
        try
        {
            ctx.Run(ct => ctx.Game.Inventory.CreativeGiveAsync(slot, itemId, count, ct));
            return ctx.Result.Ok(McStrings.Format("cmd.inventory.creative_done", itemId, count, slot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.creative_fail"));
        }
    }

    private int CreativeDelete(CommandContext ctx, int slot)
    {
        int guard = Guard(ctx);
        if (guard != int.MinValue)
            return guard;

        PlayerStatus status = ctx.Run(ct => ctx.Game.Player.GetStatusAsync(ct));
        if (!string.Equals(status.GameMode, "Creative", StringComparison.OrdinalIgnoreCase))
            return ctx.Result.Fail(McStrings.Get("cmd.inventory.need_creative"));

        ctx.Run(ct => ctx.Game.Inventory.CreativeSetSlotAsync(slot, ItemStack.Empty, ct));
        return ctx.Result.Ok(McStrings.Format("cmd.inventory.creative_delete", slot));
    }

    private static bool Matches(string itemId, string needle)
        => Identifier.TryParse(itemId, out Identifier id) && IdentifierMatch.Matches(id, needle);
}

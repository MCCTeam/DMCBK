using Umpk.Client;
using Umpk.Client.Actions;
using Umpk.Game.Inventory;
using Umpk.Game.Items;
using Umpk.Geometry;

namespace DMCBK.Core;

/// <summary>
/// The inventory/container surface: player-inventory and open-container snapshots, container clicks (pickup/quick-move/swap/clone/throw), creative slot set, close, trade selection, held-slot get/select, and the cursor stack.
/// Requires the Inventory feature; disabled features surface <see cref="MccFeatureDisabledException"/>.
///
/// <para>
/// Also the host-facing notice that a window opened or closed (<see cref="ContainerOpened"/>/<see cref="ContainerClosed"/>), which is what lets a host announce a chest the way the legacy client did and show it without being asked.
/// </para>
/// </summary>
public sealed class InventoryApi
{
    private readonly GameSession _session;
    private readonly Umpk.Text.ITranslationSource? _translations;
    private readonly object _gate = new();
    private IDisposable? _openedSubscription;
    private IDisposable? _closedSubscription;
    private UmpkClient? _client;

    internal InventoryApi(GameSession session, Umpk.Text.ITranslationSource? translations)
    {
        _session = session;
        _translations = translations;
    }

    /// <summary>
    /// Raised when the server opens a container window for this client, carrying the id and the resolved title so a host can announce it without a round trip back into the session.
    /// </summary>
    /// <remarks>
    /// Raised on the session loop, like every other UMPK-sourced notification, so a handler must not block on session work; hand anything slow to a background task.
    /// The legacy client's equivalent hook is <c>McClient.OnInventoryOpen</c> (McClient.cs:3856), which printed the window, said which command operates on it, and opened the TUI view.
    /// </remarks>
    public event EventHandler<ContainerOpenedEventArgs>? ContainerOpened;

    /// <summary>Raised when the server closes a container window. See <see cref="ContainerOpened"/>.</summary>
    public event EventHandler<ContainerClosedEventArgs>? ContainerClosed;

    /// <summary>
    /// Subscribes to one session's container notifications.
    /// Per session, like <c>ChatApi.Bind</c>, because a reconnect builds a whole new <see cref="UmpkClient"/> and the previous subscription dies with it.
    /// </summary>
    internal void Bind(UmpkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            _client = client;
            _openedSubscription = client.Events.Subscribe<Umpk.Client.Events.ContainerOpened>(OnOpened);
            _closedSubscription = client.Events.Subscribe<Umpk.Client.Events.ContainerClosed>(OnClosed);
        }
    }

    /// <inheritdoc cref="Bind"/>
    internal void Unbind()
    {
        lock (_gate)
        {
            _openedSubscription?.Dispose();
            _openedSubscription = null;
            _closedSubscription?.Dispose();
            _closedSubscription = null;
            _client = null;
        }
    }

    /// <summary>
    /// Reads the title off the live state rather than re-entering the session for a snapshot.
    /// Internal rather than private only so a test can drive the notice without a live server standing behind it.
    /// </summary>
    /// <remarks>
    /// UMPK publishes this event from the applier that just stored the window, on the session loop, so the state read here is the very window being announced.
    /// Asking <see cref="GetOpenContainerAsync"/> for it instead would be a round trip that can only ever return the same thing or something newer, and on the loop it would deadlock.
    /// </remarks>
    internal void OnOpened(Umpk.Client.Events.ContainerOpened opened)
    {
        EventHandler<ContainerOpenedEventArgs>? handler = ContainerOpened;
        if (handler is null)
            return;

        UmpkClient? client;
        lock (_gate)
            client = _client;

        string title = string.Empty;
        try
        {
            title = client?.State.Inventory.OpenContainerTitle?.ToPlainText(_translations) ?? string.Empty;
        }
        catch (FeatureDisabledException)
        {
            // Inventory tracking off: the id is still worth announcing, the title simply is not known.
        }

        handler(this, new ContainerOpenedEventArgs(opened.WindowId, title));
    }

    internal void OnClosed(Umpk.Client.Events.ContainerClosed closed)
        => ContainerClosed?.Invoke(this, new ContainerClosedEventArgs(closed.WindowId));

    /// <summary>Snapshots the player inventory (all 46 slots, held slot, cursor, and state id).</summary>
    public Task<PlayerInventorySnapshot> GetPlayerInventoryAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.PlayerInventorySnapshot snapshot =
                await client.Snapshots.PlayerInventoryAsync(ct).ConfigureAwait(false);
            return new PlayerInventorySnapshot(
                ProjectSlots(snapshot.Slots), snapshot.HeldSlot, ItemStackInfo.From(snapshot.Cursor, _translations), snapshot.StateId);
        });

    /// <summary>
    /// Snapshots the currently open container (window id, slots, properties, trades), or null when only the player inventory is open.
    /// </summary>
    public Task<OpenContainerSnapshot?> GetOpenContainerAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.OpenContainerSnapshot? snapshot =
                await client.Snapshots.OpenContainerAsync(ct).ConfigureAwait(false);
            if (snapshot is not { } container)
                return null;

            IReadOnlyList<TradeOfferInfo>? trades = container.Trades is { } merchant ? ProjectTrades(merchant) : null;
            return new OpenContainerSnapshot(
                container.WindowId, ProjectSlots(container.Slots), container.Properties,
                ItemStackInfo.From(container.Cursor, _translations), container.StateId, trades, container.MenuTypeId,
                container.Title?.ToPlainText(_translations), container.MenuType?.ToString(), container.LegacyWindowType);
        });

    /// <summary>The current cursor (carried) stack.</summary>
    public Task<ItemStackInfo> GetCursorAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            ItemStack cursor = await client.Snapshots.CursorAsync(ct).ConfigureAwait(false);
            return ItemStackInfo.From(cursor, _translations);
        });

    /// <summary>The selected hotbar slot (0-8).</summary>
    public Task<int> GetHeldSlotAsync(CancellationToken ct = default)
        => _session.ReadAsync(client => client.State.Self.HeldSlot, ct);

    /// <summary>Selects the active hotbar slot (0-8).</summary>
    public Task SelectHeldSlotAsync(int slot, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.SelectHeldSlotAsync(slot, ct));

    /// <summary>Performs a raw container click through the prediction pipeline.</summary>
    public Task ClickAsync(ClickAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _session.RunAsync(client => client.Actions.Inventory.ClickAsync(action, ct));
    }

    /// <summary>Picks up (or places) the stack in a slot with the given mouse button.</summary>
    public Task PickupAsync(int slot, MouseButton button = MouseButton.Left, CancellationToken ct = default)
        => ClickAsync(new ClickAction.Pickup(slot, button), ct);

    /// <summary>Shift-clicks (quick-moves) a slot.</summary>
    public Task QuickMoveAsync(int slot, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.QuickMoveAsync(slot, ct));

    /// <summary>Swaps a slot with a hotbar slot (number-key press).</summary>
    public Task SwapHotbarAsync(int slot, int hotbarButton, CancellationToken ct = default)
        => ClickAsync(new ClickAction.Swap(slot, hotbarButton), ct);

    /// <summary>Drops (throws) from a slot; <paramref name="wholeStack"/> drops the entire stack.</summary>
    public Task DropAsync(int slot, bool wholeStack = false, CancellationToken ct = default)
        => ClickAsync(new ClickAction.Throw(slot, wholeStack), ct);

    /// <summary>Sets a creative-mode slot directly to the given stack (advanced; requires a bound item handle).</summary>
    public Task CreativeSetSlotAsync(int slot, ItemStack item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _session.RunAsync(client => client.Actions.Inventory.CreativeSetSlotAsync(slot, item, ct));
    }

    /// <summary>
    /// Convenience creative give: resolves <paramref name="itemId"/> against the session registries and sets the slot to that item at <paramref name="count"/>.
    /// Throws when the item id is unknown.
    /// </summary>
    public Task CreativeGiveAsync(int slot, string itemId, int count = 1, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        Umpk.Identifier id = Umpk.Identifier.Parse(itemId);
        return _session.RunAsync(client =>
        {
            Umpk.Game.Registries.RegistryAccess registries = client.State.Registries
                ?? throw new InvalidOperationException("No registries are available for this session.");
            if (!registries.Items.TryGet(id, out Umpk.Game.Registries.RegistryEntry<Umpk.Game.Registries.ItemDefinition> entry))
                throw new ArgumentException($"Unknown item id '{itemId}'.", nameof(itemId));

            var stack = new ItemStack(entry, count);
            return client.Actions.Inventory.CreativeSetSlotAsync(slot, stack, ct);
        });
    }

    /// <summary>Selects a merchant trade by index (villager/wandering-trader menus).</summary>
    public Task SelectTradeAsync(int tradeIndex, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.SelectTradeAsync(tradeIndex, ct));

    /// <summary>
    /// Clicks a window button (<c>container_button_click</c>): the enchant-table option select, lectern page turn, and similar.
    /// Defaults to the open window.
    /// </summary>
    public Task ClickContainerButtonAsync(int buttonId, int? windowId = null, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.ClickContainerButtonAsync(buttonId, windowId, ct));

    /// <summary>
    /// Whether the negotiated version can send the anvil rename at all, by either wire route: the dedicated <c>rename_item</c> packet from 1.13, or the <c>MC|ItemName</c> plugin channel on 1.8-1.12.2, which is the route vanilla itself uses there.
    /// True on every supported version.
    /// </summary>
    public Task<bool> IsRenameSupportedAsync(CancellationToken ct = default)
        => _session.ReadAsync(client => client.Capabilities.CanRenameItem, ct);

    /// <summary>
    /// Renames the item in an open anvil.
    /// UMPK picks the route by era: the dedicated <c>rename_item</c> packet on 1.13+, the <c>MC|ItemName</c> plugin channel on 1.8-1.12.2. Returns true when the rename was sent and false when this version can carry neither route, in which case nothing was sent.
    /// A caller that ignores this result would report a rename that never happened.
    /// <para>
    /// The legacy route inherits vanilla's server-side name-length cap, which silently ignores an over-long name: 30 characters on 1.8-1.11.2, 35 on 1.12-1.12.2. A true result means the frame went out, not that the server accepted the name.
    /// </para>
    /// </summary>
    public Task<bool> RenameItemAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _session.RunAsync(client => client.Actions.Inventory.TryRenameItemAsync(name, ct));
    }

    /// <summary>
    /// Whether the negotiated version can send the dedicated book-edit packet.
    /// False below 1.13 (book editing used the <c>MC|BEdit</c>/<c>MC|BSign</c> plugin channel) and false on 1.13 through 1.16.5, where the wire id belongs to a different packet record than the one UMPK's send builds.
    /// </summary>
    public Task<bool> IsBookEditSupportedAsync(CancellationToken ct = default)
        => _session.ReadAsync(client => client.Capabilities.CanEditBook, ct);

    /// <summary>
    /// Writes (or signs) a book and quill in a hotbar slot (<c>edit_book</c>; 1.17+).
    /// A non-null <paramref name="title"/> signs the book.
    /// Returns TRUE when the edit was sent and FALSE when this version cannot send it, in which case nothing was sent (see <see cref="IsBookEditSupportedAsync"/>).
    /// </summary>
    public Task<bool> EditBookAsync(
        int slot, IReadOnlyList<string> pages, string? title = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return _session.RunAsync(client => client.Actions.Inventory.TryEditBookAsync(slot, pages, title, ct));
    }

    /// <summary>
    /// Which recipe-book placement form the negotiated version actually uses, plus the version it was read from.
    /// There is exactly ONE live form per version and the wire decides which, so a caller must branch on this rather than on the shape of whatever the user typed.
    /// </summary>
    public Task<RecipePlacementSupport> GetRecipePlacementAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Snapshots.RecipePlacementAsync(ct));

    /// <summary>
    /// Plans and sends one recipe-book placement using the form supported by the negotiated protocol.
    /// This is the shared high-level operation for commands and rich hosts: callers receive a truthful typed outcome instead of having to reproduce version-form and open-menu gates.
    /// </summary>
    public async Task<RecipePlacementOutcome> PlaceRecipeAsync(
        string recipe, bool makeAll = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);

        string trimmed = recipe.Trim();
        RecipePlacementSupport placement = await GetRecipePlacementAsync(ct).ConfigureAwait(false);
        RecipeCraftPlan plan = RecipeCraftPlan.Plan(placement, trimmed);
        if (!plan.CanPlace)
        {
            return new RecipePlacementOutcome(
                RecipePlacementOutcomeKind.UnsupportedRecipeForm,
                trimmed,
                placement,
                plan.Refusal);
        }

        OpenContainerSnapshot? container = await GetOpenContainerAsync(ct).ConfigureAwait(false);
        if (container is null)
        {
            return new RecipePlacementOutcome(
                RecipePlacementOutcomeKind.MissingMenu,
                trimmed,
                placement,
                RecipeCraftRefusal.None);
        }

        bool sent = plan.Form == RecipePlacementForm.NetworkId
            ? await PlaceRecipeAsync(plan.NetworkId, makeAll, container.WindowId, ct).ConfigureAwait(false)
            : await PlaceRecipeByNameAsync(plan.RecipeId!.Value, makeAll, container.WindowId, ct).ConfigureAwait(false);

        string placed = plan.Form == RecipePlacementForm.NetworkId
            ? plan.NetworkId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : plan.RecipeId!.Value.ToString();

        return new RecipePlacementOutcome(
            sent ? RecipePlacementOutcomeKind.Success : RecipePlacementOutcomeKind.Rejected,
            placed,
            placement,
            RecipeCraftRefusal.None);
    }

    /// <summary>
    /// Places a recipe into the open crafting-style menu by its network recipe-display id (<c>place_recipe</c>; 1.21.2+).
    /// <paramref name="makeAll"/> requests the maximum stack.
    /// Returns TRUE when the request was sent and FALSE when the network-id form is not the version's live form, in which case NOTHING was sent: ask <see cref="GetRecipePlacementAsync"/> to branch beforehand.
    /// </summary>
    public Task<bool> PlaceRecipeAsync(
        int recipeNetworkId, bool makeAll = false, int? windowId = null, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.TryPlaceRecipeAsync(recipeNetworkId, makeAll, windowId, ct));

    /// <summary>
    /// Places a recipe into the open crafting-style menu by its resource location (<c>place_recipe</c> name form; 1.13 through 1.21.1).
    /// <paramref name="makeAll"/> requests the maximum stack.
    /// Returns TRUE when the request was sent and FALSE when the by-name form is not the version's live form, in which case NOTHING was sent: ask <see cref="GetRecipePlacementAsync"/> to branch beforehand.
    /// </summary>
    public Task<bool> PlaceRecipeByNameAsync(
        Umpk.Identifier recipe, bool makeAll = false, int? windowId = null, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.TryPlaceRecipeByNameAsync(recipe, makeAll, windowId, ct));

    /// <summary>
    /// Snapshots the recipe book: the unlocked recipes the wire named by resource location, the ones it named by number, the four per-book open/filtering flags, and the count of additions this build keeps opaque.
    /// See <see cref="RecipeBookSnapshot.OpaqueAdditions"/> for why a caller must not read an empty recipe list as "nothing is unlocked".
    /// </summary>
    public Task<RecipeBookSnapshot> GetRecipeBookAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.RecipeBookSnapshot recipes = await client.Snapshots.RecipeBookAsync(ct).ConfigureAwait(false);
            var names = new List<string>(recipes.Recipes.Count);
            foreach (Umpk.Identifier recipe in recipes.Recipes)
                names.Add(recipe.ToString());

            var books = new List<RecipeBookInfo>(recipes.Books.Count);
            foreach (Umpk.Client.Snapshots.RecipeBookFlags book in recipes.Books)
                books.Add(new RecipeBookInfo(book.Open, book.Filtering));

            return new RecipeBookSnapshot(names, recipes.RecipeIds, books, recipes.OpaqueAdditions, recipes.Revision);
        });

    /// <summary>Closes the active container.</summary>
    public Task CloseAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Inventory.CloseAsync(ct));

    /// <summary>
    /// Uses the block at a position (right-click against its top face) and waits for the server to open a window (about 2 seconds).
    /// Returns the window snapshot, or null when no window appears in time.
    /// Completion of the send is not the effect: a use is whatever the target block decides it is, so only an observed window counts as opened and a timeout honestly reports none instead of claiming a container.
    /// </summary>
    /// <remarks>
    /// The wait is event-driven on purpose: the send runs on the session loop, but the window notice arrives through <see cref="ContainerOpened"/>, so polling a snapshot from inside the send delegate would nest a loop marshal inside itself.
    /// The handler below only sets the signal, per the rule on <see cref="OnOpened"/>.
    /// </remarks>
    /// <remarks>
    /// Both bounds sit well under the 5 s dispatch wall clock on purpose (see <c>BeaconBudgetLimits.MaxWallClock</c>): a host await consumes the handler's compute window while suspended, so a use that waited the full budget could never report back.
    /// Windows open in milliseconds on a live server; the bounds only bite on quiet ones.
    /// </remarks>
    public async Task<OpenContainerSnapshot?> OpenContainerAtAsync(BlockPos position, CancellationToken ct = default)
    {
        var opened = new TaskCompletionSource<ContainerOpenedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ContainerOpenedEventArgs>? handler = (_, args) => opened.TrySetResult(args);
        ContainerOpened += handler;
        try
        {
            // Fractional face-center cursor (0..1 in-block, like WorldCommandText.FaceHitCursor): the wire codec documents CursorX/Y/Z as in-block fractions, and an absolute position here is acked but silently ignored by the server.
            // Top face, like before.
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using CancellationTokenSource sendLinked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, sendTimeout.Token);
            try
            {
                await _session.RunAsync(client => client.Actions.Interaction
                    .PlaceBlockAsync(
                        position,
                        Direction.Up,
                        new Vec3d(0.5, 1.0, 0.5),
                        Hand.Main,
                        false,
                        sendLinked.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await opened.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            return await GetOpenContainerAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ContainerOpened -= handler;
        }
    }

    private IReadOnlyList<ItemStackInfo> ProjectSlots(IReadOnlyList<ItemStack> slots)
    {
        var list = new List<ItemStackInfo>(slots.Count);
        foreach (ItemStack slot in slots)
            list.Add(ItemStackInfo.From(slot, _translations));

        return list;
    }

    private IReadOnlyList<TradeOfferInfo> ProjectTrades(MerchantOffers merchant)
    {
        var trades = new List<TradeOfferInfo>(merchant.Offers.Count);
        foreach (MerchantOffer offer in merchant.Offers)
        {
            trades.Add(new TradeOfferInfo(
                ItemStackInfo.From(offer.AdjustedFirstCost, _translations),
                offer.SecondCost is { } second ? ItemStackInfo.From(second, _translations) : null,
                ItemStackInfo.From(offer.Result, _translations),
                offer.Uses,
                offer.MaxUses,
                offer.IsSoldOut,
                offer.Xp));
        }

        return trades;
    }
}

/// <summary>A snapshot of the player inventory.</summary>
/// <param name="Slots">The 46 player-inventory slots (crafting/armor/main/hotbar/offhand).</param>
/// <param name="HeldSlot">The selected hotbar slot (0-8).</param>
/// <param name="Cursor">The cursor (carried) stack.</param>
/// <param name="StateId">The last authoritative window state id.</param>
public sealed record PlayerInventorySnapshot(
    IReadOnlyList<ItemStackInfo> Slots, int HeldSlot, ItemStackInfo Cursor, int StateId);

/// <summary>A snapshot of the currently open container, including its semantic menu type.</summary>
/// <param name="WindowId">The open window id.</param>
/// <param name="Slots">The container's slots.</param>
/// <param name="Properties">The window's typed properties (furnace progress, enchant levels, ...) keyed by id.</param>
/// <param name="Cursor">The cursor (carried) stack.</param>
/// <param name="StateId">The last authoritative window state id.</param>
/// <param name="Trades">Merchant trade offers when the window is a merchant, else null.</param>
/// <param name="MenuTypeId">
/// The container's menu-type network id, or -1 when no container is open OR the version predates the menu registry (1.8 through 1.13.2, which name the window with a string).
/// Version-specific; prefer <paramref name="MenuType"/>.
/// </param>
/// <param name="Title">The container's rendered title, or null.</param>
/// <param name="MenuType">
/// The container's SEMANTIC type: the resolved <c>minecraft:menu</c> registry key (<c>minecraft:furnace</c>, <c>minecraft:generic_9x3</c>, ...), which means the same thing on every supported version.
/// Null when no container is open or the window kind could not be named.
/// UMPK resolves it from the numeric menu id on 1.14+ and from the window-type string before that, so a consumer can switch on the kind instead of guessing it from the slot count.
/// </param>
/// <param name="LegacyWindowType">
/// The raw pre-1.14 window-type string ("minecraft:chest", "EntityHorse", ...), or null on 1.14+ and when no container is open.
/// It is the authoritative wire key on that era and survives even when <paramref name="MenuType"/> could not name the window.
/// </param>
public sealed record OpenContainerSnapshot(
    int WindowId,
    IReadOnlyList<ItemStackInfo> Slots,
    IReadOnlyDictionary<int, int> Properties,
    ItemStackInfo Cursor,
    int StateId,
    IReadOnlyList<TradeOfferInfo>? Trades,
    int MenuTypeId,
    string? Title,
    string? MenuType = null,
    string? LegacyWindowType = null);

/// <summary>
/// A snapshot of the session's recipe book.
/// </summary>
/// <param name="Recipes">
/// The unlocked recipes the wire named by resource location.
/// Complete on 1.13 through 1.21.1; empty on 1.12-1.12.2 (that era names recipes by number) and on 1.21.2+ (see <paramref name="OpaqueAdditions"/>).
/// </param>
/// <param name="RecipeIds">
/// The unlocked recipes the wire named by number: crafting-manager ids on 1.12-1.12.2, recipe-display ids on 1.21.2+.
/// </param>
/// <param name="Books">The four recipe books' open/filtering flags: crafting, furnace, blast furnace, smoker.</param>
/// <param name="OpaqueAdditions">
/// How many 1.21.2+ recipe-book additions arrived but stayed undecoded, because that era's entry payload is a nested recipe-display tree UMPK keeps opaque.
/// A non-zero value means <paramref name="Recipes"/> and <paramref name="RecipeIds"/> are incomplete rather than genuinely empty, and a caller must say so.
/// </param>
/// <param name="Revision">The number of unlock changes applied so far.</param>
public sealed record RecipeBookSnapshot(
    IReadOnlyList<string> Recipes,
    IReadOnlyList<int> RecipeIds,
    IReadOnlyList<RecipeBookInfo> Books,
    int OpaqueAdditions,
    int Revision);

/// <summary>One recipe book's flags.</summary>
/// <param name="Open">Whether the book is open.</param>
/// <param name="Filtering">Whether the book filters to craftable recipes.</param>
public sealed record RecipeBookInfo(bool Open, bool Filtering);

/// <summary>The result category of a high-level recipe-book placement request.</summary>
public enum RecipePlacementOutcomeKind
{
    /// <summary>The placement packet was sent.</summary>
    Success,

    /// <summary>The entered identifier does not match the form this protocol can send.</summary>
    UnsupportedRecipeForm,

    /// <summary>No compatible container menu is currently open.</summary>
    MissingMenu,

    /// <summary>The version accepted the form but the placement action declined to send it.</summary>
    Rejected,
}

/// <summary>
/// A typed recipe placement result shared by the command and TUI.
/// <paramref name="Refusal"/> is populated when <paramref name="Kind"/> is <see cref="RecipePlacementOutcomeKind.UnsupportedRecipeForm"/>.
/// </summary>
public sealed record RecipePlacementOutcome(
    RecipePlacementOutcomeKind Kind,
    string Recipe,
    RecipePlacementSupport Support,
    RecipeCraftRefusal Refusal);

/// <summary>One villager/merchant trade offer.</summary>
/// <param name="FirstCost">The (price-adjusted) first input.</param>
/// <param name="SecondCost">The optional second input.</param>
/// <param name="Result">The output.</param>
/// <param name="Uses">Uses consumed so far.</param>
/// <param name="MaxUses">Uses before the offer is disabled.</param>
/// <param name="IsSoldOut">Whether the offer is currently sold out.</param>
/// <param name="Xp">Villager xp granted by the trade.</param>
public sealed record TradeOfferInfo(
    ItemStackInfo FirstCost,
    ItemStackInfo? SecondCost,
    ItemStackInfo Result,
    int Uses,
    int MaxUses,
    bool IsSoldOut,
    int Xp);

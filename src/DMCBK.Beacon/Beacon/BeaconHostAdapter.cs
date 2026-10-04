namespace DMCBK.Core.Beacon;

/// <summary>Chat sends available to Beacon scripts (the <c>say</c> / <c>whisper</c> verbs).</summary>
public interface IBeaconChatSink
{
    /// <summary>Sends public chat text.</summary>
    Task SayAsync(string text, CancellationToken ct = default);

    /// <summary>Sends a private message to <paramref name="player"/>.</summary>
    Task WhisperAsync(string player, string text, CancellationToken ct = default);
}

/// <summary>Command dispatch available to Beacon scripts (the <c>server</c> / <c>mcc</c> verbs).</summary>
public interface IBeaconCommandDispatcher
{
    /// <summary>Sends a raw server command line (leading slash required); returns server output text.</summary>
    Task<string> SendServerAsync(string commandLine, CancellationToken ct = default);

    /// <summary>Runs an MCC client command; returns output text, raises catchably on failure.</summary>
    Task<string> RunMccAsync(string commandLine, CancellationToken ct = default);
}

/// <summary>Session reads available to Beacon scripts (the <c>me.*</c> / <c>server.*</c> builtins).</summary>
public interface IBeaconSessionReads
{
    /// <summary>Own player name, or null outside a session.</summary>
    string? SelfName { get; }

    /// <summary>Up to <paramref name="limit"/> online player names (paged read).</summary>
    IReadOnlyList<string> OnlinePlayers(int limit);

    /// <summary>
    /// Exact online-player count without materializing the list.
    /// Defaults to counting a capped read; real adapters override with a cheap count so a 500-player hub reports exactly without becoming a self-DDoS.
    /// </summary>
    int OnlinePlayerCount => OnlinePlayers(500).Count;

    /// <summary>
    /// Up to <paramref name="limit"/> recent chat lines, oldest first (callers clamp to <c>BeaconReadBounds.ChatHistoryMax</c>).
    /// Defaults to empty when the host keeps no history.
    /// </summary>
    IReadOnlyList<string> ChatHistory(int limit) => [];

    /// <summary>
    /// One block read (single coordinates only, never bulk).
    /// Null means unknown or outside the loaded range, which the interpreter reports as a catchable B4011.
    /// </summary>
    BeaconBlockInfo? GetBlock(int x, int y, int z) => null;

    /// <summary>Server TPS estimate, or null when unknown.</summary>
    double? ServerTps { get; }
}

/// <summary>One block read result: display name plus numeric id.</summary>
/// <param name="Name">The block's display name.</param>
/// <param name="Id">The block's numeric id.</param>
public sealed record BeaconBlockInfo(string Name, int Id);

/// <summary>
/// One tracked entity for the <c>entities.*</c> builtins and the <c>entity_add</c>/<c>entity_remove</c> hooks.
/// A render-distance snapshot: ids are the server's and are not stable across reconnects.
/// </summary>
/// <param name="Id">The server-assigned entity id.</param>
/// <param name="Uuid">The entity uuid text.</param>
/// <param name="TypeId">The namespaced entity type id (for example <c>minecraft:zombie</c>).</param>
/// <param name="X">Position X.</param>
/// <param name="Y">Position Y.</param>
/// <param name="Z">Position Z.</param>
/// <param name="Yaw">Body yaw in degrees.</param>
/// <param name="Pitch">Pitch in degrees.</param>
/// <param name="Pose">The pose name.</param>
/// <param name="OnGround">Whether the entity was on the ground per the last update.</param>
/// <param name="PlayerName">The player name for player entities, else null.</param>
/// <param name="CustomName">The rendered custom name, or null.</param>
/// <param name="IsPlayer">True for player entities.</param>
public sealed record BeaconEntityInfo(
    int Id,
    string Uuid,
    string TypeId,
    double X,
    double Y,
    double Z,
    float Yaw,
    float Pitch,
    string Pose,
    bool OnGround,
    string? PlayerName,
    string? CustomName,
    bool IsPlayer);

/// <summary>One trade item (an input or a result) for <c>trade.list</c>.</summary>
/// <param name="Type">The namespaced item id.</param>
/// <param name="Name">The display name (custom name when set, else the short id with spaces).</param>
/// <param name="Count">The stack count.</param>
public sealed record BeaconTradeItem(string Type, string Name, int Count);

/// <summary>One merchant offer for <c>trade.list</c>, in select order.</summary>
/// <param name="Index">The trade index a client selects.</param>
/// <param name="First">The (price-adjusted) first input.</param>
/// <param name="Second">The optional second input, or null.</param>
/// <param name="Result">The output.</param>
/// <param name="Uses">Uses consumed so far.</param>
/// <param name="MaxUses">Uses before the offer locks.</param>
/// <param name="SoldOut">Whether the offer is currently sold out.</param>
/// <param name="Xp">Villager xp granted per trade.</param>
public sealed record BeaconTradeOffer(
    int Index,
    BeaconTradeItem First,
    BeaconTradeItem? Second,
    BeaconTradeItem Result,
    int Uses,
    int MaxUses,
    bool SoldOut,
    int Xp);

/// <summary>One enchanting-table option for <c>enchant.options</c>.</summary>
/// <param name="Slot">The option slot (0 top, 1 middle, 2 bottom).</param>
/// <param name="Level">The required XP level, or null when the server has not synced it yet.</param>
public sealed record BeaconEnchantOption(int Slot, int? Level);

/// <summary>One input control of the shown server dialog, for <c>dialog.show</c> and <c>on dialog</c>.</summary>
/// <param name="Key">The payload key this control writes to.</param>
/// <param name="Label">The control's label, or null when it has none.</param>
/// <param name="Kind">The control kind (Text, Boolean, SingleOption, NumberRange, Unknown).</param>
/// <param name="Value">The current (staged or initial) value.</param>
public sealed record BeaconDialogInput(string Key, string? Label, string Kind, string Value);

/// <summary>One pressable button of the shown server dialog.</summary>
/// <param name="Index">The 1-based button number, matching <c>dialog click</c>.</param>
/// <param name="Label">The button label.</param>
public sealed record BeaconDialogButton(int Index, string Label);

/// <summary>The shown server dialog for the <c>dialog.*</c> builtins.</summary>
/// <param name="Title">The dialog title; empty for a registry reference.</param>
/// <param name="Body">The body lines joined with newlines.</param>
/// <param name="Inputs">The input controls in order.</param>
/// <param name="Buttons">The pressable buttons in 1-based order.</param>
/// <param name="RegistryId">The registry index, or null for an inline dialog.</param>
public sealed record BeaconDialogInfo(
    string Title,
    string Body,
    IReadOnlyList<BeaconDialogInput> Inputs,
    IReadOnlyList<BeaconDialogButton> Buttons,
    int? RegistryId);

/// <summary>Polled self vitals for the <c>me.*</c> builtins; every field is null when unknown.</summary>
/// <param name="Health">Current health.</param>
/// <param name="MaxHealth">Maximum health.</param>
/// <param name="Food">Current food level.</param>
/// <param name="Saturation">Current saturation.</param>
/// <param name="Air">Remaining air ticks.</param>
/// <param name="XpLevel">Experience level.</param>
/// <param name="Armor">Armor points.</param>
public sealed record BeaconVitals(
    double? Health,
    double? MaxHealth,
    double? Food,
    double? Saturation,
    double? Air,
    double? XpLevel,
    double? Armor);

/// <summary>Polled self position for <c>me.pos</c>, <c>me.yaw</c>, <c>me.pitch</c>.</summary>
/// <param name="X">Block X.</param>
/// <param name="Y">Block Y.</param>
/// <param name="Z">Block Z.</param>
/// <param name="Yaw">Look yaw, or null when unknown.</param>
/// <param name="Pitch">Look pitch, or null when unknown.</param>
public sealed record BeaconPosition(double X, double Y, double Z, double? Yaw, double? Pitch);

/// <summary>One active potion effect for <c>me.effects</c>.</summary>
/// <param name="Name">The effect id (stable, never localized).</param>
/// <param name="Level">The amplifier level.</param>
/// <param name="SecondsLeft">Whole seconds remaining.</param>
public sealed record BeaconEffectInfo(string Name, int Level, int SecondsLeft);

/// <summary>Server facts for the <c>server.*</c> builtins; every field is null when unknown.</summary>
/// <param name="Ip">The server address as configured.</param>
/// <param name="Port">The server port.</param>
/// <param name="VersionName">The server version name.</param>
/// <param name="MaxPlayers">The advertised player cap.</param>
/// <param name="Motd">The message of the day.</param>
/// <param name="DayTime">Daytime ticks.</param>
/// <param name="Day">Whole days elapsed.</param>
/// <param name="Weather">The weather name.</param>
/// <param name="Difficulty">The difficulty name.</param>
public sealed record BeaconServerInfo(
    string? Ip,
    int? Port,
    string? VersionName,
    int? MaxPlayers,
    string? Motd,
    long? DayTime,
    long? Day,
    string? Weather,
    string? Difficulty);

/// <summary>One inventory slot for the <c>inv.*</c> builtins.</summary>
/// <param name="Slot">The slot index.</param>
/// <param name="Type">The normalized type id (stable, never localized).</param>
/// <param name="Name">The display name.</param>
/// <param name="Count">The stack count.</param>
/// <param name="Lore">The lore lines, or null when the item has none.</param>
public sealed record BeaconInvSlot(int Slot, string Type, string Name, int Count, IReadOnlyList<string>? Lore);

/// <summary>One chat history line with its arrival time for <c>count_matching</c>.</summary>
/// <param name="Text">The rendered line.</param>
/// <param name="When">When the line arrived.</param>
public sealed record BeaconChatLine(string Text, DateTimeOffset When);

/// <summary>One <c>stop event</c> suppression of a chat message, for diagnostics.</summary>
/// <param name="Text">The withheld line (empty when it had already scrolled out of history).</param>
/// <param name="Hook">The suppressible hook that withheld it (<c>chat</c>, <c>whisper</c>, <c>server_message</c>).</param>
/// <param name="ScriptIds">Every script whose handler ran <c>stop event</c> on it.</param>
/// <param name="When">When the verdict arrived.</param>
public sealed record BeaconSuppressedChat(
    string Text, string Hook, IReadOnlyList<string> ScriptIds, DateTimeOffset When);

/// <summary>Bounds for the suppression log.</summary>
public static class BeaconSuppressionBounds
{
    /// <summary>Maximum withheld-message records kept (diagnostics only, RAM-only like throttles).</summary>
    public const int LogMax = 50;
}

/// <summary>One block position from <c>world.find_blocks</c>.</summary>
/// <param name="X">Block X.</param>
/// <param name="Y">Block Y.</param>
/// <param name="Z">Block Z.</param>
public sealed record BeaconBlockPos(int X, int Y, int Z);

/// <summary>One sign hit from <c>world.find_signs</c>: where the sign stands and its joined text.</summary>
/// <param name="X">Block X.</param>
/// <param name="Y">Block Y.</param>
/// <param name="Z">Block Z.</param>
/// <param name="Text">The joined non-empty sign lines.</param>
public sealed record BeaconSignInfo(int X, int Y, int Z, string Text);

/// <summary>One scoreboard objective with its scores for <c>server.scoreboard</c>.</summary>
/// <param name="Name">The objective name.</param>
/// <param name="Display">The rendered display name.</param>
/// <param name="Scores">Entry name to score value.</param>
public sealed record BeaconObjectiveInfo(
    string Name, string Display, IReadOnlyDictionary<string, int> Scores);

/// <summary>One scoreboard team for <c>server.scoreboard</c>.</summary>
/// <param name="Name">The team name.</param>
/// <param name="Display">The rendered display name.</param>
/// <param name="Members">Member entry names.</param>
public sealed record BeaconTeamInfo(
    string Name, string Display, IReadOnlyList<string> Members);

/// <summary>A scoreboard snapshot for <c>server.scoreboard</c>: objectives plus teams.</summary>
/// <param name="Objectives">Objectives with their scores.</param>
/// <param name="Teams">Teams with their members.</param>
public sealed record BeaconScoreboard(
    IReadOnlyList<BeaconObjectiveInfo> Objectives, IReadOnlyList<BeaconTeamInfo> Teams)
{
    /// <summary>An empty board (no session attached).</summary>
    public static BeaconScoreboard Empty { get; } = new([], []);
}

/// <summary>One boss bar for <c>server.bossbars</c>.</summary>
/// <param name="Title">The rendered title.</param>
/// <param name="Progress">Fill from 0 to 1.</param>
/// <param name="Color">The bar color name.</param>
public sealed record BeaconBossBarInfo(string Title, double Progress, string Color);

/// <summary>The open container for <c>inv.container</c>: null when only the player inventory is open.</summary>
/// <param name="WindowId">The open window id.</param>
/// <param name="Title">The container title, or null when unknown.</param>
/// <param name="Kind">The semantic menu kind, or null when unknown.</param>
/// <param name="Slots">The container slots.</param>
public sealed record BeaconContainerInfo(
    int WindowId, string? Title, string? Kind, IReadOnlyList<BeaconInvSlot> Slots);

/// <summary>One view raycast hit for <c>world.looking_at</c>.</summary>
/// <param name="X">Block X.</param>
/// <param name="Y">Block Y.</param>
/// <param name="Z">Block Z.</param>
/// <param name="Name">The block display name.</param>
/// <param name="Distance">Eye-to-hit distance in blocks.</param>
public sealed record BeaconRaycastHit(int X, int Y, int Z, string Name, double Distance);

/// <summary>One dig outcome for <c>world.dig</c>: what the server is known to have done.</summary>
/// <param name="Broken">True when the block is known broken afterwards.</param>
/// <param name="Detail">Human-readable detail.</param>
public sealed record BeaconDigResult(bool Broken, string Detail);

/// <summary>
/// Extended session reads for the script ceiling: vitals, position, server facts, effects, detailed chat history, and light/biome reads.
/// Every member has a default (null or empty) so existing <see cref="IBeaconHostServices"/> implementers keep compiling; the interpreter renders unknowns as <c>none</c> or empty lists.
/// </summary>
public interface IBeaconExtendedReads : IBeaconSessionReads
{
    /// <summary>The game protocol number for dataset reads, or null when unknown (tables fall back to the built-in list).</summary>
    int? GameProtocol => null;

    /// <summary>Polled vitals, or null when no session is attached.</summary>
    BeaconVitals? SelfVitals => null;

    /// <summary>Polled position, or null when no session is attached.</summary>
    BeaconPosition? SelfPosition => null;

    /// <summary>Gamemode name, or null when unknown.</summary>
    string? Gamemode => null;

    /// <summary>Ping milliseconds, or null when unknown.</summary>
    int? Ping => null;

    /// <summary>Whether the player is sneaking, or null when unknown.</summary>
    bool? IsSneaking => null;

    /// <summary>Active potion effects (empty when none or unknown).</summary>
    IReadOnlyList<BeaconEffectInfo> PlayerEffects => [];

    /// <summary>Server facts, or null when no session is attached.</summary>
    BeaconServerInfo? ServerInfo => null;

    /// <summary>Milliseconds per tick, or null when unknown.</summary>
    double? ServerMspt => null;

    /// <summary>Recent chat lines with arrival times, oldest first (callers clamp to the history cap).</summary>
    IReadOnlyList<BeaconChatLine> ChatHistoryDetailed(int limit) => ChatHistory(limit)
        .Select(text => new BeaconChatLine(text, DateTimeOffset.UtcNow)).ToList();

    /// <summary>Most recent line from <paramref name="player"/>, or null when none is kept.</summary>
    string? LastFrom(string player) => ChatHistory(BeaconReadBounds.ChatHistoryMax)
        .LastOrDefault(line => line.Contains(player, StringComparison.OrdinalIgnoreCase));

    /// <summary>Light level at a position, or null when unknown.</summary>
    int? GetLight(int x, int y, int z) => null;

    /// <summary>Biome name at a position, or null when unknown.</summary>
    string? GetBiome(int x, int y, int z) => null;

    /// <summary>
    /// Block positions matching <paramref name="nameOrId"/> within <paramref name="radius"/> blocks of the player, nearest first, at most <paramref name="maxResults"/> (callers cap at <c>BeaconReadBounds.FindBlocksMax</c>).
    /// Empty when unwired.
    /// </summary>
    Task<IReadOnlyList<BeaconBlockPos>> FindBlocksAsync(
        string nameOrId, int radius, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BeaconBlockPos>>([]);

    /// <summary>
    /// The joined sign text at a position, or null when no sign is tracked there or nothing is wired.
    /// Unknown and blank-adjacent states both degrade to unknown so headless runs stay deterministic; the interpreter renders null as <c>none</c>.
    /// </summary>
    string? GetSignText(int x, int y, int z) => null;

    /// <summary>
    /// Signs whose joined text contains <paramref name="needle"/> (case-insensitive) within <paramref name="radius"/> blocks of the player, nearest first, at most <paramref name="maxResults"/> (callers cap like <c>world.find_blocks</c>).
    /// Empty when unwired.
    /// </summary>
    Task<IReadOnlyList<BeaconSignInfo>> FindSignsAsync(
        string needle, int radius, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BeaconSignInfo>>([]);

    /// <summary>Scoreboard objectives plus teams; empty when no session is attached.</summary>
    BeaconScoreboard Scoreboard => BeaconScoreboard.Empty;

    /// <summary>Active boss bars; empty when none or unknown.</summary>
    IReadOnlyList<BeaconBossBarInfo> BossBars => [];
}

/// <summary>
/// Inventory reads and writes for the <c>inv.*</c> builtins plus <c>eat()</c> and crafting.
/// Every member has an inert default (empty slots, refused writes) so existing implementers keep compiling and headless runs stay deterministic.
/// </summary>
public interface IBeaconInventoryHost
{
    /// <summary>Visible slot snapshot (empty when no session is attached).</summary>
    IReadOnlyList<BeaconInvSlot> InventorySlots => [];

    /// <summary>Selected hotbar slot index.</summary>
    int SelectedSlot => 0;

    /// <summary>Worn pieces by slot name (<c>head</c>, <c>chest</c>, <c>legs</c>, <c>feet</c>, <c>offhand</c>).</summary>
    IReadOnlyDictionary<string, BeaconInvSlot?> ArmorSlots =>
        new Dictionary<string, BeaconInvSlot?>(StringComparer.Ordinal);

    /// <summary>Selects <paramref name="slot"/>; false when the slot is out of range.</summary>
    Task<bool> SelectSlotAsync(int slot, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Drops one item (<paramref name="stack"/> drops the whole stack); false when empty.</summary>
    Task<bool> DropAsync(bool stack, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Moves a stack from a slot index to a slot index or a named slot (<c>offhand</c>); false when refused.</summary>
    Task<bool> MoveAsync(int from, string to, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Clicks <paramref name="slot"/> with <paramref name="mode"/>; false when refused.</summary>
    Task<bool> ClickAsync(int slot, string mode, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Known recipe ids (empty when crafting is unavailable).</summary>
    IReadOnlyList<string> CraftList() => [];

    /// <summary>Crafts one <paramref name="recipe"/>; false when unknown or uncraftable.</summary>
    Task<bool> CraftOneAsync(string recipe, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Eats the best food item; false when nothing edible is held. Null means "no inventory wired".</summary>
    Task<bool?> EatBestAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);

    /// <summary>The open container, or null when only the player inventory is open.</summary>
    BeaconContainerInfo? OpenContainer => null;

    /// <summary>
    /// Shift-clicks <paramref name="slot"/> of the open container into the player inventory (<paramref name="count"/> is advisory: vanilla moves the whole stack).
    /// False when no container is open or the slot is out of range.
    /// </summary>
    Task<bool> TakeFromContainerAsync(int slot, int count, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Shift-clicks <paramref name="slot"/> of the player inventory into the open container.
    /// False when no container is open or the slot is out of range.
    /// </summary>
    Task<bool> PutIntoContainerAsync(int slot, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Self actions for movement and interaction: <c>move_goto</c>, <c>move_follow</c>, <c>stop_moving</c>, <c>look_at</c>, <c>attack</c>, <c>use_in_hand</c>.
/// Defaults complete immediately so headless runs stay deterministic; live adapters steer for real.
/// </summary>
public interface IBeaconActionHost
{
    /// <summary>Walks to a coordinate; completes on arrival or cancellation.</summary>
    Task MoveToAsync(double x, double y, double z, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Tracks a player until cancelled.</summary>
    Task FollowPlayerAsync(string player, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Stops steering; a no-op when idle.</summary>
    Task StopMovingAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Turns to face a coordinate.</summary>
    Task LookAtAsync(double x, double y, double z, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Attacks a target.</summary>
    Task AttackAsync(string target, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Right-clicks a target (villager trade, sheep shear, lead attach).
    /// The default completes immediately so headless runs stay deterministic; live adapters interact for real.
    /// </summary>
    Task InteractAsync(string target, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Uses the held item.</summary>
    Task UseInHandAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Entity reads and id-targeted actions for the <c>entities.*</c> builtins plus <c>attack</c>/<c>interact</c> by id and the <c>entity_add</c>/<c>entity_remove</c> poll.
/// Every member has an inert default (empty snapshots, completing actions) so existing implementers keep compiling and headless runs stay deterministic.
/// Reads degrade to empty when entity tracking is off (<c>[Gameplay] Entity</c>), the same way inventory reads degrade.
/// </summary>
public interface IBeaconEntityHost
{
    /// <summary>
    /// Snapshots tracked entities within <paramref name="radius"/> blocks of the player (callers clamp to <c>BeaconReadBounds</c>).
    /// Empty when unwired or tracking is off.
    /// </summary>
    IReadOnlyList<BeaconEntityInfo> NearbyEntities(double radius) => [];

    /// <summary>Snapshots the entity with the given network id, or null when untracked.</summary>
    BeaconEntityInfo? EntityById(int entityId) => null;

    /// <summary>Attacks (left-clicks) the entity with the given network id.</summary>
    Task AttackEntityAsync(int entityId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Interacts (right-clicks) with the entity with the given network id.</summary>
    Task InteractEntityAsync(int entityId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Villager/wandering-trader offers for the <c>trade.*</c> builtins.
/// Every member has an inert default (no merchant, refused writes) so existing implementers keep compiling and headless runs stay deterministic.
/// </summary>
public interface IBeaconTradeHost
{
    /// <summary>
    /// The open merchant's offers in select order, or null when no merchant window is open (empty when the window is open but the server has not synced offers yet).
    /// </summary>
    IReadOnlyList<BeaconTradeOffer>? OpenTrades => null;

    /// <summary>Selects a merchant trade by index; false when the index is out of range.</summary>
    Task<bool> SelectTradeAsync(int tradeIndex, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>
    /// Buys <paramref name="count"/> units of trade <paramref name="tradeIndex"/> (select plus one exact take per unit: result to cursor, cursor parked) and answers how many units completed.
    /// Zero when the index is bad or the offer locks mid-loop.
    /// </summary>
    Task<int> BuyTradeAsync(int tradeIndex, int count, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>
/// Enchanting-table options for the <c>enchant.*</c> builtins.
/// Every member has an inert default (no table, refused writes) so existing implementers keep compiling and headless runs stay deterministic.
/// </summary>
public interface IBeaconEnchantHost
{
    /// <summary>
    /// The open enchanting table's three options (top, middle, bottom), or null when no enchanting window is open.
    /// A null level means the server has not synced that cost yet.
    /// </summary>
    IReadOnlyList<BeaconEnchantOption>? EnchantOptions => null;

    /// <summary>Clicks an enchantment option (0 top, 1 middle, 2 bottom); false when out of range.</summary>
    Task<bool> ChooseEnchantAsync(int slot, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Server dialogs (1.21.6+) for the <c>dialog.*</c> builtins and <c>on dialog</c>.
/// Every member has an inert default (no dialog, refused writes) so existing implementers keep compiling and headless runs stay deterministic.
/// </summary>
public interface IBeaconDialogHost
{
    /// <summary>The shown dialog, or null when none is open or nothing is wired.</summary>
    BeaconDialogInfo? CurrentDialog => null;

    /// <summary>Values staged by <c>dialog.set</c>, keyed by input key.</summary>
    IReadOnlyDictionary<string, string> StagedDialogInputs => new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Stages one input value; false when the key is unknown.
    /// Throws a refused error when no dialog is open.
    /// </summary>
    bool StageDialogInput(string key, string value) => false;

    /// <summary>Clears values staged by <c>dialog.set</c>.</summary>
    void ClearStagedDialogInputs()
    {
    }

    /// <summary>
    /// Presses a 1-based dialog button, submitting <paramref name="values"/> (or the staged values when null).
    /// Returns true when the press reached the server or closed the dialog; throws a refused error when there is no dialog, the button is out of range, or the button carries no action this client can perform.
    /// </summary>
    Task<bool> AnswerDialogAsync(
        IReadOnlyDictionary<string, string>? values, int buttonOneBased, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>Cancels the shown dialog the way escape does.</summary>
    Task CancelDialogAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Session control for the <c>disconnect</c> verb: leave the server cleanly and stay out (auto-reconnect stops; <c>on disconnect</c> still fires).
/// Defaults to a no-op so headless runs stay deterministic.
/// </summary>
public interface IBeaconSessionControl
{
    /// <summary>Disconnects the session cleanly and stops any auto-reconnect.</summary>
    Task DisconnectAsync(string? reason, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// World mutations for builder/miner bots: <c>world.dig</c>, <c>world.place</c>, <c>world.use</c>, <c>world.looking_at</c>.
/// Defaults refuse (false/null) so headless runs stay deterministic; live adapters dig, place, open, and raycast for real behind the <c>world.write</c> capability, the gameplay gates, and the write gate.
/// The combined seam extends this.
/// <para>
/// <c>world.use</c> needs the write cap because it actuates the world (it sends a use interaction, like <c>world.place</c>), even though it reads a container back; the targeting read <c>world.looking_at</c> is capped the same way for the same reason.
/// </para>
/// </summary>
public interface IBeaconWorldWriteHost
{
    /// <summary>
    /// Digs the block at a position through the full action sequence and reports what the server is known to have done about it.
    /// Never reports a send as a break.
    /// </summary>
    Task<BeaconDigResult> DigAsync(int x, int y, int z, string face, CancellationToken ct = default)
        => Task.FromResult(new BeaconDigResult(false, "no world writer is wired"));

    /// <summary>
    /// Uses the held item against a block face.
    /// Completion is the send, not the effect: false means refused, true means sent.
    /// </summary>
    Task<bool> PlaceAsync(int x, int y, int z, string face, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>Casts the view ray up to <paramref name="maxDistance"/> blocks; null on a miss or when unwired.</summary>
    BeaconRaycastHit? Raycast(double maxDistance) => null;

    /// <summary>
    /// Uses the block at a position and returns the window the server opens, or null when no window appears or nothing is wired.
    /// Null is honest: the send is not the effect, so a timeout reports none instead of claiming a container.
    /// </summary>
    Task<BeaconContainerInfo?> OpenContainerAtAsync(int x, int y, int z, CancellationToken ct = default)
        => Task.FromResult<BeaconContainerInfo?>(null);
}

/// <summary>
/// The bucket-style write gate behind <c>world.dig</c>/<c>world.place</c>: at most <see cref="MaxOperations"/> mutations per <see cref="Window"/>, over the interpreter clock (virtual in tests, so gate tests never sleep).
/// Past the allowance the verb refuses with catchable B4013 naming the retry delay.
/// Per script (one gate per interpreter), so one chatty script cannot spend another script's allowance.
/// </summary>
public sealed class BeaconWorldWriteGate
{
    /// <summary>Mutations allowed per window.</summary>
    public const int MaxOperations = 8;

    /// <summary>The window the allowance regenerates over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly IVirtualClock _clock;
    private readonly Queue<DateTimeOffset> _operations = new();

    /// <summary>Builds a full gate over <paramref name="clock"/>.</summary>
    public BeaconWorldWriteGate(IVirtualClock? clock = null)
    {
        _clock = clock ?? SystemClock.Shared;
    }

    /// <summary>
    /// Spends one mutation.
    /// Throws catchable B4013 with the retry delay when the window is spent.
    /// </summary>
    public void Acquire(SourceSpan span, string verb)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        DateTimeOffset now;
        TimeSpan retry;
        lock (_gate)
        {
            now = _clock.UtcNow;
            while (_operations.Count > 0 && now - _operations.Peek() >= Window)
                _operations.Dequeue();

            if (_operations.Count < MaxOperations)
            {
                _operations.Enqueue(now);
                return;
            }

            retry = Window - (now - _operations.Peek());
        }

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.WorldWriteGate,
            $"I expected a free world-write slot for '{verb}', but all {MaxOperations} per {Window.TotalSeconds:F0}s are spent. Retry in {retry.TotalSeconds:F1}s.",
            span.Origin,
            $"Wait {Math.Ceiling(retry.TotalSeconds)} seconds, then run '{verb}' again.");
    }
}

/// <summary>Persisted per-script state IO (the <c>saved</c> store; TOML-backed).</summary>
public interface IBeaconStorageIO
{
    /// <summary>Persists <paramref name="value"/> under <paramref name="key"/> for <paramref name="scriptId"/>.</summary>
    Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default);

    /// <summary>Loads a persisted value, or null when absent.</summary>
    Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default);
}

/// <summary>
/// The combined Beacon host seam: chat send, command dispatch, session reads, storage IO as one injectable surface.
/// Classic and TUI backends implement it; tests stub it.
/// The four member interfaces extend, never this combination, so existing implementers keep compiling.
/// </summary>
public interface IBeaconHostServices
    : IBeaconChatSink,
        IBeaconCommandDispatcher,
        IBeaconSessionReads,
        IBeaconStorageIO,
        IBeaconExtendedReads,
        IBeaconInventoryHost,
        IBeaconActionHost,
        IBeaconEntityHost,
        IBeaconTradeHost,
        IBeaconEnchantHost,
        IBeaconDialogHost,
        IBeaconSessionControl,
        IBeaconWorldWriteHost
{
}

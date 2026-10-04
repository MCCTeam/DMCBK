using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;

namespace DMCBK.Testing;

/// <summary>Mutable host double with the extended reads, inventory, actions, and chat history.</summary>
public sealed class ScriptTestHost : IBeaconHostServices
{
    /// <summary>Gets recorded public chat messages.</summary>
    public List<string> Says { get; } = [];
    /// <summary>Gets recorded private chat messages and their recipients.</summary>
    public List<(string Player, string Text)> Whispers { get; } = [];
    /// <summary>Gets recorded server commands.</summary>
    public List<string> ServerLines { get; } = [];
    /// <summary>Gets recorded host command invocations.</summary>
    public List<string> MccCalls { get; } = [];
    /// <summary>Gets recorded movement and interaction actions.</summary>
    public List<string> Moves { get; } = [];

    /// <summary>Gets or sets the name returned for the local player.</summary>
    public string? SelfNameValue { get; set; } = "Tester";
    /// <summary>Gets or sets the online player names.</summary>
    public List<string> Players { get; set; } = ["Alice", "Bob"];
    /// <summary>Gets or sets the reported server ticks per second.</summary>
    public double? ServerTpsValue { get; set; } = 20.0;
    /// <summary>Gets or sets the reported server milliseconds per tick.</summary>
    public double? ServerMsptValue { get; set; } = 50.0;
    /// <summary>Gets or sets the reported server information.</summary>
    public BeaconServerInfo? ServerInfoValue { get; set; }
    /// <summary>Gets or sets the local player vitals.</summary>
    public BeaconVitals? VitalsValue { get; set; }
    /// <summary>Gets or sets the local player position.</summary>
    public BeaconPosition? PositionValue { get; set; }
    /// <summary>Gets or sets the local player game mode.</summary>
    public string? GamemodeValue { get; set; }
    /// <summary>Gets or sets the reported connection latency.</summary>
    public int? PingValue { get; set; }
    /// <summary>Gets or sets the local player sneaking state.</summary>
    public bool? SneakingValue { get; set; }
    /// <summary>Gets or sets the active player effects.</summary>
    public List<BeaconEffectInfo> EffectsValue { get; set; } = [];
    /// <summary>Gets or sets the reported game protocol.</summary>
    public int? ProtocolValue { get; set; }
    /// <summary>Gets or sets the chat history in arrival order.</summary>
    public List<BeaconChatLine> ChatLines { get; set; } = [];
    /// <summary>Gets the block observations indexed by coordinates.</summary>
    public Dictionary<(int X, int Y, int Z), BeaconBlockInfo> Blocks { get; } = new();
    /// <summary>Gets the light observations indexed by coordinates.</summary>
    public Dictionary<(int X, int Y, int Z), int> Light { get; } = new();
    /// <summary>Gets the biome observations indexed by coordinates.</summary>
    public Dictionary<(int X, int Y, int Z), string> Biomes { get; } = new();

    /// <summary>Gets or sets the inventory slots.</summary>
    public List<BeaconInvSlot> Slots { get; set; } = [];
    /// <summary>Gets or sets the selected hotbar slot.</summary>
    public int SelectedSlotValue { get; set; }
    /// <summary>Gets or sets the equipped armor slots.</summary>
    public Dictionary<string, BeaconInvSlot?> Armor { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets recorded slot selection calls.</summary>
    public List<(int Slot, string Text)> SelectCalls { get; } = [];
    /// <summary>Gets recorded inventory move calls.</summary>
    public List<(int From, string To)> MoveCalls { get; } = [];
    /// <summary>Gets or sets the available crafting recipes.</summary>
    public List<string> CraftRecipes { get; set; } = [];
    /// <summary>Gets or sets the result returned for crafting.</summary>
    public bool CraftResult { get; set; } = true;
    /// <summary>Gets or sets the result returned for eating.</summary>
    public bool? EatResult { get; set; }

    /// <inheritdoc/>
    public Task SayAsync(string text, CancellationToken ct = default)
    {
        Says.Add(text);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task WhisperAsync(string player, string text, CancellationToken ct = default)
    {
        Whispers.Add((player, text));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
    {
        ServerLines.Add(commandLine);
        return Task.FromResult(string.Empty);
    }

    /// <inheritdoc/>
    public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
    {
        MccCalls.Add(commandLine);
        return Task.FromResult(string.Empty);
    }

    /// <inheritdoc/>
    public string? SelfName => SelfNameValue;
    /// <inheritdoc/>
    public IReadOnlyList<string> OnlinePlayers(int limit) => Players.Take(Math.Max(0, limit)).ToList();
    /// <inheritdoc/>
    public double? ServerTps => ServerTpsValue;
    /// <inheritdoc/>
    public double? ServerMspt => ServerMsptValue;
    /// <inheritdoc/>
    public BeaconServerInfo? ServerInfo => ServerInfoValue;
    /// <inheritdoc/>
    public BeaconVitals? SelfVitals => VitalsValue;
    /// <inheritdoc/>
    public BeaconPosition? SelfPosition => PositionValue;
    /// <inheritdoc/>
    public string? Gamemode => GamemodeValue;
    /// <inheritdoc/>
    public int? Ping => PingValue;
    /// <inheritdoc/>
    public bool? IsSneaking => SneakingValue;
    /// <inheritdoc/>
    public IReadOnlyList<BeaconEffectInfo> PlayerEffects => EffectsValue;
    /// <inheritdoc/>
    public int? GameProtocol => ProtocolValue;

    /// <inheritdoc/>
    public IReadOnlyList<string> ChatHistory(int limit) =>
        ChatLines.TakeLast(Math.Max(0, limit)).Select(l => l.Text).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<BeaconChatLine> ChatHistoryDetailed(int limit) =>
        ChatLines.TakeLast(Math.Max(0, limit)).ToList();

    /// <inheritdoc/>
    public BeaconBlockInfo? GetBlock(int x, int y, int z) =>
        Blocks.TryGetValue((x, y, z), out BeaconBlockInfo? block) ? block : null;

    /// <inheritdoc/>
    public int? GetLight(int x, int y, int z) =>
        Light.TryGetValue((x, y, z), out int level) ? level : null;

    /// <inheritdoc/>
    public string? GetBiome(int x, int y, int z) =>
        Biomes.TryGetValue((x, y, z), out string? biome) ? biome : null;

    /// <inheritdoc/>
    public IReadOnlyList<BeaconInvSlot> InventorySlots => Slots;
    /// <inheritdoc/>
    public int SelectedSlot => SelectedSlotValue;
    /// <inheritdoc/>
    public IReadOnlyDictionary<string, BeaconInvSlot?> ArmorSlots => Armor;

    /// <inheritdoc/>
    public Task<bool> SelectSlotAsync(int slot, CancellationToken ct = default)
    {
        SelectCalls.Add((slot, string.Empty));
        SelectedSlotValue = slot;
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> DropAsync(bool stack, CancellationToken ct = default) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task<bool> MoveAsync(int from, string to, CancellationToken ct = default)
    {
        MoveCalls.Add((from, to));
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> ClickAsync(int slot, string mode, CancellationToken ct = default) => Task.FromResult(true);
    /// <inheritdoc/>
    public IReadOnlyList<string> CraftList() => CraftRecipes;
    /// <inheritdoc/>
    public Task<bool> CraftOneAsync(string recipe, CancellationToken ct = default) => Task.FromResult(CraftResult);
    /// <inheritdoc/>
    public Task<bool?> EatBestAsync(CancellationToken ct = default) => Task.FromResult(EatResult);

    /// <inheritdoc/>
    public Task MoveToAsync(double x, double y, double z, CancellationToken ct = default)
    {
        Moves.Add($"goto {x} {y} {z}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FollowPlayerAsync(string player, CancellationToken ct = default)
    {
        Moves.Add($"follow {player}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopMovingAsync(CancellationToken ct = default)
    {
        Moves.Add("stop");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task LookAtAsync(double x, double y, double z, CancellationToken ct = default)
    {
        Moves.Add($"look {x} {y} {z}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AttackAsync(string target, CancellationToken ct = default)
    {
        Moves.Add($"attack {target}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InteractAsync(string target, CancellationToken ct = default)
    {
        Moves.Add($"interact {target}");
        return Task.CompletedTask;
    }

    /// <summary>Gets or sets the observable entities.</summary>
    public List<BeaconEntityInfo> Entities { get; set; } = [];
    /// <summary>Gets recorded actions targeting entity identifiers.</summary>
    public List<string> EntityActions { get; } = [];

    /// <inheritdoc/>
    public IReadOnlyList<BeaconEntityInfo> NearbyEntities(double radius)
    {
        BeaconPosition? self = PositionValue;
        if (self is null)
            return Entities.Where(e => 0 <= radius).ToList();

        return Entities.Where(e =>
        {
            double dx = e.X - self.X;
            double dy = e.Y - self.Y;
            double dz = e.Z - self.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= radius;
        }).ToList();
    }

    /// <inheritdoc/>
    public BeaconEntityInfo? EntityById(int entityId) =>
        Entities.FirstOrDefault(e => e.Id == entityId);

    /// <inheritdoc/>
    public Task AttackEntityAsync(int entityId, CancellationToken ct = default)
    {
        EntityActions.Add($"attack-id {entityId}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InteractEntityAsync(int entityId, CancellationToken ct = default)
    {
        EntityActions.Add($"interact-id {entityId}");
        return Task.CompletedTask;
    }

    /// <summary>Gets or sets the currently open trade offers.</summary>
    public List<BeaconTradeOffer>? Trades { get; set; }
    /// <summary>Gets recorded trade selections.</summary>
    public List<int> TradeSelects { get; } = [];
    /// <summary>Gets recorded trade purchases and their requested counts.</summary>
    public List<(int Index, int Count)> TradeBuys { get; } = [];
    /// <summary>Gets or sets the maximum successful trade count.</summary>
    public int TradeBuyResult { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<BeaconTradeOffer>? OpenTrades => Trades;

    /// <inheritdoc/>
    public Task<bool> SelectTradeAsync(int tradeIndex, CancellationToken ct = default)
    {
        TradeSelects.Add(tradeIndex);
        return Task.FromResult(Trades is not null && tradeIndex >= 0 && tradeIndex < Trades.Count);
    }

    /// <inheritdoc/>
    public Task<int> BuyTradeAsync(int tradeIndex, int count, CancellationToken ct = default)
    {
        TradeBuys.Add((tradeIndex, count));
        if (Trades is null || tradeIndex < 0 || tradeIndex >= Trades.Count)
            return Task.FromResult(0);

        return Task.FromResult(Math.Min(count, TradeBuyResult));
    }

    /// <summary>Gets or sets the available enchantment options.</summary>
    public List<BeaconEnchantOption>? Enchantments { get; set; }
    /// <summary>Gets recorded enchantment selections.</summary>
    public List<int> EnchantChooses { get; } = [];
    /// <summary>Gets or sets whether valid enchantment selections succeed.</summary>
    public bool EnchantChooseResult { get; set; } = true;

    /// <inheritdoc/>
    public IReadOnlyList<BeaconEnchantOption>? EnchantOptions => Enchantments;

    /// <inheritdoc/>
    public Task<bool> ChooseEnchantAsync(int slot, CancellationToken ct = default)
    {
        EnchantChooses.Add(slot);
        return Task.FromResult(EnchantChooseResult && slot is >= 0 and <= 2);
    }

    /// <summary>Gets or sets the currently open dialog.</summary>
    public BeaconDialogInfo? DialogValue { get; set; }
    /// <summary>Gets the staged dialog input values.</summary>
    public Dictionary<string, string> DialogStaged { get; } = new(StringComparer.Ordinal);
    /// <summary>Gets recorded dialog answers and button selections.</summary>
    public List<(IReadOnlyDictionary<string, string>? Values, int Button)> DialogAnswers { get; } = [];
    /// <summary>Gets the number of dialog cancellations.</summary>
    public int DialogCancels { get; private set; }
    /// <summary>Gets or sets the result returned for answering a dialog.</summary>
    public bool DialogAnswerResult { get; set; } = true;

    /// <inheritdoc/>
    public BeaconDialogInfo? CurrentDialog => DialogValue;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> StagedDialogInputs => DialogStaged;

    /// <inheritdoc/>
    public bool StageDialogInput(string key, string value)
    {
        if (DialogValue is null)
        {
            throw new BeaconRuntimeException(
                "B4002", "no dialog is open", new SourceSpan("test.mcc", 1, 1, 0), "open one first");
        }

        if (DialogValue.Inputs.All(i => !string.Equals(i.Key, key, StringComparison.Ordinal)))
            return false;

        DialogStaged[key] = value;
        return true;
    }

    /// <inheritdoc/>
    public void ClearStagedDialogInputs() => DialogStaged.Clear();

    /// <inheritdoc/>
    public Task<bool> AnswerDialogAsync(
        IReadOnlyDictionary<string, string>? values, int buttonOneBased, CancellationToken ct = default)
    {
        DialogAnswers.Add((values, buttonOneBased));
        DialogStaged.Clear();
        return Task.FromResult(DialogAnswerResult);
    }

    /// <inheritdoc/>
    public Task CancelDialogAsync(CancellationToken ct = default)
    {
        DialogCancels++;
        DialogStaged.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Gets recorded disconnection requests.</summary>
    public List<string?> Disconnects { get; } = [];

    /// <inheritdoc/>
    public Task DisconnectAsync(string? reason, CancellationToken ct = default)
    {
        Disconnects.Add(reason);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UseInHandAsync(CancellationToken ct = default)
    {
        Moves.Add("use");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

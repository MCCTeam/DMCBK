using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Game.Inventory;
using Umpk.Geometry;

namespace DMCBK.Core.Beacon;

/// <summary>
/// The inert offline host: records nothing, refuses nothing, reads empty.
/// The headless lint run and the fix preview re-lint run on this, so they stay pure by construction: no session, no network, no disk beyond the linted files themselves.
/// </summary>
public sealed class BeaconOfflineHost : IBeaconHostServices
{
    /// <summary>The shared inert instance.</summary>
    public static BeaconOfflineHost Shared { get; } = new();

    private BeaconOfflineHost()
    {
    }

    /// <inheritdoc />
    public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public string? SelfName => null;

    /// <inheritdoc />
    public IReadOnlyList<string> OnlinePlayers(int limit) => [];

    /// <inheritdoc />
    public double? ServerTps => null;

    /// <inheritdoc />
    public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// The live-session adapter: <see cref="IBeaconHostServices"/> over an <see cref="Client"/>.
/// Chat and command sends go to the live session; reads bridge the game facade with the same session-loop guard commands use (<c>WaitForCommandWork</c>), degrading to empty offline.
/// <para/>
/// Mute is owned by the caller (the <c>/scripts</c> runtime): when muted, sends are held and counted instead of reaching the session, while script logic keeps running.
/// <para/>
/// Loop affinity mirrors <c>CommandContext.Run</c>: synchronous reads degrade to unknown rather than deadlocking when taken inline on the session loop.
/// Async writes never block the loop (they await); their failures surface as catchable errors naming the verb.
/// Movement runs under the runner lease (see the <c>/scripts</c> runtime binding), never by probing: an outside holder refuses readably instead of being stolen from.
/// </summary>
public sealed class BeaconClientHost : IBeaconHostServices
{
    private readonly Client _client;
    private readonly Func<bool> _isMuted;
    private readonly Action _noteSuppressed;
    private readonly object _chatGate = new();
    private readonly Queue<TaggedChatLine> _chatHistory = new();
    private readonly Queue<BeaconSuppressedChat> _suppressedChats = new();

    private sealed record TaggedChatLine(ChatMessageReceived Message, BeaconChatLine Line);

    /// <summary>Builds the adapter over a client with mute owned by the caller.</summary>
    public BeaconClientHost(Client client, Func<bool> isMuted, Action noteSuppressed)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(isMuted);
        ArgumentNullException.ThrowIfNull(noteSuppressed);
        _client = client;
        _isMuted = isMuted;
        _noteSuppressed = noteSuppressed;
        _client.Game.Chat.MessageReceived += OnChatMessage;
    }

    private void OnChatMessage(object? sender, ChatMessageReceived message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text;
        try
        {
            text = message.Message.ToPlainText(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }

        lock (_chatGate)
        {
            _chatHistory.Enqueue(new TaggedChatLine(message, new BeaconChatLine(text, DateTimeOffset.UtcNow)));
            while (_chatHistory.Count > BeaconReadBounds.ChatHistoryMax)
                _chatHistory.Dequeue();
        }
    }

    /// <summary>
    /// Withholds a chat message from script reads after a suppressible hook's handler ran <c>stop event</c> on it.
    /// The triggering message already reached every subscriber the moment it arrived (the suppression verdict only exists after the handler dispatch), so this retracts it from the read history instead of pretending it never arrived: <c>chat_history</c> and <c>last_from</c> no longer return it.
    /// The verdict carries the hook name and every suppressing script id for diagnostics.
    /// Entries are matched by message reference, so two identical lines never retract each other.
    /// </summary>
    public void NoteSuppressedChat(
        ChatMessageReceived message, string hookName, IReadOnlyList<string> scriptIds)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookName);
        ArgumentNullException.ThrowIfNull(scriptIds);
        lock (_chatGate)
        {
            var kept = new Queue<TaggedChatLine>(_chatHistory.Count);
            string? text = null;
            while (_chatHistory.Count > 0)
            {
                TaggedChatLine entry = _chatHistory.Dequeue();
                if (text is null && ReferenceEquals(entry.Message, message))
                {
                    text = entry.Line.Text;
                    continue;
                }

                kept.Enqueue(entry);
            }

            while (kept.Count > 0)
                _chatHistory.Enqueue(kept.Dequeue());

            _suppressedChats.Enqueue(new BeaconSuppressedChat(
                text ?? string.Empty, hookName, scriptIds.ToList(), DateTimeOffset.UtcNow));
            while (_suppressedChats.Count > BeaconSuppressionBounds.LogMax)
                _suppressedChats.Dequeue();
        }
    }

    /// <summary>Recent <c>stop event</c> suppressions, oldest first (capped, diagnostics only).</summary>
    public IReadOnlyList<BeaconSuppressedChat> SuppressedChats
    {
        get
        {
            lock (_chatGate)
                return _suppressedChats.ToList();
        }
    }

    /// <inheritdoc />
    public async Task SayAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_isMuted())
        {
            _noteSuppressed();
            return;
        }

        await _client.Game.Chat.SendAsync(text, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WhisperAsync(string player, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        ArgumentNullException.ThrowIfNull(text);
        if (_isMuted())
        {
            _noteSuppressed();
            return;
        }

        await _client.Game.Chat.SendAsync($"/msg {player} {text}", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        string line = commandLine.StartsWith('/') ? commandLine : "/" + commandLine;
        await _client.Game.Chat.SendAsync(line, ct).ConfigureAwait(false);
        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        // Dispatch takes the already-unprefixed form, but scripts spell the documented slash form (mcc "/scripts list"): strip one leading slash so both spellings reach the same command instead of the slashed one routing to the server.
        string line = commandLine.StartsWith('/') ? commandLine[1..] : commandLine;
        Commands.CmdResult result = await _client.Commands.DispatchAsync(line, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Message ?? result.Status.ToString());

        return result.Message ?? result.Status.ToString();
    }

    /// <inheritdoc />
    public string? SelfName => _client.CurrentSession?.Profile.Name;

    /// <inheritdoc />
    public int? GameProtocol
    {
        get
        {
            try
            {
                Task<SessionInfoSnapshot> work = _client.Game.Session.GetInfoAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                return work.GetAwaiter().GetResult().Protocol;
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
                return null;
            }
        }
    }

    private PlayerStatus? TryStatus()
    {
        try
        {
            Task<PlayerStatus> work = _client.Game.Player.GetStatusAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    private PlayerPose? TryPose()
    {
        try
        {
            Task<PlayerPose> work = _client.Game.Movement.GetPoseAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    private SessionInfoSnapshot? TrySessionInfo()
    {
        try
        {
            Task<SessionInfoSnapshot> work = _client.Game.Session.GetInfoAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public BeaconVitals? SelfVitals
    {
        get
        {
            PlayerStatus? status = TryStatus();
            return status is null
                ? null
                : new BeaconVitals(status.Health, null, status.Food, status.Saturation, null, status.ExperienceLevel, null);
        }
    }

    /// <inheritdoc />
    public BeaconPosition? SelfPosition
    {
        get
        {
            PlayerStatus? status = TryStatus();
            if (status is null)
                return null;

            PlayerPose? pose = TryPose();
            return new BeaconPosition(
                status.Position.X, status.Position.Y, status.Position.Z,
                pose?.Yaw, pose?.Pitch);
        }
    }

    /// <inheritdoc />
    public string? Gamemode => TryStatus()?.GameMode;

    /// <inheritdoc />
    public int? Ping => TrySessionInfo()?.ObservedLatencyMs;

    /// <inheritdoc />
    public bool? IsSneaking => TryPose()?.Sneaking;

    /// <inheritdoc />
    public IReadOnlyList<BeaconEffectInfo> PlayerEffects
    {
        get
        {
            try
            {
                Task<IReadOnlyList<EffectSnapshot>> work =
                    _client.Game.Player.GetEffectsAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                return work.GetAwaiter().GetResult()
                    .Select(e => new BeaconEffectInfo(e.EffectId, e.Level, e.Duration / 20))
                    .ToList();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
                return [];
            }
        }
    }

    /// <inheritdoc />
    public BeaconServerInfo? ServerInfo
    {
        get
        {
            SessionInfoSnapshot? info = TrySessionInfo();
            if (info is null)
                return null;

            long? dayTime = null;
            long? day = null;
            try
            {
                Task<WorldTimeInfo> work = _client.Game.World.GetTimeAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                WorldTimeInfo time = work.GetAwaiter().GetResult();
                dayTime = time.DayTime;
                day = time.Day;
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
            }

            return new BeaconServerInfo(
                info.Host, info.Port, info.VersionName, null, null, dayTime, day, null, null);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> OnlinePlayers(int limit)
    {
        try
        {
            // Same boundary as CommandContext.Run: the session-loop guard raises instead of hanging when this very call is what stops the loop (an event handler reading session state inline).
            // Reads then degrade to empty rather than deadlocking.
            Task<TabListSnapshot> work = _client.Game.Player.GetTabListAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult().Entries
                .Select(e => e.Name).Take(Math.Max(0, limit)).ToList();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public double? ServerTps
    {
        get
        {
            try
            {
                Task<SessionInfoSnapshot> work = _client.Game.Session.GetInfoAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                return work.GetAwaiter().GetResult().TpsEstimate;
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ChatHistory(int limit)
        => ChatHistoryDetailed(limit).Select(l => l.Text).ToList();

    /// <inheritdoc />
    public IReadOnlyList<BeaconChatLine> ChatHistoryDetailed(int limit)
    {
        int take = Math.Clamp(limit, 0, BeaconReadBounds.ChatHistoryMax);
        lock (_chatGate)
            return _chatHistory.TakeLast(take).Select(e => e.Line).ToList();
    }

    /// <inheritdoc />
    public string? LastFrom(string player)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        lock (_chatGate)
        {
            return _chatHistory
                .LastOrDefault(entry => entry.Line.Text.Contains(player, StringComparison.OrdinalIgnoreCase))?.Line.Text;
        }
    }

    /// <inheritdoc />
    public BeaconBlockInfo? GetBlock(int x, int y, int z)
    {
        try
        {
            Task<BlockInfo> work = _client.Game.World.GetBlockAsync(new BlockPos(x, y, z), CancellationToken.None);
            _client.WaitForCommandWork(work);
            BlockInfo info = work.GetAwaiter().GetResult();
            return info.ChunkLoaded ? new BeaconBlockInfo(info.BlockId, info.StateId) : null;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BeaconBlockPos>> FindBlocksAsync(
        string nameOrId, int radius, int maxResults, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);
        try
        {
            IReadOnlyList<BlockPos> found = await _client.Game.World
                .FindBlocksAsync(nameOrId, radius, maxResults, ct).ConfigureAwait(false);
            return found.Select(p => new BeaconBlockPos(p.X, p.Y, p.Z)).ToList();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public string? GetSignText(int x, int y, int z)
    {
        try
        {
            Task<string?> work = _client.Game.World.GetSignTextAsync(new BlockPos(x, y, z), CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public int? GetLight(int x, int y, int z)
    {
        try
        {
            Task<int?> work = _client.Game.World.GetLightAsync(new BlockPos(x, y, z), CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? GetBiome(int x, int y, int z)
    {
        try
        {
            Task<string?> work = _client.Game.World.GetBiomeIdAsync(new BlockPos(x, y, z), CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BeaconSignInfo>> FindSignsAsync(
        string needle, int radius, int maxResults, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(needle);
        try
        {
            IReadOnlyList<SignInfo> found = await _client.Game.World
                .FindSignsAsync(needle, radius, maxResults, ct).ConfigureAwait(false);
            return found.Select(s => new BeaconSignInfo(s.Position.X, s.Position.Y, s.Position.Z, s.Text)).ToList();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or ArgumentOutOfRangeException or DmcbkFeatureDisabledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public BeaconScoreboard Scoreboard
    {
        get
        {
            try
            {
                Task<ScoreboardSnapshot> work = _client.Game.Player.GetScoreboardAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                ScoreboardSnapshot board = work.GetAwaiter().GetResult();
                return new BeaconScoreboard(
                    board.Objectives.Select(o => new BeaconObjectiveInfo(
                        o.Name, o.DisplayName,
                        new Dictionary<string, int>(o.Scores, StringComparer.Ordinal))).ToList(),
                    board.Teams.Select(t => new BeaconTeamInfo(
                        t.Name, t.DisplayName, t.Members.ToList())).ToList());
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
                return BeaconScoreboard.Empty;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BeaconBossBarInfo> BossBars
    {
        get
        {
            try
            {
                Task<IReadOnlyList<BossBarSnapshot>> work =
                    _client.Game.Player.GetBossBarsAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                return work.GetAwaiter().GetResult()
                    .Select(b => new BeaconBossBarInfo(b.Title, b.Progress, b.Color))
                    .ToList();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException)
            {
                return [];
            }
        }
    }

    private PlayerInventorySnapshot? TryPlayerInventory()
    {
        try
        {
            Task<PlayerInventorySnapshot> work =
                _client.Game.Inventory.GetPlayerInventoryAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return null;
        }
    }

    private static BeaconInvSlot ToBeaconSlot(int index, ItemStackInfo stack)
    {
        if (stack.IsEmpty)
            return new BeaconInvSlot(index, "minecraft:air", "Air", 0, null);

        string shortName = stack.ItemId.Contains(':')
            ? stack.ItemId[(stack.ItemId.IndexOf(':') + 1)..]
            : stack.ItemId;
        string display = string.IsNullOrWhiteSpace(stack.CustomName)
            ? shortName.Replace('_', ' ')
            : stack.CustomName;
        return new BeaconInvSlot(index, stack.ItemId, display, stack.Count, null);
    }

    private static BeaconContainerInfo ToBeaconContainer(OpenContainerSnapshot container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var slots = new List<BeaconInvSlot>(container.Slots.Count);
        for (int i = 0; i < container.Slots.Count; i++)
            slots.Add(ToBeaconSlot(i, container.Slots[i]));

        return new BeaconContainerInfo(
            container.WindowId, container.Title, container.MenuType, slots);
    }

    private OpenContainerSnapshot? GetOpenContainerSnapshot()
    {
        Task<OpenContainerSnapshot?> work =
            _client.Game.Inventory.GetOpenContainerAsync(CancellationToken.None);
        _client.WaitForCommandWork(work);
        return work.GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<EntitySnapshot>> TryGetNearbyAsync(CancellationToken ct)
    {
        try
        {
            return await _client.Game.Entities.NearbyAsync(64, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BeaconInvSlot> InventorySlots
    {
        get
        {
            PlayerInventorySnapshot? snapshot = TryPlayerInventory();
            if (snapshot is null)
                return [];

            var slots = new List<BeaconInvSlot>(snapshot.Slots.Count);
            for (int i = 0; i < snapshot.Slots.Count; i++)
                slots.Add(ToBeaconSlot(i, snapshot.Slots[i]));

            return slots;
        }
    }

    /// <inheritdoc />
    public int SelectedSlot
    {
        get
        {
            try
            {
                Task<int> work = _client.Game.Inventory.GetHeldSlotAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                return work.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, BeaconInvSlot?> ArmorSlots
    {
        get
        {
            // Vanilla player-window order: 5 head, 6 chest, 7 legs, 8 feet, 45 offhand.
            PlayerInventorySnapshot? snapshot = TryPlayerInventory();
            var worn = new Dictionary<string, BeaconInvSlot?>(StringComparer.Ordinal);
            if (snapshot is null)
                return worn;

            foreach ((string key, int index) in new[] { ("head", 5), ("chest", 6), ("legs", 7), ("feet", 8), ("offhand", 45) })
            {
                if (index < 0 || index >= snapshot.Slots.Count)
                {
                    worn[key] = null;
                    continue;
                }

                ItemStackInfo stack = snapshot.Slots[index];
                worn[key] = stack.IsEmpty ? null : ToBeaconSlot(index, stack);
            }

            return worn;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SelectSlotAsync(int slot, CancellationToken ct = default)
    {
        if (slot is < 0 or > 8)
            return false;

        try
        {
            await _client.Game.Inventory.SelectHeldSlotAsync(slot, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DropAsync(bool stack, CancellationToken ct = default)
    {
        try
        {
            PlayerInventorySnapshot? snapshot = TryPlayerInventory();
            if (snapshot is null)
                return false;

            int held = 36 + snapshot.HeldSlot;
            if (held < 0 || held >= snapshot.Slots.Count || snapshot.Slots[held].IsEmpty)
                return false;

            await _client.Game.Inventory.DropAsync(held, stack, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MoveAsync(int from, string to, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        try
        {
            PlayerInventorySnapshot? snapshot = TryPlayerInventory();
            if (snapshot is null)
                return false;

            if (from < 0 || from >= snapshot.Slots.Count)
                return false;

            if (string.Equals(to.Trim(), "offhand", StringComparison.OrdinalIgnoreCase))
            {
                // Vanilla swap gesture: the held hotbar slot against the offhand (button 40).
                int held = 36 + snapshot.HeldSlot;
                if (held < 0 || held >= snapshot.Slots.Count)
                    return false;

                await _client.Game.Inventory
                    .ClickAsync(new ClickAction.Swap(held, 40), ct).ConfigureAwait(false);
                return true;
            }

            if (!int.TryParse(to.Trim(), out int target) || target < 0 || target >= snapshot.Slots.Count)
                return false;

            if (target == from)
                return true;

            await _client.Game.Inventory
                .ClickAsync(new ClickAction.Pickup(from, MouseButton.Left), ct).ConfigureAwait(false);
            await _client.Game.Inventory
                .ClickAsync(new ClickAction.Pickup(target, MouseButton.Left), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClickAsync(int slot, string mode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        MouseButton button = mode.Trim().Equals("right", StringComparison.OrdinalIgnoreCase)
            ? MouseButton.Right
            : MouseButton.Left;
        try
        {
            await _client.Game.Inventory
                .ClickAsync(new ClickAction.Pickup(slot, button), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CraftList()
    {
        try
        {
            Task<RecipeBookSnapshot> work = _client.Game.Inventory.GetRecipeBookAsync(CancellationToken.None);
            _client.WaitForCommandWork(work);
            RecipeBookSnapshot book = work.GetAwaiter().GetResult();
            return book.Recipes.Concat(book.RecipeIds.Select(id => id.ToString())).ToList();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<bool> CraftOneAsync(string recipe, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);
        try
        {
            return await _client.Game.Inventory
                .PlaceRecipeByNameAsync(Umpk.Identifier.Parse(recipe.Trim()), false, null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException or FormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public BeaconContainerInfo? OpenContainer
    {
        get
        {
            try
            {
                OpenContainerSnapshot? container = GetOpenContainerSnapshot();
                if (container is null)
                    return null;

                return ToBeaconContainer(container);
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TakeFromContainerAsync(int slot, int count, CancellationToken ct = default)
    {
        try
        {
            OpenContainerSnapshot? container = GetOpenContainerSnapshot();
            int containerOnly = container is null ? 0 : Math.Max(0, container.Slots.Count - 36);
            if (container is null || slot < 0 || slot >= containerOnly)
                return false;

            ItemStackInfo stack = container.Slots[slot];
            if (stack.IsEmpty)
                return false;

            int want = Math.Min(count, stack.Count);
            if (want <= 0)
                return false;

            if (want >= stack.Count)
            {
                await _client.Game.Inventory.QuickMoveAsync(slot, ct).ConfigureAwait(false);
                return true;
            }

            // Exact count: left-pickup the stack, right-click place-one onto an empty player slot want times, then left-click the source to shelve the rest.
            // Every click awaits its ack, so each step observes the previous one.
            Task<PlayerInventorySnapshot> invWork =
                _client.Game.Inventory.GetPlayerInventoryAsync(CancellationToken.None);
            _client.WaitForCommandWork(invWork);
            PlayerInventorySnapshot inv = invWork.GetAwaiter().GetResult();
            if (!inv.Cursor.IsEmpty)
                return false;

            int? target = FindEmptyPlayerSlot(inv, containerOnly);
            if (target is null)
                return false;

            await _client.Game.Inventory.PickupAsync(slot, MouseButton.Left, ct).ConfigureAwait(false);
            for (int i = 0; i < want; i++)
                await _client.Game.Inventory.PickupAsync(target.Value, MouseButton.Right, ct).ConfigureAwait(false);

            await _client.Game.Inventory.PickupAsync(slot, MouseButton.Left, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return false;
        }
    }

    /// <summary>
    /// First empty player slot in window index space (container slots first, then main 9-35, then hotbar 36-44), or null when the player has nowhere to put things.
    /// </summary>
    private static int? FindEmptyPlayerSlot(PlayerInventorySnapshot inv, int containerSlotCount)
    {
        ArgumentNullException.ThrowIfNull(inv);
        for (int id = 9; id <= 44; id++)
        {
            if (id < 0 || id >= inv.Slots.Count || !inv.Slots[id].IsEmpty)
                continue;

            int windowSlot = id <= 35
                ? containerSlotCount + (id - 9)
                : containerSlotCount + 27 + (id - 36);
            return windowSlot;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> PutIntoContainerAsync(int slot, CancellationToken ct = default)
    {
        try
        {
            OpenContainerSnapshot? container = GetOpenContainerSnapshot();
            if (container is null)
                return false;

            // Container windows list container slots first, then player main (9-35), then hotbar (36-44); crafting, armor, and offhand never ride along.
            // The snapshot carries the full window (container plus the trailing 36 player slots), so the container-only prefix is total minus 36.
            int containerOnly = Math.Max(0, container.Slots.Count - 36);
            int windowSlot = slot switch
            {
                >= 9 and <= 35 => containerOnly + (slot - 9),
                >= 36 and <= 44 => containerOnly + 27 + (slot - 36),
                _ => -1,
            };
            if (windowSlot < 0)
                return false;

            await _client.Game.Inventory.QuickMoveAsync(windowSlot, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task MoveToAsync(double x, double y, double z, CancellationToken ct = default)
    {
        try
        {
            await _client.Game.Movement
                .NavigateToAsync(new BlockPos((int)x, (int)y, (int)z), 1, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("move_goto", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task FollowPlayerAsync(string player, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        while (!ct.IsCancellationRequested)
        {
            Vec3d? position = await FindPlayerPositionAsync(player, ct).ConfigureAwait(false);
            if (position is null)
                throw Refused("move_follow", $"player '{player}' is not tracked nearby");

            try
            {
                await _client.Game.Movement.NavigateToAsync(
                    BlockPos.Containing(position.Value), 2, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                throw Refused("move_follow", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    private async Task<Vec3d?> FindPlayerPositionAsync(string player, CancellationToken ct)
    {
        IReadOnlyList<EntitySnapshot> nearby = await TryGetNearbyAsync(ct).ConfigureAwait(false);
        foreach (EntitySnapshot entity in nearby)
        {
            if (string.Equals(entity.PlayerName, player, StringComparison.OrdinalIgnoreCase))
                return entity.Position;
        }

        return null;
    }

    /// <inheritdoc />
    public Task StopMovingAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task LookAtAsync(double x, double y, double z, CancellationToken ct = default)
    {
        try
        {
            await _client.Game.Movement.LookAtAsync(new Vec3d(x, y, z), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("look_at", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task AttackAsync(string target, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        int? id = await FindEntityIdAsync(target, ct).ConfigureAwait(false);
        if (id is null)
            throw Refused("attack", $"no entity or player '{target}' is tracked nearby");

        try
        {
            await _client.Game.Entities.AttackAsync(id.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("attack", ex.Message);
        }
    }

    private async Task<int?> FindEntityIdAsync(string target, CancellationToken ct)
    {
        IReadOnlyList<EntitySnapshot> nearby = await TryGetNearbyAsync(ct).ConfigureAwait(false);
        foreach (EntitySnapshot entity in nearby)
        {
            if (string.Equals(entity.PlayerName, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.CustomName, target, StringComparison.OrdinalIgnoreCase)
                || entity.TypeId.Contains(target, StringComparison.OrdinalIgnoreCase))
                return entity.Id;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task InteractAsync(string target, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        int? id = await FindEntityIdAsync(target, ct).ConfigureAwait(false);
        if (id is null)
            throw Refused("interact", $"no entity or player '{target}' is tracked nearby");

        await InteractEntityAsync(id.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<BeaconEntityInfo> NearbyEntities(double radius)
    {
        try
        {
            Task<IReadOnlyList<EntitySnapshot>> work =
                _client.Game.Entities.NearbyAsync(radius, CancellationToken.None);
            _client.WaitForCommandWork(work);
            return work.GetAwaiter().GetResult().Select(ProjectEntity).ToList();
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public BeaconEntityInfo? EntityById(int entityId)
    {
        try
        {
            Task<EntitySnapshot?> work = _client.Game.Entities.ByIdAsync(entityId, CancellationToken.None);
            _client.WaitForCommandWork(work);
            EntitySnapshot? found = work.GetAwaiter().GetResult();
            return found is null ? null : ProjectEntity(found);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task AttackEntityAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            EntitySnapshot? tracked = await _client.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (tracked is null)
                throw Refused("attack", $"no entity id {entityId} is tracked nearby");

            await _client.Game.Entities.AttackAsync(entityId, ct).ConfigureAwait(false);
        }
        catch (BeaconRuntimeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("attack", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task InteractEntityAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            EntitySnapshot? tracked = await _client.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (tracked is null)
                throw Refused("interact", $"no entity id {entityId} is tracked nearby");

            await _client.Game.Entities.InteractAsync(entityId, Hand.Main, ct).ConfigureAwait(false);
        }
        catch (BeaconRuntimeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("interact", ex.Message);
        }
    }

    private static BeaconEntityInfo ProjectEntity(EntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BeaconEntityInfo(
            entity.Id,
            entity.Uuid.ToString(),
            entity.TypeId,
            entity.Position.X,
            entity.Position.Y,
            entity.Position.Z,
            entity.Yaw,
            entity.Pitch,
            entity.Pose,
            entity.OnGround,
            entity.PlayerName,
            entity.CustomName,
            IsPlayer: !string.IsNullOrEmpty(entity.PlayerName)
                || string.Equals(entity.TypeId, "minecraft:player", StringComparison.Ordinal));
    }

    /// <summary>The merchant result slot in window space (vanilla MerchantMenu: 0,1 pay, 2 take).</summary>
    private const int MerchantResultSlot = 2;

    private static string? ShortMenuKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        string trimmed = key.Trim();
        int colon = trimmed.IndexOf(':');
        string shortKey = colon >= 0 ? trimmed[(colon + 1)..] : trimmed;
        return shortKey.ToLowerInvariant();
    }

    private static bool IsMerchantWindow(OpenContainerSnapshot container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (string.Equals(ShortMenuKey(container.MenuType), "merchant", StringComparison.Ordinal))
            return true;

        string legacy = container.LegacyWindowType ?? string.Empty;
        return legacy.Contains("merchant", StringComparison.OrdinalIgnoreCase)
            || legacy.Contains("villager", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnchantingWindow(OpenContainerSnapshot container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (string.Equals(ShortMenuKey(container.MenuType), "enchantment", StringComparison.Ordinal))
            return true;

        string legacy = container.LegacyWindowType ?? string.Empty;
        return legacy.Contains("enchant", StringComparison.OrdinalIgnoreCase);
    }

    private static BeaconTradeItem ToTradeItem(ItemStackInfo stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        string type = string.IsNullOrEmpty(stack.ItemId) ? "minecraft:air" : stack.ItemId;
        string shortName = type.Contains(':') ? type[(type.IndexOf(':') + 1)..] : type;
        string display = string.IsNullOrWhiteSpace(stack.CustomName)
            ? shortName.Replace('_', ' ')
            : stack.CustomName;
        return new BeaconTradeItem(type, display, stack.Count);
    }

    /// <inheritdoc />
    public IReadOnlyList<BeaconTradeOffer>? OpenTrades
    {
        get
        {
            OpenContainerSnapshot? container;
            try
            {
                container = GetOpenContainerSnapshot();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return null;
            }

            if (container is null || !IsMerchantWindow(container))
                return null;

            if (container.Trades is null)
                return [];

            var offers = new List<BeaconTradeOffer>(container.Trades.Count);
            for (int i = 0; i < container.Trades.Count; i++)
            {
                TradeOfferInfo offer = container.Trades[i];
                offers.Add(new BeaconTradeOffer(
                    i,
                    ToTradeItem(offer.FirstCost),
                    offer.SecondCost is null ? null : ToTradeItem(offer.SecondCost),
                    ToTradeItem(offer.Result),
                    offer.Uses,
                    offer.MaxUses,
                    offer.IsSoldOut,
                    offer.Xp));
            }

            return offers;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SelectTradeAsync(int tradeIndex, CancellationToken ct = default)
    {
        OpenContainerSnapshot? container;
        try
        {
            container = await _client.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("trade.select", ex.Message);
        }

        if (container is null || !IsMerchantWindow(container))
        {
            throw Refused(
                "trade.select",
                "no merchant window is open (right-click a villager with interact, then wait for on container_open)");
        }

        if (container.Trades is null || tradeIndex < 0 || tradeIndex >= container.Trades.Count)
            return false;

        try
        {
            await _client.Game.Inventory.SelectTradeAsync(tradeIndex, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("trade.select", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<int> BuyTradeAsync(int tradeIndex, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return 0;

        int done = 0;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            OpenContainerSnapshot? container;
            try
            {
                container = await _client.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                throw Refused("trade.buy", ex.Message);
            }

            if (container is null || !IsMerchantWindow(container))
            {
                throw Refused(
                    "trade.buy",
                    "no merchant window is open (right-click a villager with interact, then wait for on container_open)");
            }

            if (container.Trades is null || tradeIndex < 0 || tradeIndex >= container.Trades.Count)
                return done;

            if (container.Trades[tradeIndex].IsSoldOut)
                return done;

            try
            {
                await _client.Game.Inventory.SelectTradeAsync(tradeIndex, ct).ConfigureAwait(false);
                if (!await WaitForTradeResultAsync(container.WindowId, ct).ConfigureAwait(false))
                    return done;

                // Exactly one trade per unit: left-click takes the result to the cursor (one input set consumed), then the cursor is parked in the first empty player slot.
                // A shift-click would repeat while inputs last, which is why this never quick-moves the result slot.
                PlayerInventorySnapshot inv =
                    await _client.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
                if (!inv.Cursor.IsEmpty)
                    return done;

                int containerOnly = Math.Max(0, container.Slots.Count - 36);
                int? target = FindEmptyPlayerSlot(inv, containerOnly);
                if (target is null)
                    return done;

                await _client.Game.Inventory.PickupAsync(MerchantResultSlot, MouseButton.Left, ct).ConfigureAwait(false);
                inv = await _client.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
                if (inv.Cursor.IsEmpty)
                    return done;

                await _client.Game.Inventory.PickupAsync(target.Value, MouseButton.Left, ct).ConfigureAwait(false);
                done++;
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                throw Refused("trade.buy", ex.Message);
            }
        }

        return done;
    }

    private async Task<bool> WaitForTradeResultAsync(int windowId, CancellationToken ct)
    {
        // The result slot fills from the server's select-trade ack, not from our send: wait for a non-empty result before shift-clicking, so a click never moves air.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            OpenContainerSnapshot? container;
            try
            {
                container = await _client.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return false;
            }

            if (container is null || container.WindowId != windowId)
                return false;

            if (container.Slots.Count > MerchantResultSlot
                && !container.Slots[MerchantResultSlot].IsEmpty)
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<BeaconEnchantOption>? EnchantOptions
    {
        get
        {
            OpenContainerSnapshot? container;
            try
            {
                container = GetOpenContainerSnapshot();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return null;
            }

            if (container is null || !IsEnchantingWindow(container))
                return null;

            // Vanilla container data 0,1,2 carries the top/middle/bottom XP costs on every era.
            var options = new List<BeaconEnchantOption>(3);
            for (int slot = 0; slot < 3; slot++)
            {
                options.Add(new BeaconEnchantOption(
                    slot,
                    container.Properties.TryGetValue(slot, out int level) ? level : null));
            }

            return options;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ChooseEnchantAsync(int slot, CancellationToken ct = default)
    {
        if (slot is < 0 or > 2)
            return false;

        OpenContainerSnapshot? container;
        try
        {
            container = await _client.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("enchant.choose", ex.Message);
        }

        if (container is null || !IsEnchantingWindow(container))
        {
            throw Refused(
                "enchant.choose",
                "no enchanting table is open (right-click one with world.use, then wait for on container_open)");
        }

        try
        {
            await _client.Game.Inventory.ClickContainerButtonAsync(slot, container.WindowId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("enchant.choose", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string? reason, CancellationToken ct = default)
    {
        _ = reason;
        try
        {
            await _client.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException)
        {
            throw Refused("disconnect", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task UseInHandAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.Game.World.UseItemAsync(ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("use_in_hand", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<BeaconDigResult> DigAsync(int x, int y, int z, string face, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(face);
        try
        {
            DigOutcome outcome = await _client.Game.World
                .DigBlockAsync(new BlockPos(x, y, z), ParseFace(face), ct).ConfigureAwait(false);
            return outcome switch
            {
                DigOutcome.Broken => new BeaconDigResult(true, "broken (observed air afterwards)"),
                DigOutcome.NotBroken => new BeaconDigResult(false, "not broken (the server sent the block back)"),
                _ => new BeaconDigResult(false, "unconfirmed (no air read and no refusal arrived)"),
            };
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("world.dig", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> PlaceAsync(int x, int y, int z, string face, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(face);
        try
        {
            Direction direction = ParseFace(face);
            // Fractional face-center cursor (0..1 in-block): the wire codec documents CursorX/Y/Z as fractions, and absolute coordinates are acked but silently ignored by the server.
            // Same derivation as WorldCommandText.FaceHitCursor.
            var cursor = new Vec3d(
                0.5 + direction.StepX() * 0.5,
                0.5 + direction.StepY() * 0.5,
                0.5 + direction.StepZ() * 0.5);
            await _client.Game.World
                .PlaceBlockAsync(new BlockPos(x, y, z), direction, cursor, Hand.Main, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("world.place", ex.Message);
        }
    }

    /// <inheritdoc />
    public BeaconRaycastHit? Raycast(double maxDistance)
    {
        try
        {
            Task<BlockRaycastHit?> work = _client.Game.World.RaycastAsync(
                Math.Clamp(maxDistance, 0.5, 32), false, CancellationToken.None);
            _client.WaitForCommandWork(work);
            BlockRaycastHit? hit = work.GetAwaiter().GetResult();
            return hit is null
                ? null
                : new BeaconRaycastHit(
                    hit.Position.X, hit.Position.Y, hit.Position.Z, hit.BlockId, hit.Distance);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<BeaconContainerInfo?> OpenContainerAtAsync(int x, int y, int z, CancellationToken ct = default)
    {
        try
        {
            OpenContainerSnapshot? container = await _client.Game.Inventory
                .OpenContainerAtAsync(new BlockPos(x, y, z), ct).ConfigureAwait(false);
            if (container is null)
                return null;

            return ToBeaconContainer(container);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            return null;
        }
    }

    private static Direction ParseFace(string face) => face.Trim().ToLowerInvariant() switch
    {
        "down" => Direction.Down,
        "north" => Direction.North,
        "south" => Direction.South,
        "west" => Direction.West,
        "east" => Direction.East,
        _ => Direction.Up,
    };

    private readonly object _dialogGate = new();
    private readonly Dictionary<string, string> _stagedDialogInputs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public BeaconDialogInfo? CurrentDialog
    {
        get
        {
            DialogSnapshot? snapshot;
            try
            {
                Task<DialogSnapshot?> work = _client.Game.Dialogs.GetCurrentAsync(CancellationToken.None);
                _client.WaitForCommandWork(work);
                snapshot = work.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return null;
            }

            if (snapshot is null)
                return null;

            return ToBeaconDialog(snapshot, SnapshotStaged());
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> StagedDialogInputs => SnapshotStaged();

    /// <inheritdoc />
    public bool StageDialogInput(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        BeaconDialogInfo? current = CurrentDialog;
        if (current is null)
            throw Refused("dialog.set", "no dialog is open (wait for on dialog first)");

        bool known = false;
        foreach (BeaconDialogInput input in current.Inputs)
        {
            if (string.Equals(input.Key, key, StringComparison.Ordinal))
            {
                known = true;
                break;
            }
        }

        if (!known)
            return false;

        lock (_dialogGate)
            _stagedDialogInputs[key] = value;

        return true;
    }

    /// <inheritdoc />
    public void ClearStagedDialogInputs()
    {
        lock (_dialogGate)
            _stagedDialogInputs.Clear();
    }

    /// <inheritdoc />
    public async Task<bool> AnswerDialogAsync(
        IReadOnlyDictionary<string, string>? values, int buttonOneBased, CancellationToken ct = default)
    {
        DialogSnapshot? snapshot;
        try
        {
            snapshot = await _client.Game.Dialogs.GetCurrentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("dialog.answer", ex.Message);
        }

        if (snapshot is null)
            throw Refused("dialog.answer", "no dialog is open (wait for on dialog first)");

        if (snapshot.RegistryId is not null)
        {
            throw Refused(
                "dialog.answer",
                "the open dialog is a registry reference with no readable body here");
        }

        if (buttonOneBased < 1 || buttonOneBased > snapshot.Buttons.Count)
            return false;

        IReadOnlyDictionary<string, string> staged = values ?? SnapshotStaged();
        var submitted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DialogInputInfo input in snapshot.Inputs)
        {
            if (staged.TryGetValue(input.Key, out string? value))
                submitted[input.Key] = value;
        }

        DialogButtonInfo button = snapshot.Buttons[buttonOneBased - 1];
        DialogClickOutcome outcome;
        try
        {
            outcome = await _client.Game.Dialogs
                .ClickAsync(buttonOneBased - 1, submitted, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("dialog.answer", ex.Message);
        }
        finally
        {
            ClearStagedDialogInputs();
        }

        return outcome switch
        {
            DialogClickOutcome.ActionSent => true,
            DialogClickOutcome.CommandSent => true,
            DialogClickOutcome.ClosedOnly => true,
            DialogClickOutcome.ActionNotPerformed => throw Refused(
                "dialog.answer",
                $"button {buttonOneBased} ({button.Label}) carries a {button.ActionKind} action this client cannot perform"),
            _ => false,
        };
    }

    /// <inheritdoc />
    public async Task CancelDialogAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.Game.Dialogs.CancelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
            or OperationCanceledException or DmcbkFeatureDisabledException)
        {
            throw Refused("dialog.close", ex.Message);
        }
        finally
        {
            ClearStagedDialogInputs();
        }
    }

    private Dictionary<string, string> SnapshotStaged()
    {
        lock (_dialogGate)
            return new Dictionary<string, string>(_stagedDialogInputs, StringComparer.Ordinal);
    }

    private static BeaconDialogInfo ToBeaconDialog(
        DialogSnapshot snapshot, IReadOnlyDictionary<string, string> staged)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(staged);
        var inputs = new List<BeaconDialogInput>(snapshot.Inputs.Count);
        foreach (DialogInputInfo input in snapshot.Inputs)
        {
            inputs.Add(new BeaconDialogInput(
                input.Key,
                input.Label,
                input.Kind,
                staged.TryGetValue(input.Key, out string? value) ? value : input.InitialValue));
        }

        var buttons = new List<BeaconDialogButton>(snapshot.Buttons.Count);
        for (int i = 0; i < snapshot.Buttons.Count; i++)
            buttons.Add(new BeaconDialogButton(i + 1, snapshot.Buttons[i].Label));

        return new BeaconDialogInfo(
            snapshot.Title, string.Join("\n", snapshot.Body), inputs, buttons, snapshot.RegistryId);
    }

    private static BeaconRuntimeException Refused(string verb, string reason) => new(
        BeaconDiagnosticCodes.WorldWriteGate,
        $"{verb} refused: {reason}.",
        new SourceSpan("live.mcc", 1, 1, 0),
        $"Wrap it in try/catch err and read err.message.");

    /// <inheritdoc />
    public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

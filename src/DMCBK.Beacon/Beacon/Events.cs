using Microsoft.Extensions.Logging;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Fake-double field factories: one per v1 hook, mapping a source payload to the documented event field map.
/// Tests build fields here; the live session wiring maps real session sources the same way (see <see cref="BeaconEventBus"/> for the source table).
/// Null-means-unknown (TPS) surfaces as <see cref="BeaconValue.None"/>, never zero.
/// </summary>
public static class BeaconEventFields
{
    private static void ValidatePlayerMessage(string player, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        ArgumentNullException.ThrowIfNull(message);
    }

    private static Dictionary<string, BeaconValue> PlayerMessageMap(string player, string message)
    {
        ValidatePlayerMessage(player, message);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text(player),
            ["message"] = BeaconValue.Text(message),
        };
    }

    private static Dictionary<string, BeaconValue> ReasonMap(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["reason"] = BeaconValue.Text(reason),
        };
    }

    /// <summary>Builds <c>chat</c> fields: player, message, raw, is_private.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Chat(
        string player, string message, string? raw = null, bool isPrivate = false)
    {
        var map = PlayerMessageMap(player, message);
        map["raw"] = BeaconValue.Text(raw ?? message);
        map["is_private"] = BeaconValue.YesNo(isPrivate);
        return map;
    }

    /// <summary>Builds <c>whisper</c> fields: player, message, raw.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Whisper(string player, string message, string? raw = null)
    {
        var map = PlayerMessageMap(player, message);
        map["raw"] = BeaconValue.Text(raw ?? message);
        return map;
    }

    /// <summary>Builds <c>server_message</c> fields: text, translation_key (none when plain).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> ServerMessage(string text, string? translationKey = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["text"] = BeaconValue.Text(text),
            ["translation_key"] = translationKey is null ? BeaconValue.None : BeaconValue.Text(translationKey),
        };
    }

    /// <summary>Builds <c>raw_chat</c> fields: raw, category, sender, sender_id, chat_type, target, body, translation_key, verified.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> RawChat(
        string raw,
        string category,
        string? sender,
        string? senderId,
        int chatTypeId,
        string? target,
        string body,
        string? translationKey,
        bool verified)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(body);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["raw"] = BeaconValue.Text(raw),
            ["category"] = BeaconValue.Text(category),
            ["sender"] = string.IsNullOrWhiteSpace(sender) ? BeaconValue.None : BeaconValue.Text(sender),
            ["sender_id"] = string.IsNullOrWhiteSpace(senderId) ? BeaconValue.None : BeaconValue.Text(senderId),
            ["chat_type"] = chatTypeId < 0 ? BeaconValue.None : BeaconValue.Number(chatTypeId),
            ["target"] = string.IsNullOrWhiteSpace(target) ? BeaconValue.None : BeaconValue.Text(target),
            ["body"] = BeaconValue.Text(body),
            ["translation_key"] = translationKey is null ? BeaconValue.None : BeaconValue.Text(translationKey),
            ["verified"] = BeaconValue.YesNo(verified),
        };
    }

    /// <summary>Builds <c>join</c> fields: player.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Join(string player)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text(player),
        };
    }

    /// <summary>Builds <c>leave</c> fields: player.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Leave(string player)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text(player),
        };
    }

    /// <summary>Builds <c>death</c> fields: player, cause.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Death(string player, string cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        ArgumentNullException.ThrowIfNull(cause);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text(player),
            ["cause"] = BeaconValue.Text(cause),
        };
    }

    /// <summary>Builds <c>respawn</c> fields: player.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Respawn(string player)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["player"] = BeaconValue.Text(player),
        };
    }

    /// <summary>Builds <c>health</c> fields: health, max_health, change.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Health(double health, double maxHealth, double change)
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["health"] = BeaconValue.Number(health),
            ["max_health"] = BeaconValue.Number(maxHealth),
            ["change"] = BeaconValue.Number(change),
        };

    /// <summary>Builds <c>hunger</c> fields: food, saturation, change.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Hunger(double food, double saturation, double change)
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["food"] = BeaconValue.Number(food),
            ["saturation"] = BeaconValue.Number(saturation),
            ["change"] = BeaconValue.Number(change),
        };

    /// <summary>Builds <c>inventory</c> fields: slots_changed (list of numbers).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Inventory(IReadOnlyList<int> slotsChanged)
    {
        ArgumentNullException.ThrowIfNull(slotsChanged);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["slots_changed"] = BeaconValue.List(slotsChanged.Select(s => BeaconValue.Number(s)).ToList<BeaconValue>()),
        };
    }

    /// <summary>Builds <c>login</c> fields: none (lifecycle).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Login()
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

    /// <summary>Builds <c>logout</c> fields: none (lifecycle).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Logout()
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

    /// <summary>Builds <c>start</c> fields: none (lifecycle).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Start()
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

    /// <summary>Builds <c>disconnect</c> fields: reason.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Disconnect(string reason)
        => ReasonMap(reason);

    /// <summary>Builds <c>reconnect</c> fields: none (lifecycle).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Reconnect()
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

    /// <summary>Builds <c>kick</c> fields: reason (a disconnect with a kick reason).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Kick(string reason)
        => ReasonMap(reason);

    /// <summary>Builds <c>tps</c> fields: tps, mspt (null means unknown, surfaced as none).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Tps(double? tps, double? mspt)
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["tps"] = tps is double t ? BeaconValue.Number(t) : BeaconValue.None,
            ["mspt"] = mspt is double m ? BeaconValue.Number(m) : BeaconValue.None,
        };

    /// <summary>Builds <c>player_list</c> fields: players, count, header, footer.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> PlayerList(
        IReadOnlyList<string> players, string? header = null, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["players"] = BeaconValue.List(players.Select(BeaconValue.Text).ToList<BeaconValue>()),
            ["count"] = BeaconValue.Number(players.Count),
            ["header"] = header is null ? BeaconValue.None : BeaconValue.Text(header),
            ["footer"] = footer is null ? BeaconValue.None : BeaconValue.Text(footer),
        };
    }

    /// <summary>Builds <c>container_open</c> fields: window, title, kind.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> ContainerOpen(
        int window, string? title = null, string? kind = null)
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["window"] = BeaconValue.Number(window),
            ["title"] = title is null ? BeaconValue.None : BeaconValue.Text(title),
            ["kind"] = kind is null ? BeaconValue.None : BeaconValue.Text(kind),
        };

    /// <summary>Builds <c>container_close</c> fields: window.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> ContainerClose(int window)
        => new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["window"] = BeaconValue.Number(window),
        };

    /// <summary>
    /// Builds one entity row map shared by the <c>entities.*</c> builtins and the <c>entity_add</c>/<c>entity_remove</c> fields: id, uuid, type, name, custom_name, is_player, x, y, z, distance, yaw, pitch, pose, on_ground.
    /// </summary>
    public static IReadOnlyDictionary<string, BeaconValue> EntityRow(BeaconEntityInfo entity, double? distance)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["id"] = BeaconValue.Number(entity.Id),
            ["uuid"] = BeaconValue.Text(entity.Uuid),
            ["type"] = BeaconValue.Text(entity.TypeId),
            ["name"] = entity.PlayerName is null ? BeaconValue.None : BeaconValue.Text(entity.PlayerName),
            ["custom_name"] = entity.CustomName is null ? BeaconValue.None : BeaconValue.Text(entity.CustomName),
            ["is_player"] = BeaconValue.YesNo(entity.IsPlayer),
            ["x"] = BeaconValue.Number(entity.X),
            ["y"] = BeaconValue.Number(entity.Y),
            ["z"] = BeaconValue.Number(entity.Z),
            ["distance"] = distance is double d ? BeaconValue.Number(Math.Round(d, 1)) : BeaconValue.None,
            ["yaw"] = BeaconValue.Number(entity.Yaw),
            ["pitch"] = BeaconValue.Number(entity.Pitch),
            ["pose"] = BeaconValue.Text(entity.Pose),
            ["on_ground"] = BeaconValue.YesNo(entity.OnGround),
        };
    }

    /// <summary>Builds <c>entity_add</c> fields: the appeared entity's row.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> EntityAdd(BeaconEntityInfo entity, double? distance)
        => EntityRow(entity, distance);

    /// <summary>Builds <c>entity_remove</c> fields: the last known row of the gone entity.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> EntityRemove(BeaconEntityInfo entity, double? distance)
        => EntityRow(entity, distance);

    /// <summary>Builds <c>dialog</c> fields: title, body, inputs, buttons, input_keys, registry_id.</summary>
    public static IReadOnlyDictionary<string, BeaconValue> Dialog(
        string title,
        IReadOnlyList<string> body,
        IReadOnlyList<(string Key, string? Label, string Kind, string Value)> inputs,
        IReadOnlyList<(int Index, string Label)> buttons,
        int? registryId = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(buttons);
        return new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["title"] = BeaconValue.Text(title ?? string.Empty),
            ["body"] = BeaconValue.Text(string.Join("\n", body)),
            ["inputs"] = BeaconValue.List(inputs.Select(i => BeaconValue.Map(
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["key"] = BeaconValue.Text(i.Key),
                    ["label"] = i.Label is null ? BeaconValue.None : BeaconValue.Text(i.Label),
                    ["kind"] = BeaconValue.Text(i.Kind),
                    ["value"] = BeaconValue.Text(i.Value),
                })).ToList<BeaconValue>()),
            ["buttons"] = BeaconValue.List(buttons.Select(b => BeaconValue.Map(
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["index"] = BeaconValue.Number(b.Index),
                    ["label"] = BeaconValue.Text(b.Label),
                })).ToList<BeaconValue>()),
            ["input_keys"] = BeaconValue.List(inputs.Select(i => BeaconValue.Text(i.Key)).ToList<BeaconValue>()),
            ["registry_id"] = registryId is int id ? BeaconValue.Number(id) : BeaconValue.None,
        };
    }

    /// <summary>Measures eye-to-entity distance from a self position, or null when unknown.</summary>
    public static double? EntityDistance(BeaconEntityInfo entity, BeaconPosition? self)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (self is null)
            return null;

        double dx = entity.X - self.X;
        double dy = entity.Y - self.Y;
        double dz = entity.Z - self.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// Pure render-distance diff for the <c>entity_add</c>/<c>entity_remove</c> poll: ids present now but not before are added, ids gone are removed (with their last known rows).
/// Snapshots stay immutable; the polling caller owns the baseline.
/// </summary>
public static class BeaconEntityWatch
{
    /// <summary>Diffs two snapshots by entity id.</summary>
    public static void Diff(
        IReadOnlyList<BeaconEntityInfo> before,
        IReadOnlyList<BeaconEntityInfo> after,
        out List<BeaconEntityInfo> added,
        out List<BeaconEntityInfo> removed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeById = new Dictionary<int, BeaconEntityInfo>(before.Count);
        foreach (BeaconEntityInfo entity in before)
            beforeById[entity.Id] = entity;

        var afterById = new Dictionary<int, BeaconEntityInfo>(after.Count);
        foreach (BeaconEntityInfo entity in after)
            afterById[entity.Id] = entity;

        added = [];
        removed = [];
        foreach (BeaconEntityInfo entity in after)
        {
            if (!beforeById.ContainsKey(entity.Id))
                added.Add(entity);
        }

        foreach (BeaconEntityInfo entity in before)
        {
            if (!afterById.ContainsKey(entity.Id))
                removed.Add(entity);
        }
    }
}

/// <summary>
/// Immutable-snapshot copies for event dispatch.
/// Handlers reason about a moment, not a moving world: the bus deep-copies every incoming field map at <c>FireEvent</c> entry, so a source mutating its payload (or the next event overwriting it) mid-handler, including across <c>wait</c>, can never change what the handler sees.
/// </summary>
public static class BeaconEventSnapshot
{
    /// <summary>Deep-copies a field map (owned immutable copy).</summary>
    public static IReadOnlyDictionary<string, BeaconValue> CopyFields(IReadOnlyDictionary<string, BeaconValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, BeaconValue> kv in fields)
            copy[kv.Key] = CopyValue(kv.Value);

        return copy;
    }

    /// <summary>Deep-copies one value (lists and maps recurse; scalars are immutable and shared).</summary>
    public static BeaconValue CopyValue(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconListValue list => BeaconValue.List(list.Items.Select(CopyValue).ToList()),
            BeaconMapValue map => BeaconValue.Map(new Dictionary<string, BeaconValue>(
                map.Entries.ToDictionary(kv => kv.Key, kv => CopyValue(kv.Value), StringComparer.Ordinal),
                StringComparer.Ordinal)),
            _ => value,
        };
    }
}

/// <summary>
/// Evaluates one registered <c>on</c> block against an owned snapshot.
/// <see cref="BeaconEngine"/> supplies this as per-block <see cref="BeaconInterpreter.InvokeHandlerAsync"/> (which runs the <c>when</c> filter through the runtime evaluator and reports <c>stop event</c> via <see cref="BeaconRunResult.EventSuppressed"/>).
/// </summary>
/// <param name="scriptId">The owning script (for per-script globals and throttle windows).</param>
/// <param name="block">The exact registered block to run (carries alias, cooldown, filter).</param>
/// <param name="snapshot">The owned immutable field map (plus alias entry); must not be mutated.</param>
/// <param name="ct">A cancellation token.</param>
public delegate Task<BeaconRunResult> BeaconEventInvoker(
    string scriptId, OnBlock block, IReadOnlyDictionary<string, BeaconValue> snapshot, CancellationToken ct);

/// <summary>
/// The standalone Beacon event runtime: handler registry, snapshot dispatch, per-script throttles, and cancellable chat.
/// The <c>/scripts</c> runtime maps session sources to <see cref="FireEventAsync"/>; this class never touches transport, auth, or the session loop itself.
/// </summary>
/// <remarks>
/// <para>Source wiring (subscribe off-loop, dispatch snapshots onto the scheduler):</para>
/// <list type="table">
/// <item><term>chat / whisper / server_message</term><description><c>ChatApi.MessageReceived</c> (fields: player, message, raw, is_private; text plus translation_key for system). Off-loop subscribe; snapshot per message.</description></item>
/// <item><term>raw_chat</term><description><c>ChatApi.MessageReceived</c> raw path, fired alongside the parsed dispatch (fields: raw, category, sender, sender_id, chat_type, target, body, translation_key, verified). Never suppressible.</description></item>
/// <item><term>join / leave</term><description>Player join/leave notices.</description></item>
/// <item><term>death (player, cause) / respawn</term><description>Death/respawn notices.</description></item>
/// <item><term>health (health, max_health, change) / hunger (food, saturation, change)</term><description>Vitals polling via <c>PlayerApi.GetStatusAsync</c> with a change threshold plus a cooldown clause; see the catalog cost notes.</description></item>
/// <item><term>inventory (slots_changed)</term><description><c>InventoryApi</c> container events. Raised on the session loop: the handler must not block on session work.</description></item>
/// <item><term>login / disconnect / reconnect</term><description>Supervisor <c>StatusChanged</c> plus plugin <c>SessionStarted/Ended</c>.</description></item>
/// <item><term>kick (reason)</term><description>A disconnect with a kick reason.</description></item>
/// <item><term>tps (tps, mspt; null means unknown)</term><description><c>SessionApi</c> TPS estimate.</description></item>
/// </list>
/// <para>
/// Scoping: <c>as v</c> binds the event map to <c>v</c> (default <c>event</c>); bare fields win over globals inside handlers (the fixed evaluation order).
/// <c>as</c>-alias member paths (<c>e.message</c>) work in every expression position.
/// <c>event</c> (+ <c>server</c>, <c>shared</c> reads) is admitted in <c>CanStartExpr</c>, so a bare <c>event.message</c> after <c>show</c>/<c>when</c>/etc. parses and the full <c>event.field</c> form always works (interpolation holes already worked via the ungated sub-parser).
/// Blocks live at the top level only (the parser rejects nesting) and reload per script (<see cref="RegisterScriptHandlers"/> replaces one script's set and resets its throttle windows).
/// </para>
/// <para>Snapshot rule: incoming field maps are treated as owned immutable copies.</para>
/// <para><c>BeaconEngine.FireEventAsync</c> copies through <c>BeaconScheduler.CopyEventSnapshot</c> before dispatch; this bus also defensively deep-copies inside <c>FireEventAsync</c> (see <c>BeaconEventSnapshot</c>), so either layer alone guarantees immutability across <c>wait</c>.</para>
/// <para>
/// Filters stay ordinary boolean expressions over the event record plus globals (<c>on chat when message contains "!help"</c>), evaluated by the runtime evaluator inside the invoker.
/// An empty <c>when</c> is a static error at lint (the parser reports <c>B0001</c>); this bus performs no extra grammar of its own.
/// </para>
/// <para>Cancellation: <c>stop event</c> on a suppressible hook (<c>chat</c>, <c>whisper</c>, <c>server_message</c>) sets <see cref="BeaconFireResult.Suppressed"/> so the <c>/scripts</c> runtime can suppress the triggering message; the forgiven <c>cancel event</c> spelling already normalizes in desugar and the linter emits its nudge.</para>
/// <para>Ordering matters (fixed control flow): <c>stop event</c> aborts the handler immediately, so notifying verbs must precede it and statements after it never run.</para>
/// <para>On any other hook <c>stop event</c> is a documented no-op recorded in <see cref="BeaconFireResult.SuppressionNotes"/>, never silent.</para>
/// </remarks>
public sealed class BeaconEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<OnBlock>> _handlers = new(StringComparer.Ordinal);
    private readonly BeaconThrottleRegistry _throttles;
    private readonly BeaconEventInvoker _invoker;
    private readonly ILogger<BeaconEventBus>? _logger;

    /// <summary>
    /// Builds a bus over an interpreter-eval callback and a clock.
    /// </summary>
    /// <param name="invoker">Per-block evaluator (per-script <c>InvokeHandlerAsync</c>).</param>
    /// <param name="clock">Throttle clock (virtual in tests).</param>
    /// <param name="logger">Optional logger for throttle-skip Debug lines.</param>
    public BeaconEventBus(
        BeaconEventInvoker invoker,
        IVirtualClock? clock = null,
        ILogger<BeaconEventBus>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
        _throttles = new BeaconThrottleRegistry(clock ?? SystemClock.Shared);
        _logger = logger;
    }

    /// <summary>The throttle clock.</summary>
    public IVirtualClock Clock => _throttles.Clock;

    /// <summary>Currently registered script ids.</summary>
    public IReadOnlyCollection<string> ScriptIds
    {
        get
        {
            lock (_gate)
                return [.. _handlers.Keys];
        }
    }

    /// <summary>Total registered handler blocks.</summary>
    public int HandlerCount
    {
        get
        {
            lock (_gate)
                return _handlers.Values.Sum(list => list.Count);
        }
    }

    /// <summary>True when at least one handler listens for <paramref name="eventName"/> (poll skip).</summary>
    public bool HasHandlers(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        lock (_gate)
        {
            foreach (List<OnBlock> list in _handlers.Values)
            {
                foreach (OnBlock block in list)
                {
                    if (string.Equals(block.EventName, eventName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Registers one handler block.
    /// Returns a <c>B2001</c> warning for unknown hook names (with the full catalog listing); the block still registers so plugin hooks warn until their plugin loads.
    /// </summary>
    public BeaconDiagnostic? RegisterHandler(string scriptId, OnBlock block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(block);
        lock (_gate)
        {
            if (!TryGetListLocked(scriptId, out List<OnBlock>? list) || list is null)
            {
                list = [];
                _handlers[scriptId] = list;
            }

            list.Add(block);
        }

        return BeaconHookCatalog.ValidateHook(block.EventName, block.EventSpan);
    }

    /// <summary>
    /// Replaces every handler for <paramref name="scriptId"/> (hot-reloadable individually) and resets that script's throttle windows (RAM-only).
    /// Unknown names report per-block warnings.
    /// </summary>
    public void RegisterScriptHandlers(
        string scriptId, IEnumerable<OnBlock> blocks, out IReadOnlyList<BeaconDiagnostic> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(blocks);
        var list = blocks.ToList();
        lock (_gate)
            _handlers[scriptId] = new List<OnBlock>(list);

        _throttles.ResetScript(scriptId);
        var found = new List<BeaconDiagnostic>();
        foreach (OnBlock block in list)
        {
            BeaconDiagnostic? warning = BeaconHookCatalog.ValidateHook(block.EventName, block.EventSpan);
            if (warning is not null)
                found.Add(warning);
        }

        warnings = found;
    }

    /// <summary>Removes one handler block; false when not found. Throttle windows are kept.</summary>
    public bool UnregisterHandler(string scriptId, OnBlock block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(block);
        lock (_gate)
        {
            if (!TryGetListLocked(scriptId, out List<OnBlock>? list) || list is null)
                return false;

            return list.Remove(block);
        }
    }

    private bool TryGetListLocked(string scriptId, out List<OnBlock>? list)
        => _handlers.TryGetValue(scriptId, out list);

    /// <summary>Removes every handler for <paramref name="scriptId"/> and resets its throttles.</summary>
    public bool UnregisterScript(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        bool removed;
        lock (_gate)
            removed = _handlers.Remove(scriptId);

        _throttles.ResetScript(scriptId);
        return removed;
    }

    /// <summary>Removes every handler and clears every throttle (primarily for tests).</summary>
    public void Clear()
    {
        lock (_gate)
            _handlers.Clear();

        _throttles.Clear();
    }

    /// <summary>Resets one script's throttle windows (reload semantics) without touching handlers.</summary>
    public void ResetThrottle(string scriptId) => _throttles.ResetScript(scriptId);

    /// <summary>
    /// Fires <paramref name="eventName"/> to every matching handler in registration order.
    /// </summary>
    /// <remarks>
    /// The incoming <paramref name="fields"/> are defensively deep-copied at entry (owned immutable copies), then each handler additionally receives its <c>as</c>-alias entry pointing at the event map.
    /// A false <c>when</c> filter skips quietly inside the invoker; a firing inside a cooldown window is skipped plus debug-logged without invoking.
    /// Pass <paramref name="scriptId"/> to reach only that script's handlers (the <c>start</c> load hook); session events stay broadcast by passing nothing, so every loaded script sees them.
    /// </remarks>
    public async Task<BeaconFireResult> FireEventAsync(
        string eventName,
        IReadOnlyDictionary<string, BeaconValue> fields,
        CancellationToken ct = default,
        string? scriptId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(fields);

        IReadOnlyDictionary<string, BeaconValue> owned = BeaconEventSnapshot.CopyFields(fields);

        List<(string ScriptId, OnBlock Block)> matches;
        lock (_gate)
        {
            matches = [];
            foreach (KeyValuePair<string, List<OnBlock>> entry in _handlers)
            {
                if (scriptId is not null
                    && !string.Equals(entry.Key, scriptId, StringComparison.Ordinal))
                    continue;

                foreach (OnBlock block in entry.Value)
                {
                    if (string.Equals(block.EventName, eventName, StringComparison.OrdinalIgnoreCase))
                        matches.Add((entry.Key, block));
                }
            }
        }

        var handlerOutcomes = new List<BeaconHandlerFire>(matches.Count);
        var diagnostics = new List<BeaconDiagnostic>();
        var debugLog = new List<string>();
        var suppressionNotes = new List<string>();
        bool suppressed = false;

        foreach ((string ScriptId, OnBlock Block) match in matches)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = new Dictionary<string, BeaconValue>(owned, StringComparer.Ordinal);
            string alias = match.Block.Alias ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(alias)
                && !string.Equals(alias, "event", StringComparison.Ordinal))
                snapshot[alias] = BeaconValue.Map(new Dictionary<string, BeaconValue>(owned, StringComparer.Ordinal));

            TimeSpan? window = BeaconThrottleRegistry.ResolveWindow(match.Block.Cooldown);
            if (window.HasValue && window.Value > TimeSpan.Zero)
            {
                string throttleName = match.Block.Cooldown!.Name;
                if (!string.IsNullOrWhiteSpace(throttleName))
                {
                    if (!_throttles.TryAcquire(match.ScriptId, throttleName, window.Value, out TimeSpan remaining))
                    {
                        string line = BeaconThrottleRegistry.BuildSkipMessage(match.ScriptId, throttleName, remaining);
                        debugLog.Add(line);
                        _logger?.LogDebug("{Line}", line);
                        handlerOutcomes.Add(new BeaconHandlerFire(
                            match.ScriptId, match.Block, Throttled: true, remaining, Result: null));
                        continue;
                    }
                }
            }

            BeaconRunResult result = await _invoker(match.ScriptId, match.Block, snapshot, ct).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (result.EventSuppressed)
            {
                if (BeaconHookCatalog.IsSuppressible(eventName))
                    suppressed = true;
                else
                {
                    suppressionNotes.Add(
                        $"stop event on '{eventName}' is a no-op (only " +
                        $"{string.Join(", ", BeaconHookCatalog.SuppressibleHooks)} can suppress a message); " +
                        $"the handler for script '{match.ScriptId}' still stopped.");
                }
            }

            handlerOutcomes.Add(new BeaconHandlerFire(
                match.ScriptId, match.Block, Throttled: false, TimeSpan.Zero, result));
        }

        return new BeaconFireResult(eventName, suppressed, handlerOutcomes, diagnostics, debugLog, suppressionNotes);
    }
}

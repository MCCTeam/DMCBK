namespace DMCBK.Core.Beacon;

/// <summary>One event field: its bare name, value kind, and meaning.</summary>
/// <param name="Name">The bare field name in handler scope (also reachable as <c>event.name</c>).</param>
/// <param name="Kind">The value kind (<c>text</c>, <c>number</c>, <c>yes/no</c>, <c>list</c>, <c>map</c>, <c>none</c>).</param>
/// <param name="Description">What the field carries.</param>
public sealed record BeaconHookField(string Name, string Kind, string Description);

/// <summary>One hook schema: the event name, its fields, provenance, and suppression behavior.</summary>
/// <param name="Name">The hook name used in <c>on name</c>.</param>
/// <param name="Description">What fires this hook.</param>
/// <param name="Provenance">The session source the live wiring maps into fields.</param>
/// <param name="Suppressible">True when <c>stop event</c> suppresses the triggering message.</param>
/// <param name="Fields">The documented field table.</param>
/// <param name="CostNote">Polling or loop cost the hook reference must disclose, when any.</param>
public sealed record BeaconHookSchema(
    string Name,
    string Description,
    string Provenance,
    bool Suppressible,
    IReadOnlyList<BeaconHookField> Fields,
    string? CostNote = null);

/// <summary>
/// The closed Beacon hook catalog: the event names plus their field tables.
/// </summary>
/// <remarks>
/// <para>
/// Unknown <c>on</c> names emit <c>B2001</c> as a warning (established behavior in <see cref="BeaconStaticCheck"/>: loads anyway, reported but non-blocking), carrying the full catalog listing.
/// Plugin-registered names (populated by the provider registry) live in the extensible custom registry (<see cref="RegisterCustomHook(string)"/>) and validate clean once registered; until their plugin loads they warn like any other unknown hook.
/// </para>
/// <para>
/// Packet-level hooks are explicitly out of scope: entity movement, packet capture, and per-packet hooks stay in C# plugins, and the unknown-hook message says so.
/// </para>
/// <para>
/// <c>start</c> and <c>logout</c> are valid lifecycle hooks usable via <c>on</c> blocks, matching <see cref="BeaconStaticCheck"/> tolerance and the lifecycle list (<c>start</c>, <c>login</c>, <c>logout</c>, <c>disconnect</c>, <c>reconnect</c>).
/// Both carry no fields.
/// </para>
/// </remarks>
public static class BeaconHookCatalog
{
    private static object Gate => BeaconEnvironment.Current.Gate;
    private static Dictionary<string, BeaconHookSchema> CustomHooks => BeaconEnvironment.Current.CustomHooks;

    /// <summary>The closed v1 set, in catalog order.</summary>
    public static IReadOnlyList<string> KnownHooks { get; } =
    [
        "chat", "whisper", "server_message", "raw_chat", "join", "leave", "death", "respawn",
        "health", "hunger", "inventory", "login", "logout", "disconnect", "reconnect", "kick", "tps",
        "start", "player_list", "container_open", "container_close", "entity_add", "entity_remove",
        "dialog",
    ];

    /// <summary>Comma-joined hook list for diagnostics.</summary>
    public static string HookListText => string.Join(", ", KnownHooks);

    /// <summary>Hooks whose triggering message <c>stop event</c> can suppress.</summary>
    public static IReadOnlyList<string> SuppressibleHooks { get; } =
    [
        "chat", "whisper", "server_message",
    ];

    /// <summary>The documented field table per hook (case-insensitive lookup).</summary>
    public static IReadOnlyDictionary<string, BeaconHookSchema> Schemas { get; } = BuildSchemas();

    /// <summary>Currently registered plugin hook names (the provider registry populates this).</summary>
    public static IReadOnlyCollection<string> CustomHookNames
    {
        get
        {
            lock (Gate)
                return [.. CustomHooks.Keys];
        }
    }

    /// <summary>True for a v1 hook or a registered custom hook (case-insensitive).</summary>
    public static bool IsKnown(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (Schemas.ContainsKey(eventName))
            return true;

        lock (Gate)
            return CustomHooks.ContainsKey(eventName);
    }

    /// <summary>True when <c>stop event</c> suppresses the triggering message for this hook.</summary>
    public static bool IsSuppressible(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (Schemas.TryGetValue(eventName, out BeaconHookSchema? schema) && schema is not null)
            return schema.Suppressible;

        lock (Gate)
        {
            if (CustomHooks.TryGetValue(eventName, out BeaconHookSchema? custom) && custom is not null)
                return custom.Suppressible;
        }

        return false;
    }

    /// <summary>Looks up the schema for a v1 or custom hook; false when unknown.</summary>
    public static bool TryGetSchema(string eventName, out BeaconHookSchema? schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (Schemas.TryGetValue(eventName, out BeaconHookSchema? found) && found is not null)
        {
            schema = found;
            return true;
        }

        lock (Gate)
        {
            if (CustomHooks.TryGetValue(eventName, out BeaconHookSchema? custom) && custom is not null)
            {
                schema = custom;
                return true;
            }
        }

        schema = null;
        return false;
    }

    /// <summary>
    /// Registers a plugin hook name.
    /// The minimal schema carries no fields and is non-suppressible; use <see cref="RegisterCustomHook(BeaconHookSchema)"/> for a full table.
    /// Idempotent: re-registering a name replaces its schema.
    /// </summary>
    public static void RegisterCustomHook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RegisterCustomHook(new BeaconHookSchema(
            name.Trim(), $"Plugin-registered hook '{name.Trim()}'.", "Plugin-registered.",
            Suppressible: false, Fields: []));
    }

    /// <summary>
    /// Registers a plugin hook with its full field table.
    /// Idempotent per name.
    /// </summary>
    public static void RegisterCustomHook(BeaconHookSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema.Name);
        lock (Gate)
            CustomHooks[schema.Name] = schema;
    }

    /// <summary>Removes a plugin hook registration; false when the name was not registered.</summary>
    public static bool UnregisterCustomHook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate)
            return CustomHooks.Remove(name);
    }

    /// <summary>Clears all plugin hook registrations (primarily for tests; RAM-only like throttles).</summary>
    public static void ClearCustomHooks()
    {
        lock (Gate)
            CustomHooks.Clear();
    }

    /// <summary>
    /// Validates an <c>on</c> hook name: null when known, else a <c>B2001</c> warning carrying the full catalog listing plus the C# plugin pointer.
    /// Never an error: unknown hooks load anyway and warn until their plugin loads.
    /// </summary>
    public static BeaconDiagnostic? ValidateHook(string eventName, SourceSpan span)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(span);
        if (IsKnown(eventName))
            return null;

        return new BeaconDiagnostic(
            BeaconDiagnosticCodes.UnknownEvent,
            BeaconSeverity.Warning,
            $"Unknown event '{eventName}'. Known hooks: {HookListText}. " +
            "Plugin-registered hooks (like shop_buy) warn until their plugin loads. " +
            "Entity movement, packet capture, and per-packet hooks stay in C# plugins.",
            span,
            $"Use one of: {HookListText}.");
    }

    private static Dictionary<string, BeaconHookSchema> BuildSchemas()
    {
        var schemas = new Dictionary<string, BeaconHookSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat"] = new BeaconHookSchema(
                "chat",
                "A public chat message from another player.",
                "ChatApi.MessageReceived (GetText path).",
                Suppressible: true,
                Fields:
                [
                    new("player", "text", "The sender's player name."),
                    new("message", "text", "The plain-text message body."),
                    new("raw", "text", "The parsed-view trimmed line (raw_chat.raw carries the untouched line)."),
                    new("is_private", "yes/no", "Always no for public chat; yes when relayed privately."),
                ]),
            ["whisper"] = new BeaconHookSchema(
                "whisper",
                "A private message to this client.",
                "Private-message parsers over ChatApi.MessageReceived.",
                Suppressible: true,
                Fields:
                [
                    new("player", "text", "The sender's player name."),
                    new("message", "text", "The plain-text message body."),
                    new("raw", "text", "The full trimmed plain line."),
                ]),
            ["server_message"] = new BeaconHookSchema(
                "server_message",
                "A system message from the server (no player sender).",
                "System chat path (text plus translation_key).",
                Suppressible: true,
                Fields:
                [
                    new("text", "text", "The rendered system text."),
                    new("translation_key", "text", "The vanilla translation key, or none when plain."),
                ]),
            ["raw_chat"] = new BeaconHookSchema(
                "raw_chat",
                "Every inbound chat line, untouched, alongside the parsed chat/whisper/server_message dispatch.",
                "ChatApi.MessageReceived (raw path, alongside the parsed dispatch).",
                Suppressible: false,
                Fields:
                [
                    new("raw", "text", "The untouched plain-text line as the server sent it."),
                    new("category", "text", "The chat category (legacy, player, system, or disguised)."),
                    new("sender", "text|none", "The sender name, or none when the line has no sender."),
                    new("sender_id", "text|none", "The sender id, or none when the line has no sender id."),
                    new("chat_type", "number|none", "The chat type registry id, or none when the era carries none."),
                    new("target", "text|none", "The whisper or team target name, or none."),
                    new("body", "text", "The undecorated message body."),
                    new("translation_key", "text|none", "The vanilla translation key, or none when plain."),
                    new("verified", "yes/no", "Yes when the signature verified, otherwise no."),
                ]),
            ["join"] = new BeaconHookSchema(
                "join",
                "A player joined the server.",
                "Player join (OnPlayerJoin).",
                Suppressible: false,
                Fields:
                [
                    new("player", "text", "The joining player's name."),
                ]),
            ["leave"] = new BeaconHookSchema(
                "leave",
                "A player left the server.",
                "Player leave (OnPlayerLeave).",
                Suppressible: false,
                Fields:
                [
                    new("player", "text", "The leaving player's name."),
                ]),
            ["death"] = new BeaconHookSchema(
                "death",
                "A player died.",
                "Death (OnDeath/OnKilled).",
                Suppressible: false,
                Fields:
                [
                    new("player", "text", "The player who died."),
                    new("cause", "text", "The death cause text."),
                ]),
            ["respawn"] = new BeaconHookSchema(
                "respawn",
                "A player respawned.",
                "Respawn (OnRespawn).",
                Suppressible: false,
                Fields:
                [
                    new("player", "text", "The respawned player's name."),
                ]),
            ["health"] = new BeaconHookSchema(
                "health",
                "Our health changed past the change threshold.",
                "Vitals polling via PlayerApi.GetStatusAsync with a change-threshold subscription.",
                Suppressible: false,
                Fields:
                [
                    new("health", "number", "Current health."),
                    new("max_health", "number", "Maximum health."),
                    new("change", "number", "Health delta since the last fire (negative when hurt)."),
                ],
                CostNote: "Polled vitals: pair with a cooldown clause (e.g. cooldown 10 seconds) so damage bursts do not spam the handler."),
            ["hunger"] = new BeaconHookSchema(
                "hunger",
                "Our food or saturation changed past the change threshold.",
                "Food/vitals path via PlayerApi.GetStatusAsync.",
                Suppressible: false,
                Fields:
                [
                    new("food", "number", "Current food level."),
                    new("saturation", "number", "Current saturation."),
                    new("change", "number", "Food delta since the last fire."),
                ],
                CostNote: "Polled vitals: pair with a cooldown clause so drift does not spam the handler."),
            ["inventory"] = new BeaconHookSchema(
                "inventory",
                "Container or player-inventory slots changed.",
                "InventoryApi container events (subscribe off-loop; dispatch snapshots onto the scheduler; never block the session loop).",
                Suppressible: false,
                Fields:
                [
                    new("slots_changed", "list", "Changed slot indices (numbers)."),
                ],
                CostNote: "Fires on the session loop: the handler must not block on session work; hand anything slow to a background task."),
            ["login"] = new BeaconHookSchema(
                "login",
                "This client entered play (initial connect).",
                "Supervisor StatusChanged plus plugin SessionStarted (AfterGameJoined).",
                Suppressible: false,
                Fields: []),
            ["logout"] = new BeaconHookSchema(
                "logout",
                "This client logged out cleanly (not a dropped connection).",
                "Supervisor StatusChanged plus plugin SessionEnded (clean logout).",
                Suppressible: false,
                Fields: []),
            ["disconnect"] = new BeaconHookSchema(
                "disconnect",
                "This client's session ended.",
                "Supervisor StatusChanged plus plugin SessionEnded (OnDisconnect).",
                Suppressible: false,
                Fields:
                [
                    new("reason", "text", "The disconnect reason."),
                ]),
            ["reconnect"] = new BeaconHookSchema(
                "reconnect",
                "This client re-entered play after a drop.",
                "Supervisor reconnect.",
                Suppressible: false,
                Fields: []),
            ["start"] = new BeaconHookSchema(
                "start",
                "The script finished loading (top-level statements ran).",
                "Beacon engine after RunScriptAsync (lifecycle Start).",
                Suppressible: false,
                Fields: []),
            ["kick"] = new BeaconHookSchema(
                "kick",
                "This client was kicked (a disconnect with a kick reason).",
                "Disconnect-with-reason.",
                Suppressible: false,
                Fields:
                [
                    new("reason", "text", "The kick reason."),
                ]),
            ["tps"] = new BeaconHookSchema(
                "tps",
                "A server tick-rate sample arrived.",
                "SessionApi TPS estimate (null means unknown, surfaced as none).",
                Suppressible: false,
                Fields:
                [
                    new("tps", "number|none", "Measured ticks per second, or none when unknown."),
                    new("mspt", "number|none", "Milliseconds per tick, or none when unknown."),
                ],
                CostNote: "Sampled path: guard with a cooldown clause (e.g. cooldown 300 seconds named \"tps-warn\") so the warn-once pattern holds."),
            ["player_list"] = new BeaconHookSchema(
                "player_list",
                "The tab list changed: latency, gamemode, or header/footer text moved.",
                "PlayerApi tab list (OnLatencyUpdate, OnGamemodeUpdate, header/footer).",
                Suppressible: false,
                Fields:
                [
                    new("players", "list", "Current player names (text)."),
                    new("count", "number", "Current player count."),
                    new("header", "text|none", "Tab header text, or none when empty."),
                    new("footer", "text|none", "Tab footer text, or none when empty."),
                ],
                CostNote: "Churny on hubs: pair with a cooldown clause so queue-position bots do not spam the handler."),
            ["container_open"] = new BeaconHookSchema(
                "container_open",
                "A container window opened (chest, furnace, merchant, ...).",
                "InventoryApi container events (OnInventoryOpen).",
                Suppressible: false,
                Fields:
                [
                    new("window", "number", "The open window id."),
                    new("title", "text|none", "The container title, or none when unknown."),
                    new("kind", "text|none", "The semantic menu kind, or none when unknown."),
                ]),
            ["container_close"] = new BeaconHookSchema(
                "container_close",
                "The open container window closed.",
                "InventoryApi container events (OnInventoryClose).",
                Suppressible: false,
                Fields:
                [
                    new("window", "number", "The closed window id."),
                ]),
            ["entity_add"] = new BeaconHookSchema(
                "entity_add",
                "A new entity entered client tracking range (render distance).",
                "Entity poll diff over EntitiesApi.AllAsync with an id baseline (first poll only baselines).",
                Suppressible: false,
                Fields:
                [
                    new("id", "number", "The server-assigned entity id (unstable across reconnects)."),
                    new("uuid", "text", "The entity uuid."),
                    new("type", "text", "The namespaced entity type id."),
                    new("name", "text|none", "The player name, or none for non-players."),
                    new("custom_name", "text|none", "The rendered custom name, or none."),
                    new("is_player", "yes/no", "Yes for player entities."),
                    new("x", "number", "Position X."),
                    new("y", "number", "Position Y."),
                    new("z", "number", "Position Z."),
                    new("distance", "number|none", "Eye-to-entity distance, or none when our position is unknown."),
                    new("yaw", "number", "Body yaw in degrees."),
                    new("pitch", "number", "Pitch in degrees."),
                    new("pose", "text", "The pose name."),
                    new("on_ground", "yes/no", "Whether the entity was on the ground per the last update."),
                ],
                CostNote: "Bursty on login (every tracked entity fires once): pair with a when filter (e.g. when is_player is yes) and a cooldown clause so a busy spawn does not spam the handler."),
            ["entity_remove"] = new BeaconHookSchema(
                "entity_remove",
                "A tracked entity left range or despawned.",
                "Entity poll diff over EntitiesApi.AllAsync with an id baseline.",
                Suppressible: false,
                Fields:
                [
                    new("id", "number", "The server-assigned entity id."),
                    new("uuid", "text", "The entity uuid."),
                    new("type", "text", "The namespaced entity type id."),
                    new("name", "text|none", "The player name, or none for non-players."),
                    new("custom_name", "text|none", "The rendered custom name, or none."),
                    new("is_player", "yes/no", "Yes for player entities."),
                    new("x", "number", "Last known X."),
                    new("y", "number", "Last known Y."),
                    new("z", "number", "Last known Z."),
                    new("distance", "number|none", "Last known distance, or none when our position is unknown."),
                    new("yaw", "number", "Last known body yaw in degrees."),
                    new("pitch", "number", "Last known pitch in degrees."),
                    new("pose", "text", "The last known pose name."),
                    new("on_ground", "yes/no", "Whether the entity was on the ground per the last update."),
                ],
                CostNote: "Fires per despawn: pair with a when filter so chunk churn does not spam the handler."),
            ["dialog"] = new BeaconHookSchema(
                "dialog",
                "The server showed a dialog (1.21.6+), in configuration or in play.",
                "UMPK DialogShown via the session client (SessionCreated for configuration, Game.Events for play).",
                Suppressible: false,
                Fields:
                [
                    new("title", "text", "The dialog title."),
                    new("body", "text", "The body lines joined with newlines."),
                    new("inputs", "list", "Input rows ({key, label, kind, value})."),
                    new("buttons", "list", "Button rows ({index, label})."),
                    new("input_keys", "list", "The input keys in order (text)."),
                    new("registry_id", "number|none", "The registry index, or none for an inline dialog."),
                ],
                CostNote: "Filter with when input_keys contains \"your_key\" so unrelated dialogs do not run the handler."),
        };
        return schemas;
    }
}

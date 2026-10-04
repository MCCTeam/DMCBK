using Umpk.Client;

namespace DMCBK.Core;

/// <summary>
/// The live-session facade grouping the preserved feature surface (plan 1.2).
/// It wraps the UMPK client and never exposes it to hosts.
/// Every member is safe to call from any thread; snapshot reads marshal onto the UMPK session loop internally.
/// Members throw <see cref="MccNotInSessionException"/> while the client is not in a live session and <see cref="MccFeatureDisabledException"/> when a required gameplay feature is disabled.
/// </summary>
public sealed class GameApi
{
    private readonly GameSession _session;

    internal GameApi(
        ChatApi chat,
        WorldApi world,
        EntitiesApi entities,
        InventoryApi inventory,
        MovementApi movement,
        PlayerApi player,
        SessionApi session,
        DialogApi dialogs,
        MapApi maps,
        GameSession gameSession)
    {
        Chat = chat;
        World = world;
        Entities = entities;
        Inventory = inventory;
        Movement = movement;
        Player = player;
        Session = session;
        Dialogs = dialogs;
        Maps = maps;
        _session = gameSession;
    }

    /// <summary>The chat send/receive surface.</summary>
    public ChatApi Chat { get; }

    /// <summary>Block lookup/search, chunk status, raycast, time, border, dimension, dig/place/use.</summary>
    public WorldApi World { get; }

    /// <summary>Entity snapshots and attack/interact actions.</summary>
    public EntitiesApi Entities { get; }

    /// <summary>Inventory/container snapshots, clicks, held slot, creative set, trades.</summary>
    public InventoryApi Inventory { get; }

    /// <summary>Movement, pathfinding, rotation, sneak/sprint, swing.</summary>
    public MovementApi Movement { get; }

    /// <summary>Vitals, abilities, effects, respawn, tab list, scoreboard, boss bars, advancements.</summary>
    public PlayerApi Player { get; }

    /// <summary>Server/session facts and the client-settings send.</summary>
    public SessionApi Session { get; }

    /// <summary>The current server dialog (1.21.6+) and its serverbound press/submit/cancel responses.</summary>
    public DialogApi Dialogs { get; }

    /// <summary>Filled-map reads: scale/lock state and the full 128x128 color grid, by map id.</summary>
    public MapApi Maps { get; }

    /// <summary>
    /// The UMPK client's typed event bus for the live session.
    /// Throws <see cref="MccNotInSessionException"/> while there is no session.
    /// </summary>
    /// <remarks>
    /// Re-resolved on every access, so it always names the CURRENT session's bus.
    /// A host that caches the returned instance across a reconnect keeps a handle to the previous session's bus; read the property each time instead.
    /// </remarks>
    public ClientEvents Events => _session.Require().Events;
}

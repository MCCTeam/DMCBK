using Umpk.Game.Players;

namespace DMCBK.Core;

/// <summary>
/// The filled-map surface: reads one tracked map's scale/lock state and its full 128x128 color-index grid out of UMPK's <see cref="Umpk.Client.ClientState.Maps"/> (always present; no gameplay feature gate, mirroring how <see cref="PlayerApi.GetHeldItemAsync"/> is the only map-adjacent read that is feature-gated).
/// A host renders the grid through its own vanilla map-color palette (the palette itself is presentation data, not something the core carries); the core only hands back the raw bytes UMPK already decoded off the <c>map_item_data</c> packet.
/// </summary>
public sealed class MapApi
{
    private readonly GameSession _session;

    internal MapApi(GameSession session) => _session = session;

    /// <summary>
    /// Snapshots one filled map's scale, lock state, and full color-index grid (row-major, index = z*128+x;
    /// 128x128 <see cref="Umpk.Game.Players.MapData.Size"/>), or null when this session has not tracked that map id (no <c>map_item_data</c> packet for it since the world join).
    /// Loop-affine: copies the color grid on the UMPK session loop so a host never reads it mid-mutation.
    /// </summary>
    public Task<MapSnapshotInfo?> GetMapAsync(int mapId, CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            if (!client.State.Maps.TryGet(mapId, out MapData? map))
                return null;

            return new MapSnapshotInfo(map.MapId, map.Scale, map.Locked, map.Colors.ToArray());
        }, ct);

    /// <summary>The map ids this session has tracked at least one <c>map_item_data</c> packet for.</summary>
    public Task<IReadOnlyList<int>> GetKnownMapIdsAsync(CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            var ids = new List<int>(client.State.Maps.Count);
            foreach (MapData map in client.State.Maps.Maps)
                ids.Add(map.MapId);

            return (IReadOnlyList<int>)ids;
        }, ct);
}

/// <summary>
/// A filled map's scale/lock state and its full 128x128 color-index grid (vanilla map-color palette indices, row-major, index = z*128+x).
/// The host owns turning <see cref="Colors"/> into pixels.
/// </summary>
/// <param name="MapId">The map item id (the state key).</param>
/// <param name="Scale">The map scale (0-4; each step doubles the blocks-per-pixel).</param>
/// <param name="Locked">Whether the map is locked (cannot be updated by exploration).</param>
/// <param name="Colors">The 128x128 color-index grid, row-major.</param>
public sealed record MapSnapshotInfo(int MapId, byte Scale, bool Locked, byte[] Colors);

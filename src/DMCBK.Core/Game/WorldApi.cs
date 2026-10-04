using System.Text;
using System.Text.Json;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Data.Java;
using Umpk.Game.Blocks;
using Umpk.Game.Players;
using Umpk.Game.Registries;
using Umpk.Game.World;
using Umpk.Geometry;
using Umpk.Nbt;

namespace DMCBK.Core;

/// <summary>
/// The world/terrain surface: typed block lookup, block search, a per-chunk loaded-state grid, a view raycast honoring the negotiated protocol's block shapes, world time, world border, dimension info, and block interaction (dig/place/use).
/// Requires the Terrain feature; disabled features surface <see cref="MccFeatureDisabledException"/>.
/// </summary>
public sealed class WorldApi
{
    private readonly GameSession _session;

    internal WorldApi(GameSession session)
    {
        _session = session;
    }

    /// <summary>Reads the block at a world position (state id, registry name, and the derived flags).</summary>
    public Task<BlockInfo> GetBlockAsync(BlockPos position, CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            BlockSnapshot snapshot = await client.Snapshots.BlockAsync(position, ct).ConfigureAwait(false);
            return Project(snapshot);
        });

    /// <summary>
    /// Searches for blocks matching <paramref name="nameOrId"/> within <paramref name="radius"/> blocks of the player (cubic radius), returning up to <paramref name="maxResults"/> positions ordered by distance.
    /// The needle matches a namespaced or bare block id (for example <c>minecraft:chest</c> or <c>chest</c>), or a numeric block-state id.
    /// </summary>
    public Task<IReadOnlyList<BlockPos>> FindBlocksAsync(
        string nameOrId, int radius, int maxResults = 64, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        string needle = nameOrId.Trim();
        bool numeric = int.TryParse(needle, out int stateNeedle);
        string bare = Umpk.IdentifierMatch.BarePath(needle);

        return _session.ReadAsync(client =>
        {
            BlockPos origin = BlockPos.Containing(client.State.Self.Position);

            bool Match(BlockState state)
            {
                if (state.IsAir)
                    return false;

                return numeric ? state.StateId == stateNeedle : Umpk.IdentifierMatch.Matches(state.Block.Id, needle, bare);
            }

            return client.State.World.Find(origin, radius, Match, maxResults);
        }, ct);
    }

    /// <summary>
    /// The nearest block whose id is any of <paramref name="ids"/>, within <paramref name="radius"/> blocks of the player, or null when none match.
    /// One session-loop scan over an arbitrary id set (for example every bed color at once), rather than one call per id.
    /// </summary>
    public Task<BlockPos?> FindNearestAsync(IReadOnlyCollection<Umpk.Identifier> ids, int radius, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        return _session.ReadAsync(client =>
        {
            BlockPos origin = BlockPos.Containing(client.State.Self.Position);
            return client.State.World.FindNearest(origin, radius, ids);
        }, ct);
    }

    /// <summary>
    /// Reads the sign text at a world position, joining non-empty lines with newline, or null when no sign block entity is tracked there.
    /// Duck-types signs by NBT keys (modern <c>front_text.messages</c> or legacy <c>Text1</c>), never by version, and falls back to the legacy lines when no front text is present.
    /// </summary>
    public Task<string?> GetSignTextAsync(BlockPos position, CancellationToken ct = default)
        => _session.ReadAsync(client => SignComponentText.FromSignNbt(client.State.World.GetBlockEntityNbt(position)), ct);

    /// <summary>
    /// Reads the combined light level (the brighter of sky and block light, 0-15) at a world position, or null when the chunk column is not loaded.
    /// Null means unknown, never dark: a real night-time zero and an unloaded column must not read the same.
    /// </summary>
    public Task<int?> GetLightAsync(BlockPos position, CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            Umpk.Game.World.World world;
            try
            {
                world = client.State.World;
            }
            catch (InvalidOperationException)
            {
                return (int?)null;
            }

            Umpk.Game.World.ChunkColumn? column = world.GetColumn(position);
            Umpk.Game.World.ChunkSection? section = column?.GetSectionForY(position.Y);
            if (section is null)
                return (int?)null;

            // Missing arrays mean the session never stored light here (chunk-packet light is decoded but not stored today): unknown, never dark.
            if (section.SkyLight is null && section.BlockLight is null)
                return (int?)null;

            Umpk.Game.World.LightLevels levels = world.GetLight(position);
            return (int?)Math.Max(levels.Sky, levels.Block);
        }, ct);

    /// <summary>
    /// Reads the biome id (for example <c>minecraft:plains</c>) at a world position, or null when the chunk column is not loaded or the id is unknown.
    /// </summary>
    public Task<string?> GetBiomeIdAsync(BlockPos position, CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            Umpk.Game.World.World world;
            try
            {
                world = client.State.World;
            }
            catch (InvalidOperationException)
            {
                return (string?)null;
            }

            if (world.GetColumn(position) is null)
                return (string?)null;

            var entry = world.GetBiome(position);
            return entry.IsDefault ? null : entry.Id.ToString();
        }, ct);

    /// <summary>
    /// Searches for signs whose joined text contains <paramref name="needle"/> (case-insensitive) within <paramref name="radius"/> blocks of the player (cubic radius), returning up to <paramref name="maxResults"/> hits ordered by distance.
    /// Only duck-typed signs match; any other block entity reads as absent.
    /// </summary>
    public Task<IReadOnlyList<SignInfo>> FindSignsAsync(
        string needle, int radius, int maxResults = 64, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(needle);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        string want = needle.Trim();
        return _session.ReadAsync(client =>
        {
            BlockPos origin = BlockPos.Containing(client.State.Self.Position);
            var hits = new List<(SignInfo Hit, long Distance)>();
            foreach (KeyValuePair<BlockPos, NbtCompound> pair in client.State.World.BlockEntityNbt)
            {
                BlockPos pos = pair.Key;
                if (Math.Abs(pos.X - origin.X) > radius
                    || Math.Abs(pos.Y - origin.Y) > radius
                    || Math.Abs(pos.Z - origin.Z) > radius)
                    continue;

                string? text = SignComponentText.FromSignNbt(pair.Value);
                if (text is null || !text.Contains(want, StringComparison.OrdinalIgnoreCase))
                    continue;

                long dx = pos.X - origin.X;
                long dy = pos.Y - origin.Y;
                long dz = pos.Z - origin.Z;
                hits.Add((new SignInfo(pos, text), dx * dx + dy * dy + dz * dz));
            }

            return (IReadOnlyList<SignInfo>)hits
                .OrderBy(h => h.Distance)
                .Take(maxResults)
                .Select(h => h.Hit)
                .ToList();
        }, ct);
    }

    /// <summary>
    /// A grid of per-chunk loaded state centered on the player's chunk.
    /// The host supplies the viewport in chunk columns (<paramref name="columns"/> across, <paramref name="rows"/> down); odd sizes center on the player.
    /// Each cell is true when that chunk column is currently loaded.
    /// </summary>
    public Task<ChunkStatusGrid> GetChunkStatusAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns));

        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));

        return _session.RunAsync(client => client.Snapshots.ChunkStatusAsync(columns, rows, ct));
    }

    /// <summary>
    /// Drops a chunk column from the local world, exactly as if the server had told the client to unload it.
    /// Returns true when a loaded column was removed and false when there was nothing there, so the caller can report what actually happened instead of reporting a success it did not perform.
    /// </summary>
    /// <remarks>
    /// The server is not told and will not resend the column, so subsequent reads inside it answer through <see cref="BlockInfo.ChunkLoaded"/> false rather than silently returning air.
    /// Walking out of and back into the server's tracking range is what brings the column back.
    /// </remarks>
    public Task<bool> UnloadChunkAsync(int chunkX, int chunkZ, CancellationToken ct = default)
        => _session.ReadAsync(client => client.State.World.UnloadColumn(new ChunkPos(chunkX, chunkZ)), ct);

    /// <summary>
    /// Samples the top-down surface of a rectangular XZ region in one session-loop read: for each column it scans down from a ceiling (<paramref name="ceilingY"/>, or the top of the world) to the first non-air block and reports that block's identity plus its Y. This is the batched feed a top-down minimap needs; doing it per block through <see cref="GetBlockAsync"/> would marshal one delegate per column onto the session loop.
    /// Columns whose chunk is not loaded report <see cref="SurfaceColumn.Loaded"/> false.
    /// Grid is row-major over Z then X: <c>Columns[row * Width + column]</c>, column 0/row 0 at (<paramref name="originX"/>, <paramref name="originZ"/>).
    /// </summary>
    /// <param name="originX">The world X of the first column.</param>
    /// <param name="originZ">The world Z of the first row.</param>
    /// <param name="width">The number of columns (X span); must be positive.</param>
    /// <param name="length">The number of rows (Z span); must be positive.</param>
    /// <param name="ceilingY">When set, the highest Y the scan starts from (cave/underground mode); null starts at the world top.</param>
    /// <param name="ct">The ct value.</param>
    public Task<SurfaceRegionSnapshot> GetSurfaceRegionAsync(
        int originX, int originZ, int width, int length, int? ceilingY = null, CancellationToken ct = default)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        return _session.RunAsync(client => client.Snapshots.SurfaceRegionAsync(originX, originZ, width, length, ceilingY, ct));
    }

    /// <summary>
    /// Casts a ray from the player's eye along the view direction up to <paramref name="maxDistance"/> blocks, honoring the negotiated protocol's block shapes, and returns the first block hit (or null on a miss).
    /// </summary>
    public Task<BlockRaycastHit?> RaycastAsync(
        double maxDistance = 5.0, bool includeFluids = false, CancellationToken ct = default)
    {
        if (maxDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));

        return _session.RunAsync(async client =>
        {
            BlockHitResult? hit = await client.Snapshots.RaycastViewAsync(maxDistance, includeFluids, ct).ConfigureAwait(false);
            if (hit is not { } h)
                return (BlockRaycastHit?)null;

            BlockSnapshot block = await client.Snapshots.BlockAsync(h.BlockPos, ct).ConfigureAwait(false);
            return new BlockRaycastHit(h.BlockPos, h.Face, h.Point, h.Distance, block.State.StateId, block.State.Block.Id.ToString());
        });
    }

    /// <summary>
    /// How far above an entity's feet the sight ray aims.
    /// UMPK tracks no per-type dimensions, so this is mid-torso for the common mobs (zombie, skeleton, cow, player) rather than any one type's centre.
    /// Mirrors the AutoAttack plugin's own constant; the two cannot share code (the plugin must not reference DMCBK.Core), so a change here must change there too.
    /// </summary>
    internal const double VisibilityAimHeight = 0.9;

    /// <summary>
    /// True when the segment from the player's eye to <paramref name="targetFeet"/> plus <see cref="VisibilityAimHeight"/> enters no block.
    /// Pure and session-free so the geometry pins directly in tests; <see cref="HasLineOfSightAsync"/> is the session-loop entry point that feeds it the live eye, world and shape tables.
    /// </summary>
    internal static bool IsOccluded(
        Umpk.Game.World.World world, Vec3d eye, Vec3d targetFeet, IBlockShapeSource? shapes)
    {
        ArgumentNullException.ThrowIfNull(world);
        Vec3d aim = targetFeet.Add(0, VisibilityAimHeight, 0);
        return Raycast.CastBlock(world, eye, aim, includeFluids: false, shapes).Hit;
    }

    /// <summary>
    /// Whether the player's eye has an unblocked sight line to <paramref name="targetFeet"/> (an entity's feet position): fluids never count, and the negotiated protocol's own block shapes decide what counts as a wall.
    /// Reads true when there is no world to test against (the Terrain feature is off): unknown is not a wall.
    /// This is the same test AutoAttack's <c>Only_In_Front</c> applies after its facing check; the facing half lives with each caller.
    /// </summary>
    public Task<bool> HasLineOfSightAsync(Vec3d targetFeet, CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            if (!client.State.HasWorld || client.Session is null)
                return true;

            Vec3d eye = PlayerReach.EyePosition(client.State.Self.Position);
            IBlockShapeSource shapes = JavaGameData.BlockShapes(client.Session.Version.Version.Protocol);
            return !IsOccluded(client.State.World, eye, targetFeet, shapes);
        }, ct);

    /// <summary>Reads the server-reported weather: rain/thunder levels and the raining flag.</summary>
    public Task<WeatherInfo> GetWeatherAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.WeatherSnapshot weather =
                await client.Snapshots.WeatherAsync(ct).ConfigureAwait(false);
            return new WeatherInfo(weather.RainLevel, weather.ThunderLevel, weather.IsRaining);
        });

    /// <summary>Reads the world time (world age and time-of-day) and derived day/night facts.</summary>
    public Task<WorldTimeInfo> GetTimeAsync(CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            World world = client.State.World;
            long timeOfDay = world.TimeOfDay;
            long dayTime = ((timeOfDay % 24000) + 24000) % 24000;
            return new WorldTimeInfo(world.WorldAge, timeOfDay, timeOfDay / 24000, dayTime, dayTime < 12300 || dayTime > 23850);
        }, ct);

    /// <summary>Reads the current world border state.</summary>
    public Task<WorldBorderInfo> GetWorldBorderAsync(CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            WorldBorderState border = client.State.World.Border;
            return new WorldBorderInfo(
                border.CenterX, border.CenterZ, border.Size, border.TargetSize,
                border.IsLerping, border.WarningBlocks, border.WarningTimeSeconds);
        }, ct);

    /// <summary>Reads the current dimension identity and vertical bounds.</summary>
    public Task<DimensionInfo> GetDimensionAsync(CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            DimensionState dimension = client.State.World.Dimension;
            return new DimensionInfo(
                dimension.DimensionName.ToString(), dimension.Type.Id.ToString(),
                dimension.MinY, dimension.Height, dimension.MaxY, dimension.HasSkylight);
        }, ct);

    /// <summary>
    /// Digs (breaks) the block at a position through the full start/finish action sequence, and then reports what the server is known to have done about it.
    /// See <see cref="InteractionActions.DigBlockVerifiedAsync"/>: sending the start/finish pair is not breaking a block, so the only honest report is the one taken from the world afterwards.
    /// </summary>
    public Task<DigOutcome> DigBlockAsync(BlockPos position, Direction face = Direction.Up, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Interaction.DigBlockVerifiedAsync(position, face, confirmationWindow: null, ct));

    /// <summary>
    /// Places / uses the held item against a block face at a hit position.
    /// Completion is the send (plus the 1.19+ action acknowledgement where the version has one), NOT the effect: a use-on-block is whatever the target block decides it is, and vanilla reports nothing back that says which of "opened a container", "flipped a lever", "planted a crop", "placed a block" and "did nothing at all" happened.
    /// A caller must not describe the result as an achieved world change; see <see cref="DigBlockAsync"/> for the one interaction that does have an observable outcome.
    /// </summary>
    public Task PlaceBlockAsync(
        BlockPos position, Direction face, Vec3d cursor, Hand hand = Hand.Main, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Interaction.PlaceBlockAsync(position, face, cursor, hand, false, ct));

    /// <summary>
    /// Uses the held item in the air (right-click with no target).
    /// Completion is the send, not the effect; see <see cref="PlaceBlockAsync"/> for why there is nothing to confirm.
    /// </summary>
    public Task UseItemAsync(Hand hand = Hand.Main, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Interaction.UseItemAsync(hand, ct));

    private static BlockInfo Project(BlockSnapshot snapshot)
    {
        BlockState state = snapshot.State;
        return new BlockInfo(
            snapshot.Position, state.StateId, state.Block.Id.ToString(),
            state.IsAir, state.IsSolid, state.IsFluid, state.IsWaterlogged, state.BlocksMotion, snapshot.ChunkLoaded);
    }
}

/// <summary>One sign search hit: where the sign stands and its joined text.</summary>
/// <param name="Position">The sign block position.</param>
/// <param name="Text">The joined non-empty lines.</param>
public sealed record SignInfo(BlockPos Position, string Text);

/// <summary>
/// Tolerant JSON-component to plain-text reader for sign lines.
/// There is no public JSON-component parser in the engine, so this small reader lives here: a JSON string reads as-is, an object contributes its <c>text</c> plus each <c>extra</c> child with <c>translate</c> falling back to the key, an array concats its children, and numbers and booleans read as their literal text.
/// Unparseable input reads as-is, never throws.
/// </summary>
internal static class SignComponentText
{
    /// <summary>Reads one JSON-component string to plain text, tolerantly.</summary>
    public static string ToPlainText(string json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        using (document)
            return FromElement(document.RootElement);
    }

    /// <summary>
    /// Reads a raw block-entity payload to joined sign text, or null when the payload is absent or holds no sign keys.
    /// Modern front lines win; legacy <c>Text1</c> through <c>Text4</c> are the fallback.
    /// Empty lines are skipped and the rest join with newline.
    /// </summary>
    public static string? FromSignNbt(NbtCompound? nbt)
    {
        if (nbt is null)
            return null;

        if (nbt.TryGet("front_text", out NbtCompound? front)
            && front.TryGet("messages", out NbtList? messages))
        {
            var lines = new List<string>();
            foreach (NbtTag tag in messages)
            {
                if (tag is NbtString line)
                {
                    string plain = ToPlainText(line.Value);
                    if (!string.IsNullOrWhiteSpace(plain))
                        lines.Add(plain);
                }
            }

            return string.Join("\n", lines);
        }

        if (nbt.ContainsKey("Text1") || nbt.ContainsKey("Text2")
            || nbt.ContainsKey("Text3") || nbt.ContainsKey("Text4"))
        {
            var lines = new List<string>();
            foreach (string key in new[] { "Text1", "Text2", "Text3", "Text4" })
            {
                if (nbt.TryGet(key, out NbtString? line))
                {
                    string plain = ToPlainText(line.Value);
                    if (!string.IsNullOrWhiteSpace(plain))
                        lines.Add(plain);
                }
            }

            return string.Join("\n", lines);
        }

        return null;
    }

    private static string FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Object:
                var builder = new StringBuilder();
                if (element.TryGetProperty("text", out JsonElement text))
                    builder.Append(FromElement(text));
                else if (element.TryGetProperty("translate", out JsonElement translate)
                    && translate.ValueKind == JsonValueKind.String)
                    builder.Append(translate.GetString() ?? string.Empty);

                if (element.TryGetProperty("extra", out JsonElement extra)
                    && extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement child in extra.EnumerateArray())
                        builder.Append(FromElement(child));
                }

                return builder.ToString();
            case JsonValueKind.Array:
                var joined = new StringBuilder();
                foreach (JsonElement child in element.EnumerateArray())
                    joined.Append(FromElement(child));

                return joined.ToString();
            case JsonValueKind.Number:
                return element.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return string.Empty;
        }
    }
}

/// <param name="BlocksMotion">The BlocksMotion value.</param>
/// <param name="IsWaterlogged">The IsWaterlogged value.</param>
/// <param name="IsFluid">The IsFluid value.</param>
/// <param name="IsSolid">The IsSolid value.</param>
/// <param name="IsAir">The IsAir value.</param>
/// <param name="BlockId">The BlockId value.</param>
/// <param name="StateId">The StateId value.</param>
/// <param name="Position">The Position value.</param>
/// <summary>A typed view of one block state at a position.</summary>
/// <param name="ChunkLoaded">
/// Whether the chunk column holding <paramref name="Position"/> is loaded.
/// False means the client has no data there and every other field is the air default, which is NOT the same as a real air block.
/// </param>
public sealed record BlockInfo(
    BlockPos Position,
    int StateId,
    string BlockId,
    bool IsAir,
    bool IsSolid,
    bool IsFluid,
    bool IsWaterlogged,
    bool BlocksMotion,
    bool ChunkLoaded = true);

/// <summary>The first block a view raycast entered.</summary>
public sealed record BlockRaycastHit(
    BlockPos Position, Direction Face, Vec3d Point, double Distance, int StateId, string BlockId);

/// <summary>Server-reported weather.</summary>
/// <param name="RainLevel">The rain level, 0 (clear) to 1, vanilla-mapped from the game events.</param>
/// <param name="ThunderLevel">The thunder level, 0 to 1.</param>
/// <param name="IsRaining">Whether the server reports rain.</param>
public sealed record WeatherInfo(float RainLevel, float ThunderLevel, bool IsRaining);

/// <summary>World time facts.</summary>/// <param name="WorldAge">Ticks since world creation.</param>
/// <param name="TimeOfDay">The raw day-time counter (may be negative when the daylight cycle is frozen).</param>
/// <param name="Day">The elapsed day count.</param>
/// <param name="DayTime">The normalized time within the current day (0-23999).</param>
/// <param name="IsDaytime">True when the normalized time is within daylight hours.</param>
public sealed record WorldTimeInfo(long WorldAge, long TimeOfDay, long Day, long DayTime, bool IsDaytime);

/// <summary>World border facts.</summary>
public sealed record WorldBorderInfo(
    double CenterX, double CenterZ, double Size, double TargetSize, bool IsLerping, int WarningBlocks, double WarningTimeSeconds);

/// <summary>Dimension identity and vertical bounds.</summary>
public sealed record DimensionInfo(
    string DimensionName, string DimensionType, int MinY, int Height, int MaxY, bool HasSkylight);

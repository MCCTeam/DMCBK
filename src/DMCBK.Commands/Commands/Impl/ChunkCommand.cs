using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>chunk</c> command: render the chunk-load-status map, or manipulate the debug chunk overlay.
///
/// <para>
/// The map is the legacy one (Chunk.cs:89-231): a chunk-loading-status header, the player's location and chunk, the marked chunk when one was asked for, then the ASCII map trimmed to the loaded region around the player and bounded by the terminal, then the legend.
/// The player's cell carries a grey background and the marked cell a red one, both as the legacy <c>§§</c> background escapes.
/// </para>
/// <para>
/// The <c>_setloaded</c> debug op correctly marks the chunk loaded (the legacy Chunk.cs:266 bug set it to unloaded); the debug overlay is an MCC-side testable state (<see cref="ChunkDebugState"/>).
/// </para>
/// <para>
/// In a visual host a bare <c>chunk</c> opens the fullscreen chunk browser.
/// Text-only hosts keep the usage response, while <c>chunk status</c> remains the explicit text-map form.
/// </para>
/// </summary>
public sealed class ChunkCommand : CommandBase
{
    private readonly ChunkDebugState _debug = new();

    /// <inheritdoc/>
    public override string CmdName => "chunk";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.chunk.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "chunk status [chunkX chunkZ|locationX locationY locationZ] | chunk ui";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("status", "map the chunks loaded around you"),
        new("status <x> <y> <z>", "map around a position"),
        new("status <chunkX> <chunkZ>", "map around a chunk"),
        new("ui", CommandStrings.ChunkUiUsage),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["chunk status", "chunk status 150 80 380", "chunk ui"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["blockinfo"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy Chunk.cs:22-29 also answered "help chunk <subcommand>" (with the same text).
        help.ThenLiteral("status", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("_setloading", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("_setloaded", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("_delete", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => OpenUiOrUsage(ctx.Source))
            .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
            .ThenLiteral("status", h => h
                .Executes(ctx => Status(ctx.Source, null, null))
                .ThenArgument("Location", MccArguments.Location(), a => a
                    .Executes(ctx => Status(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), null)))
                .ThenArgument("Chunk", MccArguments.Tuple(), a => a
                    .Executes(ctx => Status(ctx.Source, null, ctx.GetArgument<ChunkPos>("Chunk")))))
            .ThenLiteral("_setloading", h => h
                .ThenArgument("Location", MccArguments.Location(), a => a
                    .Executes(ctx => SetLoading(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), null)))
                .ThenArgument("Chunk", MccArguments.Tuple(), a => a
                    .Executes(ctx => SetLoading(ctx.Source, null, ctx.GetArgument<ChunkPos>("Chunk")))))
            .ThenLiteral("_setloaded", h => h
                .ThenArgument("Location", MccArguments.Location(), a => a
                    .Executes(ctx => SetLoaded(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), null)))
                .ThenArgument("Chunk", MccArguments.Tuple(), a => a
                    .Executes(ctx => SetLoaded(ctx.Source, null, ctx.GetArgument<ChunkPos>("Chunk")))))
            .ThenLiteral("_delete", h => h
                .ThenArgument("Location", MccArguments.Location(), a => a
                    .Executes(ctx => Delete(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), null)))
                .ThenArgument("Chunk", MccArguments.Tuple(), a => a
                    .Executes(ctx => Delete(ctx.Source, null, ctx.GetArgument<ChunkPos>("Chunk")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int OpenUiOrUsage(CommandContext ctx)
    {
        if (ctx.TerrainEnabled && ctx.InSession && ctx.Ui?.TryOpenChunkBrowser() == true)
            return ctx.Result.Ok();

        return ShowUsage(ctx);
    }

    private static int Ui(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));
        return ctx.Ui?.TryOpenChunkBrowser() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
    }

    private int Status(CommandContext ctx, CommandLocation? location, ChunkPos? markedChunk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);
        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d? marker = location is { } loc ? loc.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch) : null;
        int rows = Math.Clamp(ctx.Ui?.ChunkViewportRows ?? 25, 25, 101) | 1;
        int columns = Math.Clamp(ctx.Ui?.ChunkViewportColumns ?? 17, 17, 101) | 1;
        ChunkStatusGrid grid = ctx.Run(ct => ctx.Game.World.GetChunkStatusAsync(columns * 2 + 1, rows * 2 + 1, ct));
        var loading = new HashSet<ChunkPos>();
        for (int row = 0; row < grid.Rows; row++)
            for (int column = 0; column < grid.Columns; column++)
            {
                ChunkPos chunk = grid.ChunkAt(row, column);
                if (grid.IsLoadedAt(row, column) && _debug.TryGet(chunk.X, chunk.Z, out bool loaded) && !loaded)
                    loading.Add(chunk);
            }
        var request = new ChunkMapPresentation(grid, loading.ToFrozenSet(), WorldCommandText.Position(pose.Position), marker, markedChunk, rows, columns);
        if (ctx.Ui?.TryPresentChunkMap(request, ctx.Glyphs) != true)
        {
            int total = 0;
            for (int row = 0; row < grid.Rows; row++)
                for (int column = 0; column < grid.Columns; column++)
                    if (grid.IsLoadedAt(row, column)) total++;
            int complete = total - loading.Count;
            ctx.Output.WriteLine(McStrings.Format("cmd.move.chunk_loading_status", total == 0 ? 0 : complete / (double)total, complete, total));
            ctx.Output.WriteLine(McStrings.Format("cmd.chunk.current", request.PlayerPositionText, grid.CenterChunkX, grid.CenterChunkZ));
            if (markedChunk is { } mark)
                ctx.Output.WriteLine(McStrings.Get("cmd.chunk.marked") + McStrings.Format("cmd.chunk.chunk_pos", mark.X, mark.Z));
        }
        return ctx.Result.Set(CmdStatus.Done);
    }

    private int SetLoading(CommandContext ctx, CommandLocation? location, ChunkPos? markedChunk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        ctx.Output.WriteLine(McStrings.Get("cmd.chunk.for_debug"));
        ChunkPos target = markedChunk ?? ChunkOf(ctx, location.GetValueOrDefault());
        if (!IsColumnLoaded(ctx, target))
            return ctx.Result.Fail(CommandStrings.ChunkNotPresent);

        _debug.SetLoading(target.X, target.Z);
        return ctx.Result.Ok(CommandStrings.ChunkSetLoading(target.X, target.Z));
    }

    private int SetLoaded(CommandContext ctx, CommandLocation? location, ChunkPos? markedChunk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        ctx.Output.WriteLine(McStrings.Get("cmd.chunk.for_debug"));
        ChunkPos target = markedChunk ?? ChunkOf(ctx, location.GetValueOrDefault());
        if (!IsColumnLoaded(ctx, target))
            return ctx.Result.Fail(CommandStrings.ChunkNotPresent);

        // Fix for legacy Chunk.cs:266: marking a chunk loaded must set the loaded flag true, not false.
        _debug.SetLoaded(target.X, target.Z);
        return ctx.Result.Ok(CommandStrings.ChunkSetLoaded(target.X, target.Z));
    }

    /// <summary>
    /// Drops the chunk column from the local world and clears any debug overlay entry for it so the status map falls back to the truth.
    /// </summary>
    private int Delete(CommandContext ctx, CommandLocation? location, ChunkPos? markedChunk)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        ctx.Output.WriteLine(McStrings.Get("cmd.chunk.for_debug"));
        ChunkPos target = markedChunk ?? ChunkOf(ctx, location.GetValueOrDefault());
        _debug.Delete(target.X, target.Z);
        bool removed = ctx.Run(ct => ctx.Game.World.UnloadChunkAsync(target.X, target.Z, ct));
        return removed
            ? ctx.Result.Ok(CommandStrings.ChunkDeleted(target.X, target.Z))
            : ctx.Result.Set(CmdStatus.FailChunkNotLoad);
    }

    /// <summary>
    /// The chunk a debug op targets: the chunk pair when that form was used, else the chunk holding the given location (legacy Chunk.cs:243, :263 and :283, which resolved the location form the same way).
    /// </summary>
    private static ChunkPos ChunkOf(CommandContext ctx, CommandLocation location)
    {
        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        return ChunkPos.Containing(BlockPos.Containing(location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch)));
    }

    private static bool IsColumnLoaded(CommandContext ctx, ChunkPos chunk)
    {
        // The loaded flag comes from the column, so any Y inside it answers the question.
        BlockPos probe = new(chunk.MinBlockX, 0, chunk.MinBlockZ);
        return ctx.Run(ct => ctx.Game.World.GetBlockAsync(probe, ct)).ChunkLoaded;
    }
}

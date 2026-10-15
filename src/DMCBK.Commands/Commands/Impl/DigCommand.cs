using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>Starts a held block attack without blocking command input. The registering client owns completion, cancellation and disposal.</summary>
public sealed class DigCommand : CommandBase, IAsyncDisposable
{
    private readonly object _gate = new();
    private Attack? _attack;
    private Task? _disposeTask;
    private bool _disposed;

    private sealed class Attack(BlockPos position, CancellationToken caller)
    {
        public BlockPos Position { get; } = position;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(caller);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work { get; set; } = Task.CompletedTask;
    }

    /// <summary>Creates a block-attack command. The registering host owns its disposal.</summary>
    public DigCommand() { }


    /// <inheritdoc/>
    public override string CmdName => "dig";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.dig.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "dig [<x> <y> <z>|stop]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "break the block you are looking at"),
        new("<x> <y> <z>", "break a specific block"),
        new("stop", CommandStrings.DigStopHelp),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["dig", "dig 150 79 380", "dig stop"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["useblock", "blockinfo"];

    /// <inheritdoc/>
    public override string? ManTopic => "movement";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => DigLookAt(ctx.Source))
            .ThenLiteral("stop", h => h.Executes(ctx => Stop(ctx.Source)))
            .ThenArgument("Location", DmcbkArguments.Location(), h => h
                .Executes(ctx => DigAt(ctx.Source, ctx.GetArgument<CommandLocation>("Location"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int DigLookAt(CommandContext ctx)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));
        if (RejectBusy(ctx) is int refused)
            return refused;

        BlockRaycastHit? hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(4.5, false, ct));
        if (hit is not { } h)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.too_far"));

        // Legacy Dig.cs:79: the look-at form reports the block's own coordinates, not the centred ones.
        return Send(ctx, h.Position, h.BlockId, h.Position.X, h.Position.Y, h.Position.Z);
    }

    private int DigAt(CommandContext ctx, CommandLocation location)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));
        if (RejectBusy(ctx) is int refused)
            return refused;

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        Vec3d target = location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch);

        // Legacy Dig.cs:57-58: eye-to-target squared distance against a flat 25, measured on the requested (unfloored) coordinates.
        if ((target - PlayerReach.EyePosition(pose.Position)).LengthSqr() > 25)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.too_far"));

        BlockPos block = BlockPos.Containing(target);
        BlockInfo info = ctx.Run(ct => ctx.Game.World.GetBlockAsync(block, ct));
        if (info.IsAir)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.no_block"));

        // Aim through the same view ray as the look-at form so the transmitted face matches the visible surface and an intervening block prevents the dig.
        ctx.Run(ct => ctx.Game.Movement.LookAtAsync(block.Center, ct));
        BlockRaycastHit? hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(4.5, false, ct));
        if (hit is not { } h || h.Position != block)
            return ctx.Result.Fail(McStrings.Get("cmd.dig.too_far"));

        // Preserve the coordinate form's reported position (X/Z centred, Y untouched).
        return Send(ctx, block, info.BlockId, Math.Floor(target.X) + 0.5, target.Y, Math.Floor(target.Z) + 0.5);
    }

    private int? RejectBusy(CommandContext ctx)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _attack is null ? null : ctx.Result.Fail(CommandStrings.DigAlreadyRunning(ctx.Commands.ActivePrefix));
        }
    }

    private int Send(CommandContext ctx, BlockPos block, string blockId, double reportX, double reportY, double reportZ)
    {
        Attack attack;
        ctx.Cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attack is not null)
                return ctx.Result.Fail(CommandStrings.DigAlreadyRunning(ctx.Commands.ActivePrefix));
            attack = new Attack(block, ctx.Cancellation);
            _attack = attack;
            try
            {
                // Resolve and start the facade action now, before returning control to a host that may reconnect. The observer waits until its task has an owner.
                Task<DigOutcome> work = ctx.Game.World.DigBlockInViewAsync(block, ct: attack.Cancellation.Token);
                attack.Work = ObserveAsync(ctx, attack, work);
            }
            catch
            {
                _attack = null;
                attack.Cancellation.Dispose();
                throw;
            }
        }
        attack.Ready.SetResult();
        return ctx.Result.Ok(McStrings.Format(
            "cmd.dig.dig", reportX, reportY, reportZ, WorldCommandText.BlockName(ctx, blockId)));
    }

    private async Task ObserveAsync(CommandContext ctx, Attack attack, Task<DigOutcome> work)
    {
        await attack.Ready.Task.ConfigureAwait(false);
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (attack.Cancellation.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        {
            // Caller cancellation and session teardown both release the library-owned mining request.
        }
        catch (Exception error)
        {
            ctx.Logger.LogWarning(error, "The block attack at {Position} failed.", attack.Position);
            try
            {
                ctx.ReportBackgroundFault?.Invoke(error);
            }
            catch (Exception subscriberError)
            {
                ctx.Logger.LogError(subscriberError, "A block-attack fault subscriber threw at {Position}.", attack.Position);
            }
            try
            {
                ctx.Client.CommandOutput.WriteLine(McStrings.Get("cmd.dig.fail"));
            }
            catch (Exception reportingError)
            {
                ctx.Logger.LogError(reportingError, "Could not report the failed block attack at {Position}.", attack.Position);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_attack, attack))
                    _attack = null;
                attack.Cancellation.Dispose();
            }
        }
    }

    private int Stop(CommandContext ctx)
    {
        BlockPos? position = ctx.Run<BlockPos?>(StopHeldAttackAsync);
        return ctx.Result.Ok(position is { } block
            ? McStrings.Format("cmd.dig.cancel", FormattableString.Invariant($"{block.X} {block.Y} {block.Z}"))
            : CommandStrings.DigNotRunning);
    }

    private async Task<BlockPos?> StopHeldAttackAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attack? attack;
        lock (_gate)
        {
            attack = _attack;
            attack?.Cancellation.Cancel();
        }
        if (attack is null)
            return null;
        await attack.Work.WaitAsync(ct).ConfigureAwait(false);
        return attack.Position;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= StopHeldAttackAsync(CancellationToken.None));
        }
    }
}

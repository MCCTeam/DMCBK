using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;

namespace DMCBK.Core.Beacon;

/// <summary>
/// The gameplay gates a movement request is checked against.
/// Mirrors <c>client.toml [Gameplay]</c>; rebind on config reload or reconnect via <see cref="FromMcc"/>.
/// </summary>
/// <param name="Terrain">Track terrain/world state.</param>
/// <param name="Inventory">Track inventory/container state.</param>
/// <param name="Entity">Track entity state.</param>
/// <param name="Physics">Run the local physics simulation.</param>
/// <param name="Pathfinding">Enable pathfinding/navigation.</param>
public sealed record BeaconGameplayGates(
    bool Terrain = true,
    bool Inventory = true,
    bool Entity = true,
    bool Physics = true,
    bool Pathfinding = true)
{
    /// <summary>Every gate on (hermetic tests and the default config).</summary>
    public static BeaconGameplayGates AllOn { get; } = new();

    /// <summary>Reads the live gates out of the MCC configuration snapshot.</summary>
    public static BeaconGameplayGates FromMcc(GameplayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new BeaconGameplayGates(
            config.Terrain, config.Inventory, config.Entity, config.Physics, config.Pathfinding);
    }
}

/// <summary>
/// The exclusive steering lease, mirroring <c>ISessionScope.TryAcquireMovement</c>.
/// Dispose to release; the owning scope also releases its lease at session end.
/// Scopes adapt their native lease to this interface member for member (<c>TryAcquireMovement(reason)</c> plus an owner tag).
/// </summary>
public interface IBeaconMovementLease : IDisposable
{
    /// <summary>Who holds the lease (the reason passed to acquire).</summary>
    string Owner { get; }
}

/// <summary>
/// The smallest movement surface the runner programs against: read the holder, acquire, never probe.
/// Each load injects one scope through <see cref="BeaconEngine.MovementBinder"/>; tests stub this.
/// </summary>
public interface IBeaconMovementScope
{
    /// <summary>
    /// The tag of whoever holds the movement lease right now, or null when it is free.
    /// Read this instead of probing with <see cref="TryAcquireMovement"/>: probing takes the lease for the length of the probe, which is enough to make a navigation starting on the same tick fail.
    /// </summary>
    string? MovementOwner { get; }

    /// <summary>Tries to acquire the exclusive movement lease; null when another owner holds it.</summary>
    IBeaconMovementLease? TryAcquireMovement(string reason);
}

/// <summary>
/// A movement scope that is always free: the lease grants immediately and releases on dispose.
/// The engine binds this behind headless and offline runs (plus the immediate-arrival executor) so steering completes deterministically with no session.
/// Live loads keep this scope and replace the executor with the session navigator call through <see cref="BeaconEngine.MovementBinder"/>.
/// </summary>
public sealed class BeaconFreeMovementScope : IBeaconMovementScope
{
    /// <inheritdoc/>
    public string? MovementOwner => null;

    /// <inheritdoc/>
    public IBeaconMovementLease? TryAcquireMovement(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new FreeLease(reason);
    }

    private sealed class FreeLease(string owner) : IBeaconMovementLease
    {
        /// <inheritdoc/>
        public string Owner { get; } = owner;

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

/// <summary>What a movement request steers toward.</summary>
public enum BeaconMovementKind
{
    /// <summary>Walk to a coordinate (<c>move_goto(x, y, z)</c>).</summary>
    Goto,

    /// <summary>Track a named player (<c>move_follow(player)</c>).</summary>
    Follow,

    /// <summary>Track any entity by id (<c>move_follow(id)</c> or <c>move_follow(row)</c>).</summary>
    FollowEntity,
}

/// <summary>One steering goal: coordinates for <see cref="BeaconMovementKind.Goto"/>, a player name for Follow.</summary>
/// <param name="Kind">Goto, Follow, or FollowEntity.</param>
/// <param name="X">Target X (Goto only).</param>
/// <param name="Y">Target Y (Goto only).</param>
/// <param name="Z">Target Z (Goto only).</param>
/// <param name="Player">Tracked player (Follow only).</param>
/// <param name="Options">Arrival tolerance plus sneak/sprint control (Goto only; null means defaults).</param>
/// <param name="EntityId">Tracked entity id (FollowEntity only).</param>
/// <param name="EntityDesc">Human-readable entity label for traces (FollowEntity only).</param>
public sealed record BeaconMoveTarget(
    BeaconMovementKind Kind, double X, double Y, double Z, string? Player, BeaconMoveOptions? Options = null,
    int? EntityId = null, string? EntityDesc = null);

/// <summary>Movement options for <c>move_goto(x, y, z, {tolerance, sneak, sprint})</c>.</summary>
/// <param name="Tolerance">Arrival radius in blocks (default 1).</param>
/// <param name="Sneak">Hold sneak while walking.</param>
/// <param name="Sprint">Hold sprint while walking.</param>
public sealed record BeaconMoveOptions(double Tolerance = 1.0, bool Sneak = false, bool Sprint = false);

/// <summary>What the motion executor reports when its run ends.</summary>
/// <param name="Reached">True on arrival (Goto) or clean track end (Follow).</param>
/// <param name="Detail">Human-readable detail for the result map.</param>
public sealed record BeaconMoveArrival(bool Reached, string Detail);

/// <summary>
/// Movement as cancellable tasks with newest-wins preemption.
/// Exactly one Beacon request steers at a time: a new <c>move_goto</c>/<c>move_follow</c> cancels the running one (whose awaiter gets a catchable <c>B4003 superseded</c> error naming the newcomer) and takes the lease; <c>stop_moving()</c> cancels with catchable <c>B4004</c>.
/// The lease is tied to the scope lifetime: <see cref="HandleSessionLost"/> cancels the run and the scope's own detach releases the lease, so no lease is ever held across a reconnect.
/// </summary>
/// <remarks>
/// <para>
/// Lease discipline (mirrors <c>ISessionScope</c>): the holder is read via <see cref="IBeaconMovementScope.MovementOwner"/>, never by probe-acquiring.
/// An externally held lease refuses readably (catchable <c>B4003</c> naming the holder) instead of stealing.
/// </para>
/// <para>
/// Gameplay gates mirror <c>MoveCommand.RequiredFeatures</c>: Goto needs Terrain+Physics+Pathfinding, Follow and FollowEntity additionally need Entity.
/// A missing gate refuses with catchable <c>B4005</c> before the lease is ever touched.
/// </para>
/// <para>Motion itself runs through the injected <c>executor</c> (a fake in tests, the session navigator call in live loads through <see cref="BeaconEngine.MovementBinder"/>).</para>
/// <para>The default completes immediately as arrived so pure logic tests need no session.</para>
/// <para>A Goto that neither completes nor fails within <see cref="MoveTimeout"/> fails catchably instead of parking the awaiter; Follow and FollowEntity are unbounded by contract.</para>
/// </remarks>
public sealed class BeaconMovementRunner
{
    /// <summary>What Goto needs on, mirroring <c>MoveCommand.RequiredFeatures</c> exactly.</summary>
    public static readonly CommandFeature RequiredGotoFeatures =
        CommandFeature.Terrain | CommandFeature.Physics | CommandFeature.Pathfinding;

    /// <summary>What Follow needs on: the Goto set plus entity tracking.</summary>
    public static readonly CommandFeature RequiredFollowFeatures =
        RequiredGotoFeatures | CommandFeature.Entity;

    private readonly object _gate = new();
    private IBeaconMovementScope _scope;
    private Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>> _executor;
    private readonly List<string> _log = [];
    private MovementRequest? _current;

    /// <summary>
    /// The default ceiling on one Goto request: a steering call that neither completes nor fails within this long is failed catchably instead of parking the awaiter forever.
    /// Follow has no ceiling by contract (it tracks until cancelled, superseded, or dropped).
    /// </summary>
    public static readonly TimeSpan DefaultMoveTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Builds a runner over a session scope, live gates, and a motion executor.</summary>
    public BeaconMovementRunner(
        IBeaconMovementScope scope,
        BeaconGameplayGates gates,
        Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>>? executor = null,
        TimeSpan? moveTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(gates);
        _scope = scope;
        Gates = gates;
        _executor = executor ?? ImmediateArrival;
        MoveTimeout = moveTimeout ?? DefaultMoveTimeout;
    }

    private static Task<BeaconMoveArrival> ImmediateArrival(BeaconMoveTarget target, CancellationToken ct)
    {
        _ = target;
        _ = ct;
        return Task.FromResult(new BeaconMoveArrival(
            true, "arrived (no session executor; the orchestrator replaces this with the navigator call)"));
    }

    /// <summary>Live gameplay gates; rebind on config reload or reconnect.</summary>
    public BeaconGameplayGates Gates { get; set; }

    /// <summary>
    /// Rebinds the session scope, motion executor, and gates (live session wiring calls this per script load; every load carries the same session, so sharing one runner across scripts keeps newest-wins global instead of per script).
    /// In-flight runs keep the captured scope and executor they started with.
    /// </summary>
    public void Rebind(
        IBeaconMovementScope scope,
        Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>>? executor,
        BeaconGameplayGates? gates = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            _scope = scope;
            _executor = executor ?? ImmediateArrival;
            if (gates is not null)
                Gates = gates;

            _log.Add("rebind scope+executor");
        }
    }

    /// <summary>
    /// Ceiling on one Goto request: the watchdog fails a Goto that neither completes nor fails within this long with a catchable timeout instead of parking the awaiter.
    /// Must stay positive; Follow and FollowEntity never read it.
    /// </summary>
    public TimeSpan MoveTimeout
    {
        get
        {
            lock (_gate)
                return _moveTimeout;
        }

        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "The movement watchdog timeout must stay positive.");
            }

            lock (_gate)
                _moveTimeout = value;
        }
    }

    private TimeSpan _moveTimeout;

    /// <summary>Owner tag of the running Beacon request, or null when nothing Beacon steers.</summary>
    public string? ActiveOwner
    {
        get
        {
            lock (_gate)
                return _current?.Owner;
        }
    }

    /// <summary>Structured run log.</summary>
    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_gate)
                return _log.ToList();
        }
    }

    /// <summary>
    /// Walks to (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): completes with a <c>{reached, detail}</c> map on arrival or cancel.
    /// Newest request wins (see class remarks).
    /// </summary>
    public Task<BeaconValue> GotoAsync(double x, double y, double z, string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return RunMovementAsync(
            new BeaconMoveTarget(BeaconMovementKind.Goto, x, y, z, Player: null), owner, ct);
    }

    /// <summary>
    /// Walks to (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) with <paramref name="options"/> (arrival tolerance, sneak/sprint).
    /// Newest request wins.
    /// </summary>
    public Task<BeaconValue> GotoAsync(
        double x, double y, double z, string owner, BeaconMoveOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(options);
        return RunMovementAsync(
            new BeaconMoveTarget(BeaconMovementKind.Goto, x, y, z, Player: null, Options: options), owner, ct);
    }

    /// <summary>
    /// Tracks <paramref name="player"/> until cancelled, superseded, or the session drops.
    /// Newest request wins (see class remarks).
    /// </summary>
    public Task<BeaconValue> FollowAsync(string player, string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return RunMovementAsync(
            new BeaconMoveTarget(BeaconMovementKind.Follow, 0, 0, 0, player), owner, ct);
    }

    /// <summary>
    /// Tracks the entity with <paramref name="entityId"/> until cancelled, superseded, the entity leaves tracking range, or the session drops.
    /// Newest request wins (see class remarks).
    /// </summary>
    public Task<BeaconValue> FollowEntityAsync(int entityId, string entityDesc, string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityDesc);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return RunMovementAsync(
            new BeaconMoveTarget(BeaconMovementKind.FollowEntity, 0, 0, 0, Player: null, EntityId: entityId, EntityDesc: entityDesc),
            owner, ct);
    }

    /// <summary>Cancels the running Beacon request for <c>stop_moving()</c>; a no-op when idle.</summary>
    public void StopMoving(string reason = "stop_moving")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_current is null)
            {
                _log.Add($"stop idle reason={reason}");
                return;
            }

            _current.StopReason = reason;
            _log.Add($"stop owner={_current.Owner} reason={reason}");
            _current.Cancellation.Cancel();
        }
    }

    /// <summary>
    /// Cancels the running request because the session is gone (reconnect, unload).
    /// The scope's own detach releases the lease; this only stops the run with a catchable <c>B4004</c>.
    /// </summary>
    public void HandleSessionLost(string reason = "session lost")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_current is null)
            {
                _log.Add($"session-lost idle reason={reason}");
                return;
            }

            _current.SessionLost = true;
            _current.StopReason = reason;
            _log.Add($"session-lost owner={_current.Owner} reason={reason}");
            _current.Cancellation.Cancel();
        }
    }

    private async Task<BeaconValue> RunMovementAsync(
        BeaconMoveTarget target, string owner, CancellationToken ct)
    {
        CheckGates(target);

        MovementRequest request;
        IBeaconMovementLease lease;
        Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>> executor;
        lock (_gate)
        {
            if (_current is not null)
            {
                _current.SupersededBy = owner;
                _current.ReleaseLease();
                _log.Add($"supersede old={_current.Owner} new={owner}");
                _current.Cancellation.Cancel();
                _current = null;
            }

            string? holder = _scope.MovementOwner;
            if (holder is not null)
                throw HeldBy(holder, owner);

            IBeaconMovementLease? acquired = _scope.TryAcquireMovement($"beacon:{owner}");
            if (acquired is null)
                throw HeldBy(_scope.MovementOwner ?? "another owner", owner);

            lease = acquired;
            request = new MovementRequest(owner, target, lease);
            _current = request;
            executor = _executor;
            _log.Add($"acquire owner={owner} kind={target.Kind}");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Cancellation.Token);
        try
        {
            Task<BeaconMoveArrival> run = executor(target, linked.Token);
            BeaconMoveArrival arrival;
            if (target.Kind is BeaconMovementKind.Follow or BeaconMovementKind.FollowEntity)
                arrival = await run.ConfigureAwait(false);
            else
            {
                TimeSpan budget;
                lock (_gate)
                    budget = _moveTimeout;

                Task first = await Task.WhenAny(run, Task.Delay(budget)).ConfigureAwait(false);
                if (ReferenceEquals(first, run))
                    arrival = await run.ConfigureAwait(false);
                else
                {
                    string? superseder;
                    lock (_gate)
                    {
                        request.TimedOut = true;
                        superseder = request.SupersededBy;
                    }

                    if (superseder is not null)
                        throw Superseded(owner, superseder);

                    request.Cancellation.Cancel();
                    ObserveLate(run, owner);
                    throw MovementTimedOut(owner, target, budget);
                }
            }

            lock (_gate)
                _log.Add($"arrival owner={owner} reached={arrival.Reached}");

            return BeaconValue.Map(new Dictionary<string, BeaconValue>
            {
                ["reached"] = BeaconValue.YesNo(arrival.Reached),
                ["detail"] = BeaconValue.Text(arrival.Detail),
            });
        }
        catch (OperationCanceledException)
        {
            string? superseder;
            string? stop;
            bool sessionLost;
            bool timedOut;
            lock (_gate)
            {
                superseder = request.SupersededBy;
                stop = request.StopReason;
                sessionLost = request.SessionLost;
                timedOut = request.TimedOut;
            }

            if (superseder is not null)
                throw Superseded(owner, superseder);

            if (timedOut && stop is null && !sessionLost)
            {
                TimeSpan budget;
                lock (_gate)
                    budget = _moveTimeout;

                throw MovementTimedOut(owner, target, budget);
            }

            throw MovementCancelled(owner, sessionLost ? "the session was lost" : stop ?? "it was cancelled");
        }
        catch (BeaconRuntimeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                "B4002",
                $"move by '{owner}' failed: {ex.Message}",
                MovementSpan(),
                "Wrap it in try/catch err and read err.message.",
                ex);
        }
        finally
        {
            linked.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_current, request))
                    _current = null;

                request.ReleaseLease();
                _log.Add($"release owner={owner}");
            }
        }
    }

    private void CheckGates(BeaconMoveTarget target)
    {
        CommandFeature required = target.Kind == BeaconMovementKind.Goto
            ? RequiredGotoFeatures
            : RequiredFollowFeatures;
        var missing = new List<string>();
        if (required.HasFlag(CommandFeature.Terrain) && !Gates.Terrain)
            missing.Add("Terrain");

        if (required.HasFlag(CommandFeature.Inventory) && !Gates.Inventory)
            missing.Add("Inventory");

        if (required.HasFlag(CommandFeature.Entity) && !Gates.Entity)
            missing.Add("Entity");

        if (required.HasFlag(CommandFeature.Physics) && !Gates.Physics)
            missing.Add("Physics");

        if (required.HasFlag(CommandFeature.Pathfinding) && !Gates.Pathfinding)
            missing.Add("Pathfinding");

        if (missing.Count > 0)
        {
            throw new BeaconRuntimeException(
                BeaconTaskErrorCodes.MovementGated,
                $"Movement needs Gameplay.{string.Join(", Gameplay.", missing)} on, but it is off. " +
                $"This mirrors /move RequiredFeatures ({RequiredGotoFeatures}). " +
                $"Turn it on in client.toml [Gameplay], or guard the move with 'if' first.",
                MovementSpan(),
                "Turn on Gameplay.Pathfinding (and Terrain/Physics) in client.toml [Gameplay].");
        }
    }

    private static BeaconRuntimeException Superseded(string loser, string superseder) => new(
        BeaconTaskErrorCodes.Superseded,
        $"Movement by '{loser}' was superseded by '{superseder}': the newest movement request wins, so the older move was cancelled. " +
        $"Only one pair of feet exists; coordinate with stop_moving() or await first.",
        MovementSpan(),
        "Wrap the move in try/catch err and read err.message to notice preemption.");

    private static BeaconRuntimeException HeldBy(string holder, string requester) => new(
        BeaconTaskErrorCodes.Superseded,
        $"Movement by '{requester}' refused: movement is held by '{holder}'. The newest Beacon request wins among scripts, " +
        $"but an outside holder is never stolen from.",
        MovementSpan(),
        "Wait for the holder to finish, or stop it first.");

    private static BeaconRuntimeException MovementCancelled(string owner, string why) => new(
        BeaconTaskErrorCodes.TaskCancelled,
        $"Movement by '{owner}' stopped: {why}.",
        MovementSpan(),
        "Wrap the move in try/catch err and read err.message.");

    private static BeaconRuntimeException MovementTimedOut(
        string owner, BeaconMoveTarget target, TimeSpan budget) => new(
        "B4002",
        CommandStrings.ScriptsMovementTimedOut(owner, DescribeTarget(target), budget.TotalSeconds),
        MovementSpan(),
        "Wrap the move in try/catch err and read err.message.");

    private static string DescribeTarget(BeaconMoveTarget target) =>
        target.Kind switch
        {
            BeaconMovementKind.Follow => $"player '{target.Player}'",
            BeaconMovementKind.FollowEntity => $"entity '{target.EntityDesc ?? target.EntityId?.ToString()}'",
            _ => string.Format(
                CultureInfo.InvariantCulture, "({0}, {1}, {2})", target.X, target.Y, target.Z),
        };

    private void ObserveLate(Task<BeaconMoveArrival> run, string owner)
    {
        ArgumentNullException.ThrowIfNull(run);
        _ = run.ContinueWith(
            task =>
            {
                lock (_gate)
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        _log.Add($"late owner={owner} reached={task.Result.Reached}");
                    else if (task.Status == TaskStatus.Canceled)
                        _log.Add($"late owner={owner} canceled");
                    else
                        _log.Add($"late owner={owner} faulted={task.Exception?.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static SourceSpan MovementSpan() => new("movement.bcn", 1, 1, 0);

    private sealed class MovementRequest(string owner, BeaconMoveTarget target, IBeaconMovementLease lease)
    {
        public string Owner { get; } = owner;
        public BeaconMoveTarget Target { get; } = target;
        public CancellationTokenSource Cancellation { get; } = new();
        public string? SupersededBy { get; set; }
        public string? StopReason { get; set; }
        public bool SessionLost { get; set; }
        public bool TimedOut { get; set; }

        private IBeaconMovementLease? _lease = lease;
        private bool _released;

        public void ReleaseLease()
        {
            if (_released)
                return;

            _released = true;
            IBeaconMovementLease? held = Interlocked.Exchange(ref _lease, null);
            held?.Dispose();
        }
    }
}

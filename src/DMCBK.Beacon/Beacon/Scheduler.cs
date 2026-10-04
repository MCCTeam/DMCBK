namespace DMCBK.Core.Beacon;

/// <summary>
/// The cooperative scheduler: one logical task per script plus children, a FIFO event queue, virtual-clock <c>wait</c> as the only sleep primitive, immutable event snapshots, and clean cancel on reconnect.
/// Standalone by design.
/// <see cref="BeaconEngine"/> holds one per script and runs handler bodies by passing interpreter callbacks into <see cref="DispatchAsync"/> (one call per matching <c>on</c> block).
/// <c>start</c>, <c>await</c>, and <c>cancel task</c> statements route to <see cref="StartTask"/>, <see cref="AwaitTaskAsync"/>, and <see cref="CancelTask"/>.
/// </summary>
/// <remarks>
/// <para>Semantics pinned here:</para>
/// <list type="bullet">
/// <item>
/// <c>wait</c> yields through <see cref="IVirtualClock.Delay"/> with a 100 ms floor (<see cref="WaitQuantum"/>): <c>wait 0</c> cannot spin.
/// At most <see cref="MaxPendingSleeps"/> sleeps may pend per script; the 33rd raises catchable <c>B4006</c>.
/// </item>
/// <item>Event fields are deep-copied at dispatch via <see cref="CopyEventSnapshot"/> (the copy helper the event path dispatches through): a <c>wait</c> mid-handler never observes a mutation made after dispatch began.</item>
/// <item>
/// Fuel: <see cref="StartTask"/>, <see cref="DispatchAsync"/>, <see cref="WaitAsync"/>, and <see cref="BeaconTaskContext.SpendBackEdge"/> each spend one step for budget accounting.
/// </item>
/// <item>
/// Reconnect: <see cref="HandleDisconnect"/> drops the session (pending sleeps abort, dispatches are refused); <see cref="HandleReconnect"/> cancels anything still pending and re-arms.
/// <see cref="HandleReload"/> resets task state; only <c>saved</c> survives (owned by the persisted store).
/// </item>
/// <item>
/// Threading: no blocking call ever sits on the scheduler path.
/// Dispatches serialize on a non-reentrant FIFO gate; handlers must not reenter <see cref="DispatchAsync"/> or <see cref="EnqueueAsync"/> or they deadlock.
/// </item>
/// </list>
/// </remarks>
public sealed class BeaconScheduler
{
    /// <summary>Maximum concurrent pending sleeps per script; the next <c>wait</c> raises <c>B4006</c>.</summary>
    public const int MaxPendingSleeps = 32;

    /// <summary>The wait quantum: every <c>wait</c> lasts at least this long.</summary>
    public static readonly TimeSpan WaitQuantum = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _fifo = new(1, 1);
    private readonly Dictionary<long, BeaconTask> _tasks = new();
    private readonly List<string> _log = [];
    private CancellationTokenSource _sessionCts = new();
    private CancellationTokenRegistration _detachedRegistration;
    private bool _hasDetachedBinding;
    private long _nextId;
    private int _pendingSleeps;
    private bool _connected = true;

    /// <summary>Builds a scheduler for one script over the injected seams.</summary>
    public BeaconScheduler(string scriptId, IVirtualClock clock, IFuelBudget fuel, ISeededRng? rng = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(fuel);
        ScriptId = scriptId;
        Clock = clock;
        Fuel = fuel;
        Rng = rng ?? SeededRng.FromEntropy();
    }

    /// <summary>Owning script id (log stamps and task attribution).</summary>
    public string ScriptId { get; }

    /// <summary>Scheduler clock (virtual in tests, wall clock in production).</summary>
    public IVirtualClock Clock { get; }

    /// <summary>Step accounting budget shared with the interpreter.</summary>
    public IFuelBudget Fuel { get; }

    /// <summary>Seeded RNG for deterministic draws inside tasks.</summary>
    public ISeededRng Rng { get; }

    /// <summary>False between <see cref="HandleDisconnect"/> and <see cref="HandleReconnect"/>; dispatches drop while false.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
    }

    /// <summary>Currently pending <c>wait</c> count (the 32-sleep cap reads this).</summary>
    public int PendingSleepCount
    {
        get
        {
            lock (_gate)
                return _pendingSleeps;
        }
    }

    /// <summary>Structured run log (virtual timestamps only, so same seed replays identically).</summary>
    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_gate)
                return _log.ToList();
        }
    }

    /// <summary>Appends a line to the structured log.</summary>
    public void Log(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
            _log.Add($"t={Clock.UtcNow:O} {ScriptId} {line}");
    }

    /// <summary>
    /// Binds the live session's <c>Detached</c> token: when it fires (reconnect, clean disconnect, or this plugin unloaded mid-session) the session drops exactly as if <see cref="HandleDisconnect"/> had been called.
    /// Bind once per session scope; never cache a scope across sessions.
    /// </summary>
    public void BindDetached(CancellationToken detached)
    {
        lock (_gate)
        {
            if (_hasDetachedBinding)
            {
                _detachedRegistration.Dispose();
                _hasDetachedBinding = false;
            }

            if (detached.CanBeCanceled)
            {
                _detachedRegistration = detached.Register(() => HandleDisconnect("detached fired"));
                _hasDetachedBinding = true;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> behind the FIFO gate (event handlers and ordered work share it).</summary>
    public async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _fifo.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            _fifo.Release();
        }
    }

    /// <summary>
    /// Starts a background task for <c>start name()</c>: returns the handle immediately with the next sequential id.
    /// The body runs concurrently; use <see cref="AwaitTaskAsync"/> to join it.
    /// </summary>
    public BeaconTask StartTask(string name, Func<BeaconTaskContext, Task<BeaconValue?>> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        Fuel.Spend();

        BeaconTask task;
        TaskCompletionSource<BeaconValue?> settled;
        lock (_gate)
        {
            long id = ++_nextId;
            var cts = new CancellationTokenSource();
            settled = new TaskCompletionSource<BeaconValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = new BeaconTask(id, name, ScriptId, cts, settled.Task);
            _tasks[id] = task;
        }

        Log($"start task={task.Id} name={name}");
        CancellationTokenSource taskCts = task.Cancellation;
        CancellationTokenSource sessionCts = CurrentSessionCts();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(taskCts.Token, sessionCts.Token);
        var context = new BeaconTaskContext(task.Id, name, ScriptId, linked.Token, this);

        _ = Task.Run(async () =>
        {
            try
            {
                BeaconValue? result = await body(context).ConfigureAwait(false);
                lock (_gate)
                {
                    if (task.Status == BeaconTaskStatus.Running)
                    {
                        task.Status = BeaconTaskStatus.Completed;
                        task.Result = result;
                    }
                }

                Log($"done task={task.Id} status={task.Status}");
                settled.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                FailTask(task, BeaconTaskErrorCodes.TaskCancelled, $"Task '{name}' (id {task.Id}) was cancelled.");
                Log($"done task={task.Id} status={task.Status} cancelled");
                settled.TrySetResult(null);
            }
            catch (BeaconRuntimeException ex) when (ex.IsCatchable)
            {
                FailTask(task, ex.Code, ex.Message);
                Log($"done task={task.Id} status={task.Status} code={ex.Code}");
                settled.TrySetResult(null);
            }
            catch (Exception ex)
            {
                FailTask(task, BeaconDiagnosticCodes.Parse, $"Task '{name}' (id {task.Id}) failed: {ex.Message}.");
                Log($"done task={task.Id} status={task.Status} unexpected");
                settled.TrySetResult(null);
            }
            finally
            {
                linked.Dispose();
            }
        });

        return task;
    }

    /// <summary>
    /// Joins a task for <c>await id</c>: returns its value, rethrows its catchable failure, or raises catchable <c>B4004</c> when it was cancelled.
    /// Unknown ids raise catchable <c>B2002</c>.
    /// </summary>
    public async Task<BeaconValue?> AwaitTaskAsync(long taskId, CancellationToken ct = default)
    {
        BeaconTask task;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out BeaconTask? found) || found is null)
                throw UnknownTask(taskId);

            task = found;
        }

        BeaconValue? result = await task.Completion.WaitAsync(ct).ConfigureAwait(false);
        BeaconTaskStatus status;
        string? code;
        string? message;
        lock (_gate)
        {
            status = task.Status;
            code = task.FailureCode;
            message = task.FailureMessage;
        }

        return status switch
        {
            BeaconTaskStatus.Completed => result,
            BeaconTaskStatus.Cancelled => throw CancelledTask(task),
            BeaconTaskStatus.Failed => throw new BeaconRuntimeException(
                code ?? BeaconDiagnosticCodes.Parse,
                message ?? $"Task '{task.Name}' (id {task.Id}) failed.",
                SchedulerSpan(),
                $"Wrap 'await {taskId}' in try/catch err and read err.message."),
            _ => result,
        };
    }

    /// <summary>
    /// Stops a task for <c>cancel task id</c>: the pending sleep (if any) unblocks promptly via token cancellation.
    /// Returns false when the id is unknown or already settled.
    /// </summary>
    public bool CancelTask(long taskId, string reason = "cancel task")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out BeaconTask? task) || task is null)
                return false;

            if (task.Status != BeaconTaskStatus.Running)
                return false;

            _log.Add($"t={Clock.UtcNow:O} {ScriptId} cancel task={taskId} reason={reason}");
            task.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Lists tasks for <c>tasks()</c>: identity plus status, oldest first.</summary>
    public IReadOnlyList<BeaconTaskInfo> ListTasks()
    {
        lock (_gate)
        {
            return _tasks.Values
                .OrderBy(t => t.Id)
                .Select(t => new BeaconTaskInfo(t.Id, t.Name, t.ScriptId, t.Status, t.Result))
                .ToList();
        }
    }

    /// <summary>
    /// Dispatches one event to one handler body with an immutable snapshot of <paramref name="fields"/> (see <see cref="CopyEventSnapshot"/>).
    /// Serializes behind the FIFO gate.
    /// Into a dead session the handler never runs and null is returned.
    /// <see cref="BeaconEngine"/> calls this once per matching <c>on</c> block with an interpreter callback.
    /// </summary>
    public async Task<BeaconValue?> DispatchAsync(
        string eventName,
        IReadOnlyDictionary<string, BeaconValue> fields,
        Func<BeaconTaskContext, IReadOnlyDictionary<string, BeaconValue>, Task<BeaconValue?>> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(handler);

        IReadOnlyDictionary<string, BeaconValue> snapshot = CopyEventSnapshot(fields);
        bool connected;
        CancellationTokenSource sessionCts;
        lock (_gate)
        {
            connected = _connected;
            sessionCts = _sessionCts;
        }

        if (!connected)
        {
            Log($"drop event={eventName} reason=dead-session");
            return null;
        }

        Fuel.Spend();
        await _fifo.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);
            var context = new BeaconTaskContext(0, $"on {eventName}", ScriptId, linked.Token, this);
            Log($"dispatch event={eventName}");
            return await handler(context, snapshot).ConfigureAwait(false);
        }
        finally
        {
            _fifo.Release();
        }
    }

    /// <summary>
    /// The <c>wait</c> primitive: spends one fuel step, floors <paramref name="requested"/> at <see cref="WaitQuantum"/>, enforces the <see cref="MaxPendingSleeps"/> cap (catchable <c>B4006</c> past it), and yields on the virtual clock.
    /// Aborts promptly when <paramref name="taskCt"/> or the session drops.
    /// </summary>
    public async Task WaitAsync(TimeSpan requested, CancellationToken taskCt)
    {
        if (requested < TimeSpan.Zero)
            requested = TimeSpan.Zero;

        Fuel.Spend();
        TimeSpan effective = requested < WaitQuantum ? WaitQuantum : requested;
        CancellationTokenSource sessionCts = CurrentSessionCts();

        lock (_gate)
        {
            if (_pendingSleeps >= MaxPendingSleeps)
            {
                throw new BeaconRuntimeException(
                    BeaconTaskErrorCodes.TooManySleeps,
                    $"I expected at most {MaxPendingSleeps} pending waits, but another 'wait' arrived while all {MaxPendingSleeps} are still sleeping. " +
                    $"Restructure with an 'every' block instead of one task per wait.",
                    SchedulerSpan(),
                    "Move interval work into an 'every' block.");
            }

            _pendingSleeps++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(taskCt, sessionCts.Token);
            await Clock.Delay(effective, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _pendingSleeps--;
        }
    }

    /// <summary>
    /// Drops the session: pending sleeps abort (their tasks settle as Cancelled) and further dispatches are refused until <see cref="HandleReconnect"/>.
    /// Fires from the <see cref="BindDetached"/> binding, a disconnect hook, or a reload boundary.
    /// </summary>
    public void HandleDisconnect(string reason = "disconnect")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CancellationTokenSource stale;
        lock (_gate)
        {
            _connected = false;
            stale = _sessionCts;
            _sessionCts = new CancellationTokenSource();
            _log.Add($"t={Clock.UtcNow:O} {ScriptId} disconnect reason={reason}");
        }

        stale.Cancel();
        stale.Dispose();
    }

    /// <summary>
    /// Re-arms after a disconnect: cancels anything still pending from the dead session (spec: pending sleeps never survive a reconnect) and accepts dispatches again.
    /// <see cref="BeaconEngine.HandleReconnectAsync"/> refires <c>login</c>/<c>reconnect</c> hooks after this returns.
    /// </summary>
    public void HandleReconnect()
    {
        List<BeaconTask> running;
        CancellationTokenSource stale;
        lock (_gate)
        {
            stale = _sessionCts;
            _sessionCts = new CancellationTokenSource();
            _connected = true;
            running = _tasks.Values.Where(t => t.Status == BeaconTaskStatus.Running).ToList();
            foreach (BeaconTask task in running)
                task.Cancellation.Cancel();

            _log.Add($"t={Clock.UtcNow:O} {ScriptId} reconnect cancels={running.Count}");
        }

        stale.Cancel();
        stale.Dispose();
    }

    /// <summary>
    /// Resets task state for a script reload: cancels everything pending and clears the registry (ids keep increasing).
    /// Only <c>saved</c> survives a reload (owned by the persisted store).
    /// Unload passes its own <paramref name="reason"/> so the log names the boundary honestly; the mechanics (cancel everything, clear the registry) are identical.
    /// </summary>
    public void HandleReload(string reason = "reload")
    {
        List<BeaconTask> running;
        lock (_gate)
        {
            running = _tasks.Values.Where(t => t.Status == BeaconTaskStatus.Running).ToList();
            foreach (BeaconTask task in running)
                task.Cancellation.Cancel();

            _tasks.Clear();
            _log.Add($"t={Clock.UtcNow:O} {ScriptId} {reason} resets={running.Count}");
        }
    }

    /// <summary>
    /// Deep-copies an event field map at dispatch.
    /// Values are immutable records, but the containers underneath lists and maps are caller-owned, so every list and map is rebuilt recursively; text, number, yes/no, and none pass through by reference.
    /// This is the copy function the event path dispatches through: mutate the source afterwards and the handler snapshot (and its <c>event</c> map twin) cannot tell.
    /// </summary>
    public static IReadOnlyDictionary<string, BeaconValue> CopyEventSnapshot(
        IReadOnlyDictionary<string, BeaconValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = new Dictionary<string, BeaconValue>(fields.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, BeaconValue> entry in fields)
        {
            ArgumentNullException.ThrowIfNull(entry.Value);
            copy[entry.Key] = CopyBeaconValue(entry.Value);
        }

        return copy;
    }

    /// <summary>Recursively copies one Beacon value (see <see cref="CopyEventSnapshot"/>).</summary>
    public static BeaconValue CopyBeaconValue(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconListValue list => BeaconValue.List(list.Items.Select(CopyBeaconValue).ToList()),
            BeaconMapValue map => BeaconValue.Map(CopyMapEntries(map.Entries)),
            _ => value,
        };
    }

    private static Dictionary<string, BeaconValue> CopyMapEntries(IReadOnlyDictionary<string, BeaconValue> entries)
    {
        var copy = new Dictionary<string, BeaconValue>(entries.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, BeaconValue> entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry.Value);
            copy[entry.Key] = CopyBeaconValue(entry.Value);
        }

        return copy;
    }

    private CancellationTokenSource CurrentSessionCts()
    {
        lock (_gate)
            return _sessionCts;
    }

    private void FailTask(BeaconTask task, string code, string message)
    {
        lock (_gate)
        {
            if (task.Status != BeaconTaskStatus.Running)
                return;

            task.Status = code == BeaconTaskErrorCodes.TaskCancelled
                ? BeaconTaskStatus.Cancelled
                : BeaconTaskStatus.Failed;
            task.FailureCode = code;
            task.FailureMessage = message;
            task.Error = BeaconValue.Map(new Dictionary<string, BeaconValue>
            {
                ["message"] = BeaconValue.Text(message),
            });
        }
    }

    private BeaconRuntimeException UnknownTask(long taskId) => new(
        BeaconDiagnosticCodes.UnknownName,
        $"I expected a live task id for 'await {taskId}', but no task has that id. Task ids come from 'start' and reset on reload.",
        SchedulerSpan(),
        "Write set id to start patrol() then await id.");

    private BeaconRuntimeException CancelledTask(BeaconTask task) => new(
        BeaconTaskErrorCodes.TaskCancelled,
        $"Task '{task.Name}' (id {task.Id}) was cancelled before it finished.",
        SchedulerSpan(),
        $"Wrap 'await {task.Id}' in try/catch err and read err.message.");

    private SourceSpan SchedulerSpan() => new($"{ScriptId}.mcc", 1, 1, 0);
}

/// <summary>
/// The handle a task body or event handler runs with: its own cancellation (task cancel plus session drop linked together), scheduler-scoped <c>wait</c>, structured logging, and the fuel back-edge hook for loops.
/// <see cref="BeaconScheduler.DispatchAsync"/> builds one per dispatch for <c>on</c> bodies (id 0, named <c>on event</c>) and <see cref="BeaconScheduler.StartTask"/> builds one per task.
/// </summary>
public sealed class BeaconTaskContext
{
    internal BeaconTaskContext(
        long taskId, string taskName, string scriptId, CancellationToken cancellationToken, BeaconScheduler scheduler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(scheduler);
        TaskId = taskId;
        TaskName = taskName;
        ScriptId = scriptId;
        CancellationToken = cancellationToken;
        Scheduler = scheduler;
    }

    /// <summary>Task id from <c>start</c>; 0 for an event-dispatch context.</summary>
    public long TaskId { get; }

    /// <summary>Task or handler name for log attribution.</summary>
    public string TaskName { get; }

    /// <summary>Owning script id.</summary>
    public string ScriptId { get; }

    /// <summary>Linked task-plus-session token: cancel, reconnect, reload, and unload all flow through it.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The owning scheduler.</summary>
    public BeaconScheduler Scheduler { get; }

    /// <summary>
    /// Yields for <paramref name="requested"/> (floored at the 100 ms quantum, capped at 32 concurrent sleeps per script).
    /// Aborts promptly on <see cref="CancellationToken"/> or a session drop; pass an extra <paramref name="ct"/> to also abort on a narrower scope.
    /// </summary>
    public async Task WaitAsync(TimeSpan requested, CancellationToken ct = default)
    {
        if (!ct.CanBeCanceled)
        {
            await Scheduler.WaitAsync(requested, CancellationToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, ct);
        await Scheduler.WaitAsync(requested, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Appends a line to the scheduler's structured log.</summary>
    public void Log(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Scheduler.Log($"{TaskName} {line}");
    }

    /// <summary>Spends one fuel step for a loop back-edge (the budget accounting hook).</summary>
    public void SpendBackEdge() => Scheduler.Fuel.Spend();
}

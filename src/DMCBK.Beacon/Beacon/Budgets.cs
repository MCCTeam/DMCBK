namespace DMCBK.Core.Beacon;

/// <summary>
/// Budget numbers: 100k operations fuel plus 5 s wall clock per dispatch.
/// </summary>
public static class BeaconBudgetLimits
{
    /// <summary>Operations fuel per dispatch (loop back-edges, calls, waits, host calls).</summary>
    public const long MaxOperations = 100_000;

    /// <summary>Compute-without-yielding window per dispatch; each <c>wait</c> re-arms it.</summary>
    public static readonly TimeSpan MaxWallClock = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum nested script calls.
    /// Deep recursion aborts here, long before the CLR stack is involved: at 256 script frames the host is never allowed to decide via StackOverflow.
    /// </summary>
    public const int MaxCallDepth = 256;
}

/// <summary>
/// Per-dispatch budget enforcement wrapping the <see cref="IFuelBudget"/> seam plus a wall clock over <see cref="IVirtualClock"/> (virtual in tests, so budget tests never flake).
/// </summary>
/// <remarks>
/// <para>Semantics:</para>
/// <list type="bullet">
/// <item>
/// <see cref="Reset"/> arms a fresh dispatch window: fuel zeroed, wall clock from now, seed plus handler recorded for failure reports and replay.
/// </item>
/// <item>
/// <see cref="Spend"/> checks the wall window first, then the fuel seam.
/// Either breach throws a catchable B4007 with handler, line, locals, task stack, seed, and the <c>every</c> suggestion (see <see cref="BeaconErrors"/>).
/// </item>
/// <item>
/// <see cref="NoteWaitCompleted"/> re-arms the wall window after every <c>wait</c>: the watchdog guards compute that never yields, not legitimate sleeping.
/// A 24/7 patrol with <c>wait 3 seconds</c> between moves must survive; a wait-less spin must not.
/// </item>
/// <item><see cref="TrackCall"/> bounds nesting at <see cref="BeaconBudgetLimits.MaxCallDepth"/>.</item>
/// <item>
/// <see cref="EnterTask"/> re-arms a background task as its own window (fuel inherited, wall clock fresh); the returned scope restores the parent window when the body settles.
/// </item>
/// </list>
/// </remarks>
public sealed class BeaconDispatchBudget
{
    private readonly List<string> _stack = [];
    private string _handler = "top-level";

    /// <summary>Builds enforcement over a fuel seam and a clock (both injectable for tests).</summary>
    public BeaconDispatchBudget(IFuelBudget fuel, IVirtualClock clock, TimeSpan? wallClockLimit = null, int maxCallDepth = BeaconBudgetLimits.MaxCallDepth)
    {
        ArgumentNullException.ThrowIfNull(fuel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCallDepth, 0);
        Fuel = fuel;
        Clock = clock;
        WallClockLimit = wallClockLimit ?? BeaconBudgetLimits.MaxWallClock;
        MaxCallDepth = maxCallDepth;
        StartedAt = clock.UtcNow;
    }

    /// <summary>Step accounting seam (shared per engine; reset per dispatch).</summary>
    public IFuelBudget Fuel { get; }

    /// <summary>Clock the wall window is measured on.</summary>
    public IVirtualClock Clock { get; }

    /// <summary>Compute-without-yielding allowance (default <see cref="BeaconBudgetLimits.MaxWallClock"/>).</summary>
    public TimeSpan WallClockLimit { get; }

    /// <summary>Nesting allowance (default <see cref="BeaconBudgetLimits.MaxCallDepth"/>).</summary>
    public int MaxCallDepth { get; set; }

    /// <summary>Seed of the current dispatch (recorded on failure for replay).</summary>
    public int Seed { get; private set; }

    /// <summary>Handler of the current dispatch (<c>top-level</c>, <c>on chat</c>, <c>every</c>, task).</summary>
    public string Handler => _handler;

    /// <summary>Virtual time the current window started (dispatch, last wait, or task start).</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Current nested-call depth.</summary>
    public int CallDepth { get; private set; }

    /// <summary>Function-name stack, outermost first (for abort dumps).</summary>
    public IReadOnlyList<string> CallStack
    {
        get
        {
            lock (_stack)
                return _stack.ToList();
        }
    }

    /// <summary>Steps spent in the current fuel window.</summary>
    public long Used => Fuel.Used;

    /// <summary>Compute time since the window started (dispatch, last wait, or task start).</summary>
    public TimeSpan Elapsed => Clock.UtcNow - StartedAt;

    /// <summary>Arms a fresh dispatch: fuel zeroed, wall clock from now, seed plus handler recorded.</summary>
    public void Reset(int seed, string handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        Fuel.Reset();
        Seed = seed;
        _handler = handler;
        StartedAt = Clock.UtcNow;
        CallDepth = 0;
        lock (_stack)
            _stack.Clear();
    }

    /// <summary>
    /// Re-arms the wall window after a <c>wait</c> completes.
    /// Fuel, seed, handler, and the call stack are untouched: yielding proves liveness, it does not start a new dispatch.
    /// </summary>
    public void NoteWaitCompleted()
    {
        StartedAt = Clock.UtcNow;
    }

    /// <summary>
    /// Re-arms a background task as its own window (fuel inherited from the spawning dispatch, wall clock fresh, stack cleared).
    /// The returned scope restores the parent window.
    /// </summary>
    public IDisposable EnterTask(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        string parentHandler = _handler;
        DateTimeOffset parentStarted = StartedAt;
        List<string> parentStack;
        lock (_stack)
        {
            parentStack = _stack.ToList();
            _stack.Clear();
        }

        _handler = "task " + taskName;
        StartedAt = Clock.UtcNow;
        return new TaskScope(this, parentHandler, parentStarted, parentStack);
    }

    /// <summary>
    /// Spends one accounting step for a loop back-edge, call, wait, or host call, checking the wall window first.
    /// Breaches throw catchable B4007 with handler, line, locals, task stack, seed, and the <c>every</c> suggestion.
    /// Locals render lazily, only on failure.
    /// </summary>
    public void Spend(BeaconScope? scope, SourceSpan? span)
    {
        SourceSpan at = span?.Origin ?? new SourceSpan("unknown.bcn", 1, 1, 0);
        if (Elapsed > WallClockLimit)
        {
            throw BeaconErrors.WallClockExceeded(
                _handler, at, BeaconDiagnostics.SummarizeScope(scope), CallStack, Seed, Elapsed, WallClockLimit);
        }

        try
        {
            Fuel.Spend();
        }
        catch (BeaconFuelExhaustedException ex)
        {
            throw BeaconErrors.BudgetExhausted(
                _handler, at, BeaconDiagnostics.SummarizeScope(scope), CallStack, Seed, ex.Used, ex.Limit);
        }
    }

    /// <summary>
    /// Guards one nested call: past <see cref="MaxCallDepth"/> throws catchable B4007 naming the function, before the CLR stack is ever involved.
    /// Dispose (via using) on exit.
    /// </summary>
    public IDisposable TrackCall(string functionName, SourceSpan? span)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (CallDepth >= MaxCallDepth)
        {
            SourceSpan at = span?.Origin ?? new SourceSpan("unknown.bcn", 1, 1, 0);
            List<string> stack = CallStack.ToList();
            stack.Add(functionName);
            throw BeaconErrors.RecursionTooDeep(_handler, at, functionName, CallDepth + 1, MaxCallDepth, Seed);
        }

        CallDepth++;
        lock (_stack)
            _stack.Add(functionName);

        return new CallScope(this);
    }

    private void PopCall()
    {
        CallDepth--;
        lock (_stack)
        {
            if (_stack.Count > 0)
                _stack.RemoveAt(_stack.Count - 1);
        }
    }

    private sealed class CallScope(BeaconDispatchBudget owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.PopCall();
            }
        }
    }

    private sealed class TaskScope(
        BeaconDispatchBudget owner, string parentHandler, DateTimeOffset parentStarted, List<string> parentStack) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._handler = parentHandler;
                owner.StartedAt = parentStarted;
                lock (owner._stack)
                {
                    owner._stack.Clear();
                    owner._stack.AddRange(parentStack);
                }
            }
        }
    }
}

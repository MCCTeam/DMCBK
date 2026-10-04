namespace DMCBK.Core.Beacon;

/// <summary>Which session moment a lifecycle hook runs at.</summary>
public enum BeaconLifecycleEvent
{
    /// <summary>The script finished loading (top-level statements ran).</summary>
    Start,

    /// <summary>A session reached play. Refires after every reconnect.</summary>
    Login,

    /// <summary>A clean logout (not a dropped connection).</summary>
    Logout,

    /// <summary>The connection dropped; tasks park, sleeps cancel.</summary>
    Disconnect,

    /// <summary>A new session is live after a drop. Refires alongside <see cref="Login"/>.</summary>
    Reconnect,
}

/// <summary>
/// One <c>every</c> firing: which timer, whether it is a late catch-up, and how many whole intervals were skipped to coalesce it.
/// </summary>
/// <param name="Name">Timer name from registration.</param>
/// <param name="WasCatchUp">True when at least one interval was missed (late fire).</param>
/// <param name="MissedIntervals">Whole intervals skipped beyond the one that fired.</param>
public sealed record BeaconEveryFire(string Name, bool WasCatchUp, long MissedIntervals);

/// <summary>
/// One <c>every N seconds|minutes|hours</c> block: its interval and next fire time.
/// Fires at most once per <see cref="BeaconLifecycle.EveryTick"/> call: missed intervals coalesce into a single catch-up instead of a greeting storm.
/// </summary>
public sealed class BeaconEveryTimer
{
    internal BeaconEveryTimer(string name, TimeSpan interval, DateTimeOffset nextFire)
    {
        Name = name;
        Interval = interval;
        NextFire = nextFire;
    }

    /// <summary>Timer name.</summary>
    public string Name { get; }

    /// <summary>Fire interval (clamped to at least one second at registration).</summary>
    public TimeSpan Interval { get; }

    /// <summary>Next scheduled fire time on the scheduler clock.</summary>
    public DateTimeOffset NextFire { get; internal set; }

    internal bool TryFire(DateTimeOffset now, out BeaconEveryFire fire)
    {
        if (now < NextFire)
        {
            fire = new BeaconEveryFire(Name, WasCatchUp: false, MissedIntervals: 0);
            return false;
        }

        long missed = (now - NextFire).Ticks / Interval.Ticks;
        fire = new BeaconEveryFire(Name, WasCatchUp: missed >= 1, MissedIntervals: missed);
        NextFire = now + Interval;
        return true;
    }
}

/// <summary>
/// One <c>in count unit do</c> block: fires a single time at <see cref="FireAt"/>, then it is gone.
/// Pending one-shots cancel on reconnect (like pending <c>wait</c> sleeps) and on reload.
/// </summary>
public sealed class BeaconOneShot
{
    internal BeaconOneShot(string name, TimeSpan delay, DateTimeOffset fireAt)
    {
        Name = name;
        Delay = delay;
        FireAt = fireAt;
    }

    /// <summary>Timer name.</summary>
    public string Name { get; }

    /// <summary>Requested delay.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Clock time the body runs at.</summary>
    public DateTimeOffset FireAt { get; internal set; }

    /// <summary>True once the body ran (or the timer was cancelled).</summary>
    public bool Settled { get; internal set; }
}

/// <summary>
/// Lifecycle hooks plus <c>every</c> timers for one script.
/// Hooks fire in registration order through <see cref="FireAsync"/> (<c>start</c> on load, <c>login</c>/<c>reconnect</c> after every reconnect, <c>logout</c>/<c>disconnect</c> on the way down).
/// Timers advance on the scheduler clock through <see cref="EveryTick"/>: each due timer fires at most once per call and reschedules to now plus its interval, so a long outage produces exactly one catch-up run and then the normal cadence.
/// <see cref="BeaconEngine.TickEveryAsync"/> runs due timer bodies on the per-script scheduler.
/// </summary>
public sealed class BeaconLifecycle
{
    /// <summary>Minimum <c>every</c> interval; smaller registrations clamp to this.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<BeaconLifecycleEvent, List<(string Name, Func<CancellationToken, Task> Handler)>> _hooks = new();
    private readonly List<BeaconEveryTimer> _timers = [];
    private readonly List<BeaconOneShot> _oneShots = [];
    private readonly List<BeaconLifecycleEvent> _fired = [];
    private readonly List<string> _log = [];

    /// <summary>Builds lifecycle state over the scheduler clock.</summary>
    public BeaconLifecycle(IVirtualClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Clock = clock;
    }

    /// <summary>Scheduler clock (virtual in tests).</summary>
    public IVirtualClock Clock { get; }

    /// <summary>Events fired so far, in order (for tests and timeout dumps).</summary>
    public IReadOnlyList<BeaconLifecycleEvent> Fired
    {
        get
        {
            lock (_gate)
                return _fired.ToList();
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

    /// <summary>Registers a hook body for <paramref name="ev"/>; hooks fire in registration order.</summary>
    public void OnHook(BeaconLifecycleEvent ev, string name, Func<CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_hooks.TryGetValue(ev, out List<(string Name, Func<CancellationToken, Task> Handler)>? list) || list is null)
            {
                list = [];
                _hooks[ev] = list;
            }

            list.Add((name, handler));
        }
    }

    /// <summary>
    /// Fires every hook for <paramref name="ev"/> in registration order.
    /// All hooks run even when one fails; failures aggregate and throw together after the last hook.
    /// </summary>
    public async Task FireAsync(BeaconLifecycleEvent ev, CancellationToken ct = default)
    {
        List<(string Name, Func<CancellationToken, Task> Handler)> hooks;
        lock (_gate)
        {
            _fired.Add(ev);
            _log.Add($"fire {ev}");
            hooks = _hooks.TryGetValue(ev, out List<(string Name, Func<CancellationToken, Task> Handler)>? list) && list is not null
                ? list.ToList()
                : [];
        }

        List<Exception>? failures = null;
        foreach ((string name, Func<CancellationToken, Task> handler) in hooks)
        {
            try
            {
                await handler(ct).ConfigureAwait(false);
                lock (_gate)
                    _log.Add($"fire {ev} hook={name} ok");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_gate)
                    _log.Add($"fire {ev} hook={name} failed");

                failures ??= [];
                failures.Add(ex);
            }
        }

        if (failures is { Count: > 0 })
            throw new AggregateException($"Beacon lifecycle event '{ev}' had {failures.Count} failing hook(s).", failures);
    }

    /// <summary>
    /// Registers an <c>every</c> block firing every <paramref name="interval"/> from now.
    /// Intervals below one second clamp to one second (a sub-second <c>every</c> is a spin).
    /// </summary>
    public BeaconEveryTimer RegisterEvery(string name, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (interval < MinimumInterval)
            interval = MinimumInterval;

        var timer = new BeaconEveryTimer(name, interval, Clock.UtcNow + interval);
        lock (_gate)
        {
            _timers.Add(timer);
            _log.Add($"every {name} interval={interval}");
        }

        return timer;
    }

    /// <summary>Registers an <c>every count unit</c> block (see <see cref="EveryInterval"/>).</summary>
    public BeaconEveryTimer RegisterEvery(string name, double count, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        return RegisterEvery(name, EveryInterval(count, unit));
    }

    /// <summary>
    /// Returns the timers due at the clock's now, at most one firing each: a timer whose time has come fires once and reschedules to now plus its interval, so missed intervals coalesce into a single catch-up (<see cref="BeaconEveryFire.WasCatchUp"/>) instead of queuing a storm.
    /// </summary>
    public IReadOnlyList<BeaconEveryFire> EveryTick()
    {
        DateTimeOffset now = Clock.UtcNow;
        var due = new List<BeaconEveryFire>();
        lock (_gate)
        {
            foreach (BeaconEveryTimer timer in _timers)
            {
                if (timer.TryFire(now, out BeaconEveryFire fire))
                {
                    due.Add(fire);
                    _log.Add($"every {timer.Name} fire catchup={fire.WasCatchUp} missed={fire.MissedIntervals}");
                }
            }
        }

        return due;
    }

    /// <summary>
    /// Registers an <c>in count unit do</c> block firing once after <paramref name="delay"/>.
    /// Negative delays clamp to zero (the next tick runs the body).
    /// </summary>
    public BeaconOneShot RegisterOnce(string name, TimeSpan delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var oneShot = new BeaconOneShot(name, delay, Clock.UtcNow + delay);
        lock (_gate)
        {
            _oneShots.Add(oneShot);
            _log.Add($"once {name} delay={delay}");
        }

        return oneShot;
    }

    /// <summary>Registers an <c>in count unit do</c> block (see <see cref="EveryInterval"/>).</summary>
    public BeaconOneShot RegisterOnce(string name, double count, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        return RegisterOnce(name, EveryInterval(count, unit));
    }

    /// <summary>
    /// Returns the one-shots due at the clock's now, each at most once: due timers settle and leave the registry, so a fired body never refires even when ticks run long.
    /// </summary>
    public IReadOnlyList<BeaconOneShot> OnceTick()
    {
        DateTimeOffset now = Clock.UtcNow;
        var due = new List<BeaconOneShot>();
        lock (_gate)
        {
            foreach (BeaconOneShot oneShot in _oneShots)
            {
                if (!oneShot.Settled && now >= oneShot.FireAt)
                {
                    oneShot.Settled = true;
                    due.Add(oneShot);
                    _log.Add($"once {oneShot.Name} fire");
                }
            }

            _oneShots.RemoveAll(o => o.Settled);
        }

        return due;
    }

    /// <summary>Pending one-shot count (unfired timers).</summary>
    public int PendingOnceCount
    {
        get
        {
            lock (_gate)
                return _oneShots.Count(o => !o.Settled);
        }
    }

    /// <summary>
    /// Re-arms after a reconnect.
    /// Timer next-fire times are left alone on purpose: the next <see cref="EveryTick"/> coalesces any outage into exactly one catch-up run, then the normal cadence resumes.
    /// <see cref="BeaconEngine.HandleReconnectAsync"/> refires <c>login</c>/<c>reconnect</c> hooks (see <see cref="FireAsync"/>) after this returns.
    /// </summary>
    public void HandleReconnect()
    {
        int cancelled;
        lock (_gate)
        {
            cancelled = _oneShots.Count(o => !o.Settled);
            _oneShots.Clear();
            _log.Add($"reconnect every-armed={_timers.Count} once-cancelled={cancelled}");
        }
    }

    /// <summary>Clears hooks, timers, one-shots, and the fired record for a script reload.</summary>
    public void HandleReload()
    {
        lock (_gate)
        {
            _hooks.Clear();
            _timers.Clear();
            _oneShots.Clear();
            _fired.Clear();
            _log.Add("reload lifecycle-cleared");
        }
    }

    /// <summary>
    /// Parses an <c>every</c>/<c>wait</c> unit to a span.
    /// Accepts seconds, minutes, hours (plus singulars), case-insensitively; anything else raises catchable <c>B3005</c> naming the unit.
    /// </summary>
    public static TimeSpan EveryInterval(double count, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (double.IsNaN(count) || double.IsInfinity(count) || count < 0)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownUnit,
                $"I expected a non-negative count for the interval, but found {count}.",
                LifecycleSpan(),
                "Write every 60 seconds or every 5 minutes.");
        }

        return unit.Trim().ToLowerInvariant() switch
        {
            "second" or "seconds" => TimeSpan.FromSeconds(count),
            "minute" or "minutes" => TimeSpan.FromMinutes(count),
            "hour" or "hours" => TimeSpan.FromHours(count),
            _ => throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownUnit,
                $"I expected seconds, minutes, or hours after 'every', but found '{unit}'.",
                LifecycleSpan(),
                "Write every 60 seconds or every 5 minutes."),
        };
    }

    private static SourceSpan LifecycleSpan() => new("lifecycle.mcc", 1, 1, 0);
}

namespace DMCBK.Core.Beacon;

/// <summary>
/// Per-script <c>cooldown</c> windows: <c>on tps cooldown 300 seconds named "tps-warn"</c>.
/// </summary>
/// <remarks>
/// <para>Semantics:</para>
/// <list type="bullet">
/// <item>
/// A firing inside the window is skipped and debug-logged (the caller logs; this registry reports the remaining time via <see cref="TryAcquire"/> and <see cref="BuildSkipMessage"/>).
/// </item>
/// <item>
/// Windows are per script: the key is (script id, throttle name).
/// Two handlers in one script sharing a name share the window; two scripts with the same name do not.
/// </item>
/// <item>
/// Windows are RAM-only: a reload resets them (<see cref="ResetScript"/>), a new registry starts empty, and nothing persists.
/// </item>
/// <item>
/// Evaluation order note: the bus checks the throttle before invoking the handler (throttle-before-filter, consume-on-attempt), because the evaluator offers no filter-only entry point to check the <c>when</c> first.
/// A <c>when</c>-false dispatch therefore still consumes the window.
/// A public filter probe or a filter-passed signal on <see cref="BeaconRunResult"/> would enable throttle-after-filter; the v1 behavior is documented here, not silent.
/// </item>
/// </list>
/// </remarks>
public sealed class BeaconThrottleRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ScriptId, string Name), DateTimeOffset> _lastFire = new();

    /// <summary>Builds a registry over <paramref name="clock"/> (virtual in tests).</summary>
    public BeaconThrottleRegistry(IVirtualClock? clock = null)
    {
        Clock = clock ?? SystemClock.Shared;
    }

    /// <summary>The clock windows are measured on.</summary>
    public IVirtualClock Clock { get; }

    /// <summary>
    /// Peeks without marking: true when a firing right now would be throttled.
    /// </summary>
    public bool IsThrottled(string scriptId, string throttleName, TimeSpan window, out TimeSpan remaining)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(throttleName);
        if (window <= TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        DateTimeOffset now = Clock.UtcNow;
        lock (_gate)
        {
            if (TryGetRemainingLocked((scriptId, throttleName), now, window, out remaining))
                return true;
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Attempts a firing: false (plus <paramref name="remaining"/>) when inside the window and the firing must be skipped; true when allowed, marking the window start at now.
    /// </summary>
    public bool TryAcquire(string scriptId, string throttleName, TimeSpan window, out TimeSpan remaining)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(throttleName);
        if (window <= TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
            return true;
        }

        DateTimeOffset now = Clock.UtcNow;
        lock (_gate)
        {
            if (TryGetRemainingLocked((scriptId, throttleName), now, window, out remaining))
                return false;

            _lastFire[(scriptId, throttleName)] = now;
        }

        remaining = TimeSpan.Zero;
        return true;
    }

    private bool TryGetRemainingLocked(
        (string ScriptId, string Name) key, DateTimeOffset now, TimeSpan window, out TimeSpan remaining)
    {
        if (_lastFire.TryGetValue(key, out DateTimeOffset last))
        {
            TimeSpan elapsed = now - last;
            if (elapsed < window)
            {
                remaining = window - elapsed;
                return true;
            }
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Marks a firing now without checking (the post-filter path: peek with <see cref="IsThrottled"/>, run the filter, then mark only on filter-pass).
    /// </summary>
    public void Mark(string scriptId, string throttleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(throttleName);
        lock (_gate)
            _lastFire[(scriptId, throttleName)] = Clock.UtcNow;
    }

    /// <summary>Resets every window for <paramref name="scriptId"/> (reload resets; RAM-only).</summary>
    public void ResetScript(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            var keys = _lastFire.Keys
                .Where(k => string.Equals(k.ScriptId, scriptId, StringComparison.Ordinal))
                .ToList();
            foreach ((string ScriptId, string Name) key in keys)
                _lastFire.Remove(key);
        }
    }

    /// <summary>Clears every window (primarily for tests).</summary>
    public void Clear()
    {
        lock (_gate)
            _lastFire.Clear();
    }

    /// <summary>
    /// Resolves an <c>on</c> cooldown clause to a window: null when absent or unresolvable (non-literal count, unknown unit, negative count), in which case the caller fires unthrottled (fail open; lint already rejects malformed clauses on clean scripts).
    /// </summary>
    public static TimeSpan? ResolveWindow(BeaconCooldown? cooldown)
    {
        if (cooldown is null)
            return null;

        BeaconExpr count = cooldown.Count;
        while (count is ParenExpr paren)
            count = paren.Inner;

        if (count is not NumberLiteral number
            || double.IsNaN(number.Value)
            || double.IsInfinity(number.Value)
            || number.Value < 0)
            return null;

        return cooldown.Unit switch
        {
            "second" => TimeSpan.FromSeconds(number.Value),
            "minute" => TimeSpan.FromMinutes(number.Value),
            "hour" => TimeSpan.FromHours(number.Value),
            _ => null,
        };
    }

    /// <summary>Builds the debug-log line for a skipped firing (the bus logs it; tests assert it).</summary>
    public static string BuildSkipMessage(string scriptId, string throttleName, TimeSpan remaining)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(throttleName);
        return $"Beacon throttle '{throttleName}' for script '{scriptId}' skipped a firing " +
            $"({remaining.TotalSeconds:F1}s remaining in window).";
    }
}

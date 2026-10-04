namespace DMCBK.Core.Beacon;

/// <summary>
/// The global chat bucket: one token bucket shared by every script in the process, burst 8 per 10 s, over <see cref="IVirtualClock"/> (virtual in tests, so flood tests never sleep).
/// </summary>
/// <remarks>
/// <para>
/// Per-script buckets are explicitly rejected as bypassable (N scripts would multiply the allowance by N); the N-script flood test proves the single shared bucket throttles globally.
/// Overflow queues, then warns naming the script and suggesting <c>wait</c>; messages are never silently dropped.
/// Throttled sends await the refill on the clock, so a <c>wait 0</c>-style spin cannot burn through the bucket: without a clock advance no slot ever opens.
/// </para>
/// <para>
/// Scope: <c>say</c> and <c>whisper</c> consume tokens; <c>server</c>/<c>mcc</c> take the command path and do not.
/// <see cref="Muted"/> is the mute gag point: when set, chat verbs skip the transport but still audit-log as muted (logic keeps running, chat stays silent).
/// </para>
/// </remarks>
public sealed class BeaconChatBucket
{
    /// <summary>Burst allowance: that many sends pass immediately on a full bucket.</summary>
    public const int BurstCapacity = 8;

    /// <summary>Refill window: a full burst regenerates per this interval (0.8 tokens/s).</summary>
    public static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private double _tokens = BurstCapacity;
    private DateTimeOffset _lastRefill;

    /// <summary>Builds a full bucket over <paramref name="clock"/> (virtual in tests).</summary>
    public BeaconChatBucket(IVirtualClock? clock = null)
    {
        Clock = clock ?? SystemClock.Shared;
        _lastRefill = Clock.UtcNow;
    }

    /// <summary>The clock refills are measured on.</summary>
    public IVirtualClock Clock { get; }

    /// <summary>
    /// The mute gag point: when true, chat verbs skip the transport and audit-log as muted instead of sending.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>Currently available tokens (for tests and status lines).</summary>
    public double Available
    {
        get
        {
            lock (_gate)
            {
                RefillLocked();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// Tries to consume one token for <paramref name="scriptId"/>.
    /// True means send now; false means queued, with <paramref name="retryAfter"/> saying how long the slot needs.
    /// </summary>
    public bool TryAcquire(string scriptId, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            RefillLocked();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((1.0 - _tokens) / RatePerSecond);
            return false;
        }
    }

    /// <summary>
    /// Consumes one token, waiting for the refill when empty (never drops).
    /// The first wait per script per dispatch appends a B4008 warning naming the script and suggesting <c>wait</c>; later waits in the same dispatch queue silently.
    /// Returns true when the slot needed a wait (a cooperative yield: callers re-arm watchdog windows on true).
    /// </summary>
    public async Task<bool> WaitForSlotAsync(
        string scriptId, SourceSpan span, string verb, List<BeaconDiagnostic> warnings, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(warnings);

        bool waited = false;
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                RefillLocked();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return waited;
                }

                wait = TimeSpan.FromSeconds((1.0 - _tokens) / RatePerSecond);
            }

            waited = true;
            WarnOnce(scriptId, span, verb, warnings);

            if (wait < TimeSpan.FromMilliseconds(1))
                wait = TimeSpan.FromMilliseconds(1);

            await Clock.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static double RatePerSecond => BurstCapacity / RefillInterval.TotalSeconds;

    private void RefillLocked()
    {
        DateTimeOffset now = Clock.UtcNow;
        double elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(BurstCapacity, _tokens + elapsed * RatePerSecond);
            _lastRefill = now;
        }
    }

    private static void WarnOnce(string scriptId, SourceSpan span, string verb, List<BeaconDiagnostic> warnings)
    {
        foreach (BeaconDiagnostic existing in warnings)
        {
            if (string.Equals(existing.Code, BeaconDiagnosticCodes.ChatThrottled, StringComparison.Ordinal)
                && existing.Message.Contains($"'{scriptId}'", StringComparison.Ordinal))
                return;
        }

        warnings.Add(new BeaconDiagnostic(
            BeaconDiagnosticCodes.ChatThrottled,
            BeaconSeverity.Warning,
            $"Script '{scriptId}' hit the global chat bucket (burst {BurstCapacity} per {RefillInterval.TotalSeconds:F0} seconds); "
            + $"this {verb} queued instead of dropping. Add waits between sends so bursts stay under the bucket.",
            span.Origin,
            "Add 'wait 1 second' between sends."));
    }
}

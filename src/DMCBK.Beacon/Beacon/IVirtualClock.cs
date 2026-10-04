namespace DMCBK.Core.Beacon;

/// <summary>
/// Injectable clock behind the Beacon scheduler seam.
/// Production runs on wall time; tests inject <see cref="VirtualClock"/> and fast-forward deterministically, so no scheduler test ever sleeps.
/// </summary>
public interface IVirtualClock
{
    /// <summary>Current time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Completes after <paramref name="delay"/> (virtual time on <see cref="VirtualClock"/>).</summary>
    Task Delay(TimeSpan delay, CancellationToken ct = default);

    /// <summary>
    /// Moves the clock forward, completing every delay whose deadline has passed.
    /// A no-op on the wall-clock implementation, where time passes on its own.
    /// </summary>
    void Advance(TimeSpan delta);
}

/// <summary>
/// Manually advanced test double.
/// Starts at <see cref="DateTimeOffset.UnixEpoch"/> unless a start is given; <see cref="Delay"/> waiters complete only via <see cref="Advance"/>, never via wall time.
/// </summary>
public sealed class VirtualClock : IVirtualClock
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private DateTimeOffset _now;

    /// <summary>Builds a clock at <paramref name="start"/> (Unix epoch by default).</summary>
    public VirtualClock(DateTimeOffset? start = null)
    {
        _now = start ?? DateTimeOffset.UnixEpoch;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
                return _now;
        }
    }

    /// <inheritdoc />
    public Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _waiters.Add(new Waiter(_now + delay, gate));

        if (ct.CanBeCanceled)
        {
            CancellationTokenRegistration registration = ct.Register(() =>
            {
                bool removed;
                lock (_gate)
                    removed = _waiters.RemoveAll(w => ReferenceEquals(w.Gate, gate)) > 0;

                if (removed)
                    gate.TrySetCanceled(ct);
            });
            _ = gate.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return gate.Task;
    }

    /// <inheritdoc />
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<Waiter> due;
        lock (_gate)
        {
            _now += delta;
            due = _waiters.Where(w => w.Deadline <= _now).ToList();
            foreach (Waiter waiter in due)
                _waiters.Remove(waiter);
        }

        foreach (Waiter waiter in due)
            waiter.Gate.TrySetResult(true);
    }

    private sealed record Waiter(DateTimeOffset Deadline, TaskCompletionSource<bool> Gate);
}

/// <summary>Production wall-clock implementation.</summary>
public sealed class SystemClock : IVirtualClock
{
    private SystemClock()
    {
    }

    /// <summary>The shared wall-clock instance.</summary>
    public static SystemClock Shared { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);

    /// <inheritdoc />
    public void Advance(TimeSpan delta)
    {
    }
}

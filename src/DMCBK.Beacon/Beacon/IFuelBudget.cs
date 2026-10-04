namespace DMCBK.Core.Beacon;

/// <summary>
/// Step accounting for Beacon execution (loop back-edges, calls, waits, host calls charge here).
/// Aborts fail closed with <see cref="BeaconFuelExhaustedException"/>.
/// </summary>
public interface IFuelBudget
{
    /// <summary>Step ceiling.</summary>
    long Limit { get; }

    /// <summary>Steps spent since the last <see cref="Reset"/>.</summary>
    long Used { get; }

    /// <summary>Steps left before exhaustion.</summary>
    long Remaining => Limit - Used;

    /// <summary>True once <see cref="Used"/> has reached <see cref="Limit"/>.</summary>
    bool IsExhausted => Used >= Limit;

    /// <summary>Spends <paramref name="steps"/>; throws <see cref="BeaconFuelExhaustedException"/> without spending when over budget.</summary>
    void Spend(long steps = 1);

    /// <summary>Tries to spend; returns false without spending when over budget.</summary>
    bool TrySpend(long steps = 1);

    /// <summary>Zeroes <see cref="Used"/> (dispatch and reconnect boundaries).</summary>
    void Reset();
}

/// <summary>Raised when a Beacon execution exceeds its fuel budget; handler attribution travels with the B4007 abort.</summary>
public sealed class BeaconFuelExhaustedException(long limit, long used)
    : Exception($"Beacon fuel exhausted: spent {used} of {limit} steps.")
{
    /// <summary>The ceiling that was hit.</summary>
    public long Limit { get; } = limit;

    /// <summary>Steps spent when the abort fired.</summary>
    public long Used { get; } = used;
}

/// <summary>Locked in-memory fuel budget.</summary>
public sealed class FuelBudget : IFuelBudget
{
    /// <summary>Default ceiling: 100k operations per dispatch.</summary>
    public const long DefaultLimit = 100_000;

    private readonly object _gate = new();
    private long _used;

    /// <summary>Builds a budget with <paramref name="limit"/> steps.</summary>
    public FuelBudget(long limit = DefaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        Limit = limit;
    }

    /// <inheritdoc />
    public long Limit { get; }

    /// <inheritdoc />
    public long Used
    {
        get
        {
            lock (_gate)
                return _used;
        }
    }

    /// <inheritdoc />
    public void Spend(long steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        lock (_gate)
        {
            if (Limit - _used < steps)
                throw new BeaconFuelExhaustedException(Limit, _used);

            _used += steps;
        }
    }

    /// <inheritdoc />
    public bool TrySpend(long steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        lock (_gate)
        {
            if (Limit - _used < steps)
                return false;

            _used += steps;
            return true;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
            _used = 0;
    }
}

namespace DMCBK.Core.Beacon;

/// <summary>
/// Per-dispatch seeded RNG.
/// Each script dispatch gets a fresh instance from a recorded seed so a failure can be replayed exactly; the seed travels with failure reports.
/// </summary>
public interface ISeededRng
{
    /// <summary>The seed this instance was built from (captured for replay).</summary>
    int Seed { get; }

    /// <summary>Non-negative value below <paramref name="maxExclusive"/>.</summary>
    int Next(int maxExclusive);

    /// <summary>Value in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>Value in [0.0, 1.0).</summary>
    double NextDouble();

    /// <summary>Uniformly picked element.</summary>
    T Pick<T>(IReadOnlyList<T> items);
}

/// <summary>Locked <see cref="Random"/> wrapper with a captured seed (<see cref="Random"/> is not thread-safe).</summary>
public sealed class SeededRng : ISeededRng
{
    private readonly object _gate = new();
    private readonly Random _inner;

    /// <summary>Builds a generator from an explicit seed.</summary>
    public SeededRng(int seed)
    {
        Seed = seed;
        _inner = new Random(seed);
    }

    /// <inheritdoc />
    public int Seed { get; }

    /// <summary>Builds a generator from shared entropy; record <see cref="Seed"/> for replay.</summary>
    public static SeededRng FromEntropy() => new(Random.Shared.Next());

    /// <inheritdoc />
    public int Next(int maxExclusive)
    {
        lock (_gate)
            return _inner.Next(maxExclusive);
    }

    /// <inheritdoc />
    public int Next(int minInclusive, int maxExclusive)
    {
        lock (_gate)
            return _inner.Next(minInclusive, maxExclusive);
    }

    /// <inheritdoc />
    public double NextDouble()
    {
        lock (_gate)
            return _inner.NextDouble();
    }

    /// <inheritdoc />
    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(items.Count, 1);
        lock (_gate)
            return items[_inner.Next(items.Count)];
    }
}

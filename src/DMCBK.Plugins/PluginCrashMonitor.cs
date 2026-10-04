namespace DMCBK.PluginSdk;
/// <summary>
/// Counts the exceptions each plugin throws.
/// Past <paramref name="threshold"/> inside <paramref name="window"/> the plugin is over its budget and the host disables it.
/// The window slides, so a plugin that throws once a minute forever is left alone and one that throws ten times in ten seconds is not.
/// </summary>
internal sealed class PluginCrashMonitor(int threshold, TimeSpan window, TimeProvider? time = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How many faults inside the window are tolerated. Zero or less switches the monitor off.</summary>
    public int Threshold { get; } = threshold;

    /// <summary>The sliding window the threshold is counted over.</summary>
    public TimeSpan Window { get; } = window;

    /// <summary>True when the monitor is switched off and no plugin is ever disabled for throwing.</summary>
    public bool Disabled => Threshold <= 0 || Window <= TimeSpan.Zero;

    /// <summary>
    /// Records one fault and answers how many are now inside the window, and whether that is over budget.
    /// </summary>
    public (int Count, bool OverBudget) Record(string id)
    {
        if (Disabled)
            return (0, false);

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_recent.TryGetValue(id, out List<DateTimeOffset>? times))
            {
                times = [];
                _recent[id] = times;
            }

            times.Add(now);
            times.RemoveAll(t => now - t > Window);
            return (times.Count, times.Count >= Threshold);
        }
    }

    /// <summary>Forgets a plugin's history, so a reload starts it on a clean sheet.</summary>
    public void Forget(string id)
    {
        lock (_gate)
            _recent.Remove(id);
    }
}

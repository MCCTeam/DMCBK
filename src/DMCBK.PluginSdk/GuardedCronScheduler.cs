using Umpk.Hosting;

namespace DMCBK.PluginSdk;

/// <summary>
/// Wraps a plugin's cron scheduler so a throwing callback is reported before it is swallowed.
/// UMPK's own scheduler already catches and logs, which keeps the job alive but leaves nobody able to attribute the throw to a plugin, so the catch has to happen on this side of it.
/// </summary>
internal sealed class GuardedCronScheduler(ICronScheduler inner, Action<Exception> onFault) : ICronScheduler
{
    public IDisposable Every(TimeSpan interval, Action callback)
        => inner.Every(interval, Wrap(callback));

    public IDisposable EveryWithJitter(TimeSpan min, TimeSpan max, Action callback)
        => inner.EveryWithJitter(min, max, Wrap(callback));

    public IDisposable DailyAt(TimeOnly timeOfDay, Action callback)
        => inner.DailyAt(timeOfDay, Wrap(callback));

    public IDisposable At(DateTimeOffset when, Action callback)
        => inner.At(when, Wrap(callback));

    private Action Wrap(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return () =>
        {
            try
            {
                callback();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onFault(ex);
            }
        };
    }
}

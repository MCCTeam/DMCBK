namespace DMCBK.Core;

/// <summary>
/// The client is shutting down and the session is still live.
/// Raised once, by <see cref="Client.StopAsync"/>, before the session is closed and before any plugin is deactivated, so a handler can still say goodbye on chat or over a bridge it is about to close.
/// <para>
/// Work that cannot finish inline goes to <see cref="Defer"/>.
/// Everything deferred is awaited together under <see cref="Deadline"/>, and what has not finished by then is abandoned: shutdown continues, and a plugin holding it up cannot keep the process open.
/// </para>
/// </summary>
public sealed class BeforeExitEventArgs : EventArgs
{
    private readonly List<Task> _deferred = [];
    private readonly object _gate = new();

    internal BeforeExitEventArgs(CancellationToken deadline) => Deadline = deadline;

    /// <summary>Cancelled when the shutdown deadline expires. Pass it to anything that can wait.</summary>
    public CancellationToken Deadline { get; }

    /// <summary>Hands the shutdown a task to wait for, up to <see cref="Deadline"/>.</summary>
    public void Defer(Task work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
            _deferred.Add(work);
    }

    /// <summary>The deferred work, as it stands once every handler has run.</summary>
    internal Task[] Deferred()
    {
        lock (_gate)
            return [.. _deferred];
    }
}

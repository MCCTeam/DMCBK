using DMCBK.Core.Commands;

namespace DMCBK.PluginSdk;

/// <summary>
/// A plugin's internal-command registration scope: the ONE command surface a plugin registers through.
/// A command registered here goes onto the same UMPK-backed <see cref="ICommandDispatcher"/> that dispatches real console input, so it actually executes, and it comes back off again (<see cref="ICommandDispatcher.RegisterScopedCommand"/> disposes its own scope) when this scope's lifetime ends.
/// <para>
/// Two scopes exist, differing only in lifetime.
/// <see cref="PluginContext.Commands"/> lasts as long as the plugin is loaded and is torn down on unload/disable/reload; <see cref="ISessionScope.Commands"/> lasts for one live session and is torn down at session end (and on unload, whichever comes first).
/// Register a command that only makes sense while connected on the session scope; register everything else on the plugin scope.
/// </para>
/// </summary>
public interface IPluginCommandScope
{
    /// <summary>
    /// Registers an internal command and returns the handle that unregisters it.
    /// Disposing the handle is optional: the scope disposes every handle it issued when its own lifetime ends.
    /// </summary>
    IDisposable Register(CommandBase command);
}

/// <summary>
/// The default <see cref="IPluginCommandScope"/>: it forwards to <see cref="ICommandDispatcher.RegisterScopedCommand"/> and remembers the handles so the owning lifetime can drop them all at once.
/// </summary>
internal sealed class PluginCommandScope : IPluginCommandScope
{
    private readonly ICommandDispatcher _commands;
    private readonly object _gate = new();
    private readonly List<IDisposable> _handles = [];
    private readonly List<CommandBase> _registered = [];
    private bool _closed;

    internal PluginCommandScope(ICommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    /// <inheritdoc/>
    public IDisposable Register(CommandBase command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            IDisposable handle = _commands.RegisterScopedCommand(command);
            _handles.Add(handle);
            _registered.Add(command);
            return handle;
        }
    }

    /// <summary>
    /// Whether this scope registered <paramref name="command"/>.
    /// The host asks so a command that threw can be counted against the plugin that owns it rather than against the client.
    /// </summary>
    internal bool Owns(CommandBase command)
    {
        lock (_gate)
            return _registered.Any(c => ReferenceEquals(c, command));
    }

    /// <summary>Unregisters every command this scope registered. Idempotent; the scope is closed afterwards.</summary>
    internal void DisposeAll()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            _closed = true;
            handles = [.. _handles];
            _handles.Clear();
            _registered.Clear();
        }

        foreach (IDisposable handle in handles)
            handle.Dispose();
    }
}

using DMCBK.Core.Beacon;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMCBK.PluginSdk;

/// <summary>
/// The per-plugin <see cref="IBeaconHost"/>: validates signatures fast at registration, queues registrations until the client Beacon engine exists (plugins activate before the first <c>/scripts</c> run), flushes on first use, and withdraws everything on unload, disable, and reload like the messenger and service handles.
/// </summary>
internal sealed class PluginBeaconHost : IBeaconHost, IBeaconFunctionRegistry, IBeaconVariableRegistry
{
    private readonly object _gate = new();
    private readonly string _pluginId;
    private readonly DMCBK.Core.Client _client;
    private readonly ILogger _logger;
    private readonly List<BeaconFunction> _pending = [];
    private readonly List<BeaconVariable> _pendingVariables = [];
    private readonly List<PendingEvent> _pendingEvents = [];
    private readonly List<IDisposable> _handles = [];
    private bool _flushed;

    private sealed record PendingEvent(
        string Name,
        IReadOnlyList<string> Fields,
        string Description,
        bool Suppressible,
        string? Capability);

    internal PluginBeaconHost(string pluginId, DMCBK.Core.Client client, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(client);
        _pluginId = pluginId;
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public IBeaconFunctionRegistry Functions => this;

    /// <inheritdoc/>
    public IBeaconVariableRegistry Variables => this;

    /// <inheritdoc/>
    public IDisposable Register(BeaconFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Capability);
        if (function.ParameterTypes is not null || function.ReturnType is not null)
        {
            BeaconFunction.ValidateSignature(
                _pluginId, function.ParameterTypes ?? [], function.ReturnType);
        }

        lock (_gate)
        {
            IBeaconEngine? engine = BeaconEngineWiring.Resolve(_client);
            if (engine is null || !_flushed && HasPending && !TryFlush(engine, out _))
            {
                _pending.Add(function);
                return new PendingHandle(() => RemovePending(function));
            }

            _flushed = true;
            IDisposable handle = RegisterOnBridge(engine.Bridge, function);
            _handles.Add(handle);
            return handle;
        }
    }

    /// <inheritdoc/>
    public IDisposable Register(BeaconVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable.Capability);
        ArgumentNullException.ThrowIfNull(variable.Snapshot);
        lock (_gate)
        {
            IBeaconEngine? engine = BeaconEngineWiring.Resolve(_client);
            if (engine is null || !_flushed && HasPending && !TryFlush(engine, out _))
            {
                _pendingVariables.Add(variable);
                return new PendingHandle(() => RemovePendingVariable(variable));
            }

            _flushed = true;
            IDisposable handle = RegisterVariableOnBridge(engine.Bridge, variable);
            _handles.Add(handle);
            return handle;
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterEvent(
        string name,
        IReadOnlyList<string> fields,
        string description,
        bool suppressible = false,
        string? capability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(description);
        string? trimmedCapability = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var pending = new PendingEvent(name.Trim(), fields.ToList(), description, suppressible, trimmedCapability);
        lock (_gate)
        {
            IBeaconEngine? engine = BeaconEngineWiring.Resolve(_client);
            if (engine is null)
            {
                _pendingEvents.Add(pending);
                return new PendingHandle(() => RemovePendingEvent(pending));
            }

            if (!_flushed && HasPending && !TryFlush(engine, out string? flushRefusal) && flushRefusal is not null)
            {
                _pendingEvents.Add(pending);
                return new PendingHandle(() => RemovePendingEvent(pending));
            }

            EnsureFlushed(engine);
            IDisposable handle = engine.Bridge.RegisterEvent(
                _pluginId, pending.Name, pending.Fields, pending.Description, pending.Suppressible, pending.Capability);
            _handles.Add(handle);
            return handle;
        }
    }

    /// <inheritdoc/>
    public async Task<BeaconFireResult> FireEventAsync(
        string name,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken detached = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);
        IBeaconEngine engine = RequireEngine($"fire Beacon event '{name}'");
        var beaconFields = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach ((string key, object? value) in fields)
        {
            try
            {
                beaconFields[key] = BeaconMarshal.ToBeacon(value);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Plugin '{_pluginId}' cannot fire Beacon event '{name}': {ex.Message}", ex);
            }
        }

        return await engine.Bridge.FireEventAsync(name, beaconFields, detached).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<object?> CallFunctionAsync(
        string scriptId, string function, IReadOnlyList<object?> args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        ArgumentNullException.ThrowIfNull(args);
        IBeaconEngine engine = RequireEngine($"call '{scriptId}.{function}'");
        try
        {
            return await engine.CallExportFromHostAsync(scriptId, function, args, ct).ConfigureAwait(false);
        }
        catch (BeaconRuntimeException ex)
        {
            throw new BeaconCallException(
                scriptId, function, ex.Message, callerLine: 0, inner: ex);
        }
    }

    /// <summary>Queues an attribute-scanned function (same record as explicit registration).</summary>
    internal void AddScanned(BeaconFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        lock (_gate)
        {
            IBeaconEngine? engine = BeaconEngineWiring.Resolve(_client);
            if (engine is null)
            {
                _pending.Add(function);
                return;
            }

            EnsureFlushed(engine);
            _handles.Add(RegisterOnBridge(engine.Bridge, function));
        }
    }

    /// <summary>Withdraws every registration and clears the queue (host teardown).</summary>
    internal void DisposeAll()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            handles = [.. _handles];
            _handles.Clear();
            _pending.Clear();
            _pendingVariables.Clear();
            _pendingEvents.Clear();
            _flushed = false;
        }

        foreach (IDisposable handle in handles)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Beacon withdrawal for plugin '{Id}' threw.", _pluginId);
            }
        }

        IBeaconEngine? engine = null;
        try
        {
            engine = BeaconEngineWiring.Resolve(_client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }

        engine?.Bridge.WithdrawPlugin(_pluginId);
    }

    private IBeaconEngine RequireEngine(string what)
    {
        lock (_gate)
        {
            IBeaconEngine? engine = BeaconEngineWiring.Resolve(_client);
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"Plugin '{_pluginId}' cannot {what}: the Beacon engine is not running yet " +
                    "(it starts with the first /scripts command).");
            }

            EnsureFlushed(engine);
            return engine;
        }
    }

    private void EnsureFlushed(IBeaconEngine engine)
    {
        if (!HasPending)
        {
            _flushed = true;
            return;
        }

        if (!TryFlush(engine, out string? refusal))
            throw new InvalidOperationException(refusal);

        _flushed = true;
    }

    private bool HasPending => _pending.Count > 0 || _pendingVariables.Count > 0 || _pendingEvents.Count > 0;

    private bool TryFlush(IBeaconEngine engine, out string? refusal)
    {
        refusal = null;
        foreach (BeaconFunction function in _pending.ToList())
        {
            try
            {
                _handles.Add(RegisterOnBridge(engine.Bridge, function));
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
                return false;
            }
        }

        _pending.Clear();
        foreach (BeaconVariable variable in _pendingVariables.ToList())
        {
            try
            {
                _handles.Add(RegisterVariableOnBridge(engine.Bridge, variable));
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
                return false;
            }
        }

        _pendingVariables.Clear();
        foreach (PendingEvent pending in _pendingEvents.ToList())
        {
            try
            {
                _handles.Add(RegisterEventOnBridge(engine.Bridge, pending));
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
                return false;
            }
        }

        _pendingEvents.Clear();
        return true;
    }

    private IDisposable RegisterOnBridge(IBeaconBridge bridge, BeaconFunction function)
    {
        var record = new BeaconExtensionFunction(
            function.Name,
            _pluginId,
            function.Capability,
            function.Description,
            function.Parameters,
            call => InvokeAsync(function, call));
        return bridge.RegisterFunction(record);
    }

    private IDisposable RegisterVariableOnBridge(IBeaconBridge bridge, BeaconVariable variable)
    {
        var record = new BeaconVariableRegistration(
            variable.Name,
            _pluginId,
            variable.Capability,
            variable.Description,
            variable.Snapshot);
        return bridge.RegisterVariable(record);
    }

    private IDisposable RegisterEventOnBridge(IBeaconBridge bridge, PendingEvent pending)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(pending);
        return bridge.RegisterEvent(
            _pluginId, pending.Name, pending.Fields, pending.Description, pending.Suppressible, pending.Capability);
    }

    private static async Task<BeaconValue> InvokeAsync(BeaconFunction function, BeaconExtensionCall call)
    {
        var context = new BeaconCallContext(
            function.Name, call.CallerScriptId, call.CallerSpan, call.Args, call.Cancellation);
        object? raw;
        try
        {
            raw = await function.Invoke(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Extern '{function.Name}' from plugin '{call.OwnerPluginId}' failed: {ex.Message}",
                call.CallerSpan.Origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }

        try
        {
            return BeaconMarshal.ToBeacon(raw);
        }
        catch (InvalidOperationException ex)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Extern '{function.Name}' from plugin '{call.OwnerPluginId}' returned a value that cannot cross: {ex.Message}",
                call.CallerSpan.Origin,
                "Return only text, numbers, booleans, lists, string-keyed maps, or null.",
                ex);
        }
    }

    private void RemovePending(BeaconFunction function)
    {
        lock (_gate)
            _pending.Remove(function);
    }

    private void RemovePendingVariable(BeaconVariable variable)
    {
        lock (_gate)
            _pendingVariables.Remove(variable);
    }

    private void RemovePendingEvent(PendingEvent pending)
    {
        lock (_gate)
            _pendingEvents.Remove(pending);
    }

    private sealed class PendingHandle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

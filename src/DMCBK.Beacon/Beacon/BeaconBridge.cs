namespace DMCBK.Core.Beacon;

/// <summary>
/// The per-engine interop hub: C# extension functions, custom events, script exports, and script-registered commands for one <see cref="BeaconEngine"/>.
/// The provider OFFERS behind these live in the static <see cref="BeaconProviders"/> registry (scoped to this bridge, so tooling can resolve them through its explicit environment); the INVOKES live here so one process can host engines with different plugin sets in tests.
/// </summary>
public sealed class BeaconBridge : IBeaconBridge
{
    /// <summary>The extension metadata owned by this bridge.</summary>
    public BeaconEnvironment Environment { get; }

    private readonly object _gate = new();
    private readonly Dictionary<string, BeaconExtensionFunction> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, BeaconHookSchema Schema)> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, BeaconExportedFunction>> _exports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, BeaconExportedValue>> _valueExports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeaconVariableRegistration> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _variableOwners = new(StringComparer.Ordinal);
    private readonly Func<string, IReadOnlyDictionary<string, BeaconValue>, CancellationToken, Task<BeaconFireResult>> _fire;

    /// <summary>Builds a bridge that fires custom events through <paramref name="fire"/>.</summary>
    public BeaconBridge(
        Func<string, IReadOnlyDictionary<string, BeaconValue>, CancellationToken, Task<BeaconFireResult>> fire, BeaconEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(fire);
        _fire = fire;
        Environment = environment ?? BeaconEnvironment.Current;
    }

    /// <summary>Registered extension function names.</summary>
    public IReadOnlyCollection<string> FunctionNames
    {
        get
        {
            lock (_gate)
                return [.. _functions.Keys];
        }
    }

    /// <summary>
    /// Registers an extension function.
    /// A name claimed by a DIFFERENT plugin is a load error naming both (the services refusal model); re-registering your own name replaces it.
    /// Returns the handle that withdraws the registration.
    /// </summary>
    public IDisposable RegisterFunction(BeaconExtensionFunction function)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentNullException.ThrowIfNull(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(function.OwnerPluginId);
        lock (_gate)
        {
            if (_functionOwners.TryGetValue(function.Name, out string? owner)
                && owner is not null
                && !string.Equals(owner, function.OwnerPluginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Beacon function '{function.Name}' is already claimed by plugin '{owner}'; " +
                    $"plugin '{function.OwnerPluginId}' cannot claim it too. Rename one of the two functions.");
            }

            _functions[function.Name] = function;
            _functionOwners[function.Name] = function.OwnerPluginId;
        }

        BeaconProviders.OfferFunction(function.OwnerPluginId, function.Name, function.Capability);
        return new Withdrawal(() => WithdrawFunction(function.OwnerPluginId, function.Name));
    }

    /// <summary>Looks up an extension function by name.</summary>
    public bool TryGetFunction(string name, out BeaconExtensionFunction? function)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
            return _functions.TryGetValue(name, out function);
    }

    private void WithdrawFunction(string owner, string name)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (_functionOwners.TryGetValue(name, out string? current)
                && string.Equals(current, owner, StringComparison.Ordinal))
            {
                _functions.Remove(name);
                _functionOwners.Remove(name);
            }
        }
    }

    /// <summary>
    /// Registers a custom event with its field table (also registered as a custom hook so <c>on name</c> validates clean).
    /// A name claimed by a different plugin is a load error naming both.
    /// Returns the handle that withdraws the registration.
    /// </summary>
    public IDisposable RegisterEvent(
        string owner,
        string name,
        IReadOnlyList<string> fields,
        string description,
        bool suppressible = false,
        string? capability = null)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(description);
        string? trimmedCapability = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        BeaconHookSchema schema = new(
            name.Trim(),
            description,
            $"Plugin '{owner}'.",
            Suppressible: suppressible,
            Fields: fields.Select(f => new BeaconHookField(f, "value", $"The '{f}' field.")).ToList());
        lock (_gate)
        {
            if (_events.TryGetValue(name, out (string Owner, BeaconHookSchema Schema) existing)
                && !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Beacon event '{name}' is already claimed by plugin '{existing.Owner}'; " +
                    $"plugin '{owner}' cannot claim it too. Rename one of the two events.");
            }

            _events[name] = (owner, schema);
        }

        BeaconHookCatalog.RegisterCustomHook(schema);
        BeaconProviders.OfferEvent(owner, name, trimmedCapability);
        return new Withdrawal(() => WithdrawEvent(owner, name));
    }

    /// <summary>Registered custom event names with their owning plugin.</summary>
    public IReadOnlyDictionary<string, string> CustomEvents
    {
        get
        {
            lock (_gate)
                return _events.ToDictionary(kv => kv.Key, kv => kv.Value.Owner, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WithdrawEvent(string owner, string name)
    {
        using IDisposable environmentScope = Environment.Enter();
        bool owned;
        lock (_gate)
        {
            owned = _events.TryGetValue(name, out (string Owner, BeaconHookSchema Schema) existing)
                && string.Equals(existing.Owner, owner, StringComparison.Ordinal);
            if (owned)
                _events.Remove(name);
        }

        if (owned)
            BeaconHookCatalog.UnregisterCustomHook(name);
    }

    /// <summary>
    /// Fires a plugin-registered event to every matching <c>on</c> block.
    /// Honors <paramref name="detached"/>: a fired token skips dispatch (nothing fires into a dead session).
    /// </summary>
    public Task<BeaconFireResult> FireEventAsync(
        string name,
        IReadOnlyDictionary<string, BeaconValue> fields,
        CancellationToken detached = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);
        if (detached.IsCancellationRequested)
            return Task.FromResult(new BeaconFireResult(name, false, [], [], [], []));

        return _fire(name, fields, detached);
    }

    /// <summary>Publishes one script export (later loads of the same script replace it).</summary>
    public void PublishExport(BeaconExportedFunction export)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentNullException.ThrowIfNull(export);
        lock (_gate)
        {
            if (!_exports.TryGetValue(export.ScriptId, out Dictionary<string, BeaconExportedFunction>? table))
            {
                table = new Dictionary<string, BeaconExportedFunction>(StringComparer.Ordinal);
                _exports[export.ScriptId] = table;
            }

            table[export.Name] = export;
        }
    }

    /// <summary>Looks up an export by script id and function name.</summary>
    public bool TryGetExport(string scriptId, string name, out BeaconExportedFunction? export)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_exports.TryGetValue(scriptId, out Dictionary<string, BeaconExportedFunction>? table)
                && table.TryGetValue(name, out BeaconExportedFunction? found))
            {
                export = found;
                return true;
            }
        }

        export = null;
        return false;
    }

    /// <summary>Withdraws every export of <paramref name="scriptId"/> (unload, reload).</summary>
    public void WithdrawScript(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            _exports.Remove(scriptId);
            _valueExports.Remove(scriptId);
        }
    }

    /// <summary>Publishes one script value export (later loads of the same script replace it).</summary>
    public void PublishValueExport(BeaconExportedValue export)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentNullException.ThrowIfNull(export);
        lock (_gate)
        {
            if (!_valueExports.TryGetValue(export.ScriptId, out Dictionary<string, BeaconExportedValue>? table))
            {
                table = new Dictionary<string, BeaconExportedValue>(StringComparer.Ordinal);
                _valueExports[export.ScriptId] = table;
            }

            table[export.Name] = export;
        }
    }

    /// <summary>Looks up a value export by script id and value name.</summary>
    public bool TryGetValueExport(string scriptId, string name, out BeaconExportedValue? export)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_valueExports.TryGetValue(scriptId, out Dictionary<string, BeaconExportedValue>? table)
                && table.TryGetValue(name, out BeaconExportedValue? found))
            {
                export = found;
                return true;
            }
        }

        export = null;
        return false;
    }

    /// <summary>
    /// Registers a plugin variable namespace.
    /// A name claimed by a DIFFERENT plugin is a load error naming both (the function-registry refusal model); re-registering your own name replaces it.
    /// Returns the handle that withdraws the registration.
    /// </summary>
    public IDisposable RegisterVariable(BeaconVariableRegistration variable)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable.OwnerPluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable.Capability);
        lock (_gate)
        {
            if (_variableOwners.TryGetValue(variable.Name, out string? owner)
                && owner is not null
                && !string.Equals(owner, variable.OwnerPluginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Beacon variable '{variable.Name}' is already claimed by plugin '{owner}'; " +
                    $"plugin '{variable.OwnerPluginId}' cannot claim it too. Rename one of the two namespaces.");
            }

            _variables[variable.Name] = variable;
            _variableOwners[variable.Name] = variable.OwnerPluginId;
        }

        BeaconProviders.OfferVariable(variable.OwnerPluginId, variable.Name, variable.Capability);
        return new Withdrawal(() => WithdrawVariable(variable.OwnerPluginId, variable.Name));
    }

    /// <summary>Looks up a plugin variable namespace by name.</summary>
    public bool TryGetVariable(string name, out BeaconVariableRegistration? variable)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
            return _variables.TryGetValue(name, out variable);
    }

    /// <summary>Registered plugin variable namespaces.</summary>
    public IReadOnlyCollection<string> VariableNames
    {
        get
        {
            lock (_gate)
                return [.. _variables.Keys];
        }
    }

    private void WithdrawVariable(string owner, string name)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (_variableOwners.TryGetValue(name, out string? current)
                && string.Equals(current, owner, StringComparison.Ordinal))
            {
                _variables.Remove(name);
                _variableOwners.Remove(name);
            }
        }
    }

    /// <summary>
    /// Withdraws every function, event, and variable namespace owned by <paramref name="owner"/> (unload, disable, reload), mirroring the service and messenger handle withdrawal.
    /// </summary>
    public void WithdrawPlugin(string owner)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        List<string> functions;
        List<string> events;
        List<string> variables;
        lock (_gate)
        {
            functions = _functionOwners
                .Where(kv => string.Equals(kv.Value, owner, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
            foreach (string name in functions)
            {
                _functions.Remove(name);
                _functionOwners.Remove(name);
            }

            events = _events
                .Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
            foreach (string name in events)
                _events.Remove(name);

            variables = _variableOwners
                .Where(kv => string.Equals(kv.Value, owner, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
            foreach (string name in variables)
            {
                _variables.Remove(name);
                _variableOwners.Remove(name);
            }
        }

        foreach (string name in events)
            BeaconHookCatalog.UnregisterCustomHook(name);

        BeaconProviders.WithdrawPlugin(owner);
    }

    private sealed class Withdrawal(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// The scoped provider registry behind the bridge: capability, function, and event OFFERS, available to offline tooling only through its explicit environment.
/// Invokes live on <see cref="BeaconBridge"/>; offers live here.
/// Tests reset via <see cref="Clear"/>; production withdraws on unload, disable, and reload.
/// </summary>
public static class BeaconProviders
{
    private static BeaconProviderRegistry Inner => BeaconEnvironment.Current.Providers;

    /// <summary>Records that <paramref name="pluginId"/> offers <paramref name="capability"/>.</summary>
    public static void OfferCapability(string pluginId, string capability) => Inner.OfferCapability(pluginId, capability);

    /// <summary>Records that <paramref name="pluginId"/> offers extension function <paramref name="name"/>.</summary>
    public static void OfferFunction(string pluginId, string name, string capability) => Inner.OfferFunction(pluginId, name, capability);

    /// <summary>Records that <paramref name="pluginId"/> offers custom event <paramref name="name"/>.</summary>
    public static void OfferEvent(string pluginId, string name, string? capability = null) => Inner.OfferEvent(pluginId, name, capability);

    /// <summary>Records that <paramref name="pluginId"/> offers variable namespace <paramref name="name"/>.</summary>
    public static void OfferVariable(string pluginId, string name, string capability) => Inner.OfferVariable(pluginId, name, capability);

    /// <summary>Withdraws every offer from <paramref name="pluginId"/>.</summary>
    public static void WithdrawPlugin(string pluginId) => Inner.WithdrawPlugin(pluginId);

    /// <summary>True when some loaded plugin offers <paramref name="capability"/>.</summary>
    public static bool HasCapability(string capability) => Inner.HasCapability(capability);

    /// <summary>Plugin ids offering <paramref name="capability"/> (empty when none).</summary>
    public static IReadOnlyList<string> ProvidersOf(string capability) => Inner.ProvidersOf(capability);

    /// <summary>True when a loaded plugin offers extension function <paramref name="name"/>.</summary>
    public static bool TryGetFunction(string name, out string pluginId, out string capability)
        => Inner.TryGetFunction(name, out pluginId, out capability);

    /// <summary>True when a loaded plugin offers custom event <paramref name="name"/>.</summary>
    public static bool TryGetEvent(string name, out string pluginId) => Inner.TryGetEvent(name, out pluginId);

    /// <summary>True when a loaded plugin offers custom event <paramref name="name"/>, with its capability.</summary>
    public static bool TryGetEvent(string name, out string pluginId, out string? capability)
        => Inner.TryGetEvent(name, out pluginId, out capability);

    /// <summary>True when a loaded plugin offers variable namespace <paramref name="name"/>.</summary>
    public static bool TryGetVariable(string name, out string pluginId, out string capability)
        => Inner.TryGetVariable(name, out pluginId, out capability);

    /// <summary>Clears every offer (tests only; RAM-only like throttle windows).</summary>
    public static void Clear()
    {
        foreach (string plugin in Inner.OfferingPlugins)
            Inner.WithdrawPlugin(plugin);
    }
}

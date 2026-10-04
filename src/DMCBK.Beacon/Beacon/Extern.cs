namespace DMCBK.Core.Beacon;


/// <summary>
/// The provider registry: which plugin provides which capability, extension function, and custom event.
/// Populated by bridge registrations (the C# side) and read by lint plus the manifest enforcer.
/// Warn-by-default is intentional: an unresolvable <c>extern</c>/<c>call</c> or a missing capability provider WARNS (B1006/B1002) and loads.
/// It refuses only under <c>--strict</c>.
/// This keeps script loading independent of plugin load order.
/// </summary>
public sealed class BeaconProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _capabilityToPlugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, string Capability)> _functionToPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, string? Capability)> _eventToPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, string Capability)> _variableToPlugin = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="pluginId"/> offers <paramref name="capability"/>.</summary>
    public void OfferCapability(string pluginId, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        lock (_gate)
        {
            if (!_capabilityToPlugins.TryGetValue(capability, out HashSet<string>? owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                _capabilityToPlugins[capability] = owners;
            }

            owners.Add(pluginId);
        }
    }

    /// <summary>Records that <paramref name="pluginId"/> offers extension function <paramref name="name"/>.</summary>
    public void OfferFunction(string pluginId, string name, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        lock (_gate)
        {
            _functionToPlugin[name] = (pluginId, capability);
            OfferCapability(pluginId, capability);
        }
    }

    /// <summary>Records that <paramref name="pluginId"/> offers variable namespace <paramref name="name"/>.</summary>
    public void OfferVariable(string pluginId, string name, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        lock (_gate)
        {
            _variableToPlugin[name] = (pluginId, capability);
            OfferCapability(pluginId, capability);
        }
    }

    /// <summary>Records that <paramref name="pluginId"/> offers custom event <paramref name="name"/>.</summary>
    public void OfferEvent(string pluginId, string name, string? capability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string? trimmedCapability = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        lock (_gate)
        {
            _eventToPlugin[name] = (pluginId, trimmedCapability);
            if (trimmedCapability is not null)
            {
                if (!_capabilityToPlugins.TryGetValue(trimmedCapability, out HashSet<string>? owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    _capabilityToPlugins[trimmedCapability] = owners;
                }

                owners.Add(pluginId);
            }
        }
    }

    /// <summary>Withdraws every offer from <paramref name="pluginId"/> (unload, disable, reload).</summary>
    public void WithdrawPlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            foreach ((string capability, HashSet<string> owners) in _capabilityToPlugins)
                owners.Remove(pluginId);

            foreach (string name in _functionToPlugin.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
                _functionToPlugin.Remove(name);

            foreach (string name in _variableToPlugin.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
                _variableToPlugin.Remove(name);

            foreach (string name in _eventToPlugin.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
                _eventToPlugin.Remove(name);
        }
    }

    /// <summary>True when some loaded plugin offers <paramref name="capability"/>.</summary>
    public bool HasCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        lock (_gate)
        {
            return _capabilityToPlugins.TryGetValue(capability, out HashSet<string>? owners) && owners.Count > 0;
        }
    }

    /// <summary>Plugin ids offering <paramref name="capability"/> (empty when none).</summary>
    public IReadOnlyList<string> ProvidersOf(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        lock (_gate)
        {
            return _capabilityToPlugins.TryGetValue(capability, out HashSet<string>? owners)
                ? owners.OrderBy(p => p, StringComparer.Ordinal).ToList()
                : [];
        }
    }

    /// <summary>True when a loaded plugin offers extension function <paramref name="name"/>.</summary>
    public bool TryGetFunction(string name, out string pluginId, out string capability)
        => TryGetOffer(_functionToPlugin, name, out pluginId, out capability);

    /// <summary>True when a loaded plugin offers custom event <paramref name="name"/>.</summary>
    public bool TryGetEvent(string name, out string pluginId)
    {
        bool found = TryGetEvent(name, out pluginId, out _);
        return found;
    }

    /// <summary>True when a loaded plugin offers custom event <paramref name="name"/>, with its capability.</summary>
    public bool TryGetEvent(string name, out string pluginId, out string? capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_eventToPlugin.TryGetValue(name, out (string PluginId, string? Capability) found))
            {
                pluginId = found.PluginId;
                capability = found.Capability;
                return true;
            }
        }

        pluginId = string.Empty;
        capability = null;
        return false;
    }

    /// <summary>True when a loaded plugin offers variable namespace <paramref name="name"/>.</summary>
    public bool TryGetVariable(string name, out string pluginId, out string capability)
        => TryGetOffer(_variableToPlugin, name, out pluginId, out capability);

    private bool TryGetOffer(
        Dictionary<string, (string PluginId, string Capability)> table,
        string name, out string pluginId, out string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            if (table.TryGetValue(name, out (string PluginId, string Capability) found))
            {
                pluginId = found.PluginId;
                capability = found.Capability;
                return true;
            }
        }

        pluginId = string.Empty;
        capability = string.Empty;
        return false;
    }

    /// <summary>Every plugin id with at least one outstanding offer (for test resets).</summary>
    public IReadOnlyList<string> OfferingPlugins
    {
        get
        {
            lock (_gate)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (HashSet<string> owners in _capabilityToPlugins.Values)
                    seen.UnionWith(owners);

                foreach ((string PluginId, string? Capability) owner in _eventToPlugin.Values)
                    seen.Add(owner.PluginId);

                foreach (KeyValuePair<string, (string PluginId, string Capability)> entry in _functionToPlugin)
                    seen.Add(entry.Value.PluginId);

                return seen.OrderBy(p => p, StringComparer.Ordinal).ToList();
            }
        }
    }
}

/// <summary>
/// Script-side <c>extern</c> bookkeeping: which names a script declared and from which plugin.
/// One instance per loaded script, built from <see cref="BeaconScript.Externs"/> at load.
/// </summary>
public sealed class BeaconExternTable
{
    private readonly Dictionary<string, string> _nameToPlugin = new(StringComparer.Ordinal);

    /// <summary>Builds the table from a script prologue (later entries win on duplicates).</summary>
    public BeaconExternTable(BeaconScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        foreach (BeaconExtern ext in script.Externs)
            _nameToPlugin[ext.Name] = ext.PluginId;
    }

    /// <summary>True when the script declares <paramref name="name"/> via <c>extern</c>.</summary>
    public bool Declares(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _nameToPlugin.ContainsKey(name);
    }

    /// <summary>The plugin id the script named for <paramref name="name"/>, or null when undeclared.</summary>
    public string? PluginFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _nameToPlugin.TryGetValue(name, out string? plugin) ? plugin : null;
    }

    /// <summary>Declared extern names.</summary>
    public IReadOnlyCollection<string> Names => _nameToPlugin.Keys;
}

namespace DMCBK.Core.Beacon;

/// <summary>
/// Lexical scope chain for Beacon execution.
/// </summary>
/// <remarks>
/// <para>
/// FROZEN evaluation API for the scheduler, event, and state runtimes (scope lookup order).
/// Do not reorder without a wave-sync review, because event filters, handler snapshots, and saved/shared bridging all program against it:
/// </para>
/// <para>
/// Read order for a bare name <c>x</c>:
/// 1. Function call-frame locals and parameters, innermost frame first.
/// 2. Handler event fields (the <c>on</c> payload plus the <c>event</c> map). Event fields
/// shadow globals inside handlers; the full <c>event.field</c> form always works.
/// 3. Per-script globals (one root <see cref="BeaconScope"/> per script id; never shared
/// across scripts).
/// 4. Read-only builtins view (converters, helpers, and the <c>me</c>/<c>server</c>/<c>time</c>
/// namespaces).
/// Assigning to a builtin name is a <c>B2002</c> error, never a shadow.
/// 5. Bare-shortcut twins: <c>health</c> for <c>me.health</c>, <c>max_health</c>, <c>food</c>,
/// <c>saturation</c>, <c>air</c>, <c>xp_level</c>, <c>online_count</c> for <c>server.online_count</c>, <c>tps</c> for <c>server.tps</c>.
/// There is deliberately no bare <c>hunger</c>; food is <c>food</c> everywhere and <c>hunger</c> suggests <c>food</c>.
/// </para>
/// <para>
/// Write order for <c>set x to ...</c> (bare target): new names define in the current scope; existing names update the defining scope, except across barriers.
/// A function frame is a barrier (locals shadow globals: <c>set x</c> inside a function never leaks); a handler event scope is a barrier for its own field names only (event snapshots stay immutable, so <c>set player</c> inside a handler shadows instead of mutating the event).
/// A <c>set a.b to ...</c> / <c>set a[i] to ...</c> target resolves its base through the read order above and mutates the defining scope's value with copy-on-write, so <c>set quiz.running to yes</c> inside a handler updates the global map entry instead of creating a handler-local.
/// </para>
/// </remarks>
public sealed class BeaconScope
{
    private readonly Dictionary<string, BeaconValue> _values = new(StringComparer.Ordinal);

    /// <summary>Builds a scope with an optional parent (globals have none).</summary>
    public BeaconScope(BeaconScope? parent = null)
    {
        Parent = parent;
    }

    /// <summary>Parent scope; null for per-script globals roots.</summary>
    public BeaconScope? Parent { get; }

    /// <summary>True for function call frames (locals shadow globals).</summary>
    public bool IsFunctionBoundary { get; set; }

    /// <summary>True for handler event scopes (field names are immutable snapshots).</summary>
    public bool IsEventBoundary { get; set; }

    /// <summary>Defines or overwrites <paramref name="name"/> in this scope only.</summary>
    public void Define(string name, BeaconValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
    }

    /// <summary>Reads <paramref name="name"/> walking outward; false when nowhere defined.</summary>
    public bool TryLookup(string name, out BeaconValue? value)
        => TryLookupWithOwner(name, out value, out _);

    /// <summary>
    /// Reads <paramref name="name"/> walking outward and reports the owning scope, so member/index assignment can copy-on-write back into the right place.
    /// </summary>
    public bool TryLookupWithOwner(string name, out BeaconValue? value, out BeaconScope? owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        for (BeaconScope? current = this; current is not null; current = current.Parent)
        {
            if (current._values.TryGetValue(name, out BeaconValue? found) && found is not null)
            {
                value = found;
                owner = current;
                return true;
            }
        }

        value = null;
        owner = null;
        return false;
    }

    /// <summary>True when <paramref name="name"/> is defined in this scope only (no walk).</summary>
    public bool IsDefinedLocally(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _values.ContainsKey(name);
    }

    /// <summary>Creates a child scope (call frames, loop bodies, handler scratch, catch).</summary>
    public BeaconScope CreateChild() => new(this);

    /// <summary>Snapshot of this scope's own entries (for timeout dumps and tests).</summary>
    public IReadOnlyDictionary<string, BeaconValue> Snapshot()
        => new Dictionary<string, BeaconValue>(_values, StringComparer.Ordinal);

    /// <summary>Local names in this scope only (for did-you-mean candidates).</summary>
    public IEnumerable<string> LocalNames => _values.Keys;

    /// <summary>All visible names from here outward (for did-you-mean candidates).</summary>
    public IEnumerable<string> VisibleNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (BeaconScope? current = this; current is not null; current = current.Parent)
        {
            foreach (string name in current._values.Keys)
            {
                if (seen.Add(name))
                    yield return name;
            }
        }
    }
}

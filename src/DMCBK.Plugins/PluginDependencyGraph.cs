namespace DMCBK.PluginSdk;

/// <summary>
/// The plugin-to-plugin dependency graph, built from every discovered manifest's <c>[requires]</c> and <c>[optional]</c> tables.
/// It answers two questions: what order may these plugins load in, and which of them sit in a cycle.
/// <para>
/// Ordering is a depth-first post-order over ids sorted ordinally, so a dependency always precedes its dependents and the order is stable from run to run even though directory enumeration is not.
/// Compatible optional edges order providers first when doing so cannot create a cycle.
/// The difference between the two tables is elsewhere: a missing optional plugin is no edge at all, while a missing required one is refused by the loader.
/// </para>
/// </summary>
internal static class PluginDependencyGraph
{
    /// <summary>The order to load in, and who sits in a cycle.</summary>
    /// <param name="Order">
    /// Every id, dependencies before dependents.
    /// Cycle members are included, so the order stays a complete list; the loader refuses them by consulting <paramref name="Cycles"/>.
    /// </param>
    /// <param name="Cycles">
    /// Maps every id in a cycle to that cycle's members, in the order the walk found them, so a refusal can name the whole ring rather than one arbitrary member.
    /// </param>
    internal readonly record struct Result(
        IReadOnlyList<string> Order,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Cycles);

    /// <summary>Builds the graph over a set of manifests. Ids not in the set are simply not edges.</summary>
    internal static Result Build(IEnumerable<PluginManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var byId = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginManifest manifest in manifests)
            byId[manifest.Id] = manifest;

        var edges = byId.ToDictionary(pair => pair.Key,
            pair => pair.Value.RequiredRanges.Keys.Where(byId.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach ((string consumer, PluginManifest manifest) in byId.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            foreach ((string provider, SemVerRange range) in manifest.OptionalRanges.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                if (byId.TryGetValue(provider, out PluginManifest? offered)
                    && SemVer.TryParse(offered.Version, out SemVer version) && range.Satisfies(version)
                    && !Reaches(provider, consumer, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    edges[consumer].Add(provider);

        bool Reaches(string id, string target, HashSet<string> visited)
            => id.Equals(target, StringComparison.OrdinalIgnoreCase)
                || (visited.Add(id) && edges[id].Any(next => Reaches(next, target, visited)));

        var order = new List<string>(byId.Count);
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        var roots = new List<string>(byId.Keys);
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string id in roots)
            Visit(id, edges, state, path, order, cycles);

        return new Result(order, cycles);
    }

    /// <summary>
    /// Every id in <paramref name="dependents"/> that transitively depends on <paramref name="id"/>, with a plugin before anything it depends on.
    /// That is the order they have to be unloaded in, and what the refusal prints as a chained command.
    /// </summary>
    internal static IReadOnlyList<string> DependentsOf(
        string id, IEnumerable<PluginManifest> dependents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(dependents);

        var manifests = new List<PluginManifest>(dependents);
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (PluginManifest manifest in manifests)
            {
                if (reached.Contains(manifest.Id))
                    continue;

                foreach (string required in manifest.RequiredRanges.Keys)
                {
                    if (reached.Contains(required))
                    {
                        reached.Add(manifest.Id);
                        grew = true;
                        break;
                    }
                }
            }
        }

        reached.Remove(id);
        if (reached.Count == 0)
            return [];

        // Dependencies-first order, reversed: a dependent has to go before the plugin it depends on.
        IReadOnlyList<string> order = Build(manifests).Order;
        var result = new List<string>(reached.Count);
        for (int i = order.Count - 1; i >= 0; i--)
        {
            if (reached.Contains(order[i]))
                result.Add(order[i]);
        }

        return result;
    }

    private static void Visit(
        string id,
        Dictionary<string, HashSet<string>> edges,
        Dictionary<string, VisitState> state,
        List<string> path,
        List<string> order,
        Dictionary<string, IReadOnlyList<string>> cycles)
    {
        if (state.TryGetValue(id, out VisitState visited))
        {
            if (visited == VisitState.Visiting)
                RecordCycle(id, path, cycles);

            return;
        }

        state[id] = VisitState.Visiting;
        path.Add(id);

        foreach (string dependency in edges[id].Order(StringComparer.OrdinalIgnoreCase))
            Visit(dependency, edges, state, path, order, cycles);

        path.RemoveAt(path.Count - 1);
        state[id] = VisitState.Done;
        order.Add(id);
    }

    /// <summary>
    /// Records the ring the back edge just closed, from the node we came back to through the one that closed it.
    /// Every member gets the same list, so whichever the loader refuses first can print the whole cycle.
    /// </summary>
    private static void RecordCycle(
        string id, List<string> path, Dictionary<string, IReadOnlyList<string>> cycles)
    {
        int start = path.FindIndex(entry => string.Equals(entry, id, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return;

        string[] members = [.. path[start..]];
        foreach (string member in members)
            cycles[member] = members;
    }

    private enum VisitState
    {
        Visiting,
        Done,
    }
}

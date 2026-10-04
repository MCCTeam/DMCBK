using DMCBK.PluginSdk;

namespace DMCBK.Marketplace;

/// <summary>A catalogue explicitly bound to a marketplace identity.</summary>
/// <param name="MarketplaceId">The publisher binding.</param>
/// <param name="Catalogue">The immutable release history.</param>
public sealed record CatalogueSource(string MarketplaceId, ReleaseCatalogue Catalogue);

/// <summary>An installed version and its explicit publisher binding.</summary>
/// <param name="Id">The plugin identifier.</param>
/// <param name="MarketplaceId">The bound publisher.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Pinned">Whether resolution must preserve its version.</param>
/// <param name="Asset">The previously authorized selected asset, if known.</param>
public sealed record InstalledVersion(string Id, string MarketplaceId, string Version, bool Pinned = false, ReleaseAsset? Asset = null);

/// <summary>A requested graph change.</summary>
/// <param name="Id">The plugin to install or update.</param>
/// <param name="MarketplaceId">An explicit publisher selection, or the installed binding.</param>
/// <param name="Range">The permitted release range.</param>
/// <param name="ExactVersion">An exact version that must never be substituted.</param>
/// <param name="Update">Whether to prefer newer versions for this requested plugin.</param>
/// <param name="IncludePrerelease">Whether prerelease candidates are enabled.</param>
/// <param name="Assets">Source preference and fallback policy.</param>
/// <param name="Pin">An explicit pin change; null preserves current pin policy.</param>
public sealed record ResolutionRequest(string Id, string? MarketplaceId = null, string Range = "*",
    string? ExactVersion = null, bool Update = false, bool IncludePrerelease = false, AssetPolicy? Assets = null, bool? Pin = null);

/// <summary>One selected version and the single payload to download.</summary>
/// <param name="Id">The plugin identity.</param>
/// <param name="MarketplaceId">The selected publisher.</param>
/// <param name="Release">The selected release metadata.</param>
/// <param name="Asset">The selected payload.</param>
/// <param name="Reason">A stable explanation identifier.</param>
public sealed record ResolvedPlugin(string Id, string MarketplaceId, PluginRelease Release, ReleaseAsset Asset, string Reason);

/// <summary>A compatible whole graph in dependency-first activation order.</summary>
/// <param name="Plugins">The selected graph.</param>
/// <param name="UnavailableOptionalDependencies">Incompatible or absent optional providers, qualified by consumer.</param>
public sealed record DependencyResolution(IReadOnlyList<ResolvedPlugin> Plugins,
    IReadOnlyList<string> UnavailableOptionalDependencies);

/// <summary>Backtracks over the complete installed graph before any installation state changes.</summary>
public sealed class DependencyResolver
{
    private readonly HostInfo _host;
    private readonly Dictionary<(string Marketplace, string Plugin), PluginRelease[]> _catalogues;

    /// <summary>Creates a resolver over a validated catalogue snapshot.</summary>
    public DependencyResolver(HostInfo host, IEnumerable<CatalogueSource> sources)
    {
        _host = host;
        _catalogues = [];
        foreach (CatalogueSource source in sources)
        {
            if (!PluginManifest.IsUsableId(source.MarketplaceId) || source.Catalogue.SchemaVersion != 2
                || !PluginManifest.IsUsableId(source.Catalogue.Id))
                throw new MarketplaceException("resolution.source-invalid", source.MarketplaceId, source.Catalogue.Id);
            foreach (PluginRelease release in source.Catalogue.Releases) release.Validate(source.Catalogue.Id);
            if (!_catalogues.TryAdd((Key(source.MarketplaceId), Key(source.Catalogue.Id)),
                    source.Catalogue.Releases.OrderByDescending(release => SemVer.Parse(release.Version)).ToArray()))
                throw new MarketplaceException("resolution.source-duplicate", source.MarketplaceId, source.Catalogue.Id);
        }
    }

    /// <summary>Resolves the request while validating other installed dependents, source bindings and pins.</summary>
    public DependencyResolution Resolve(ResolutionRequest request, IEnumerable<InstalledVersion>? installed = null)
    {
        if (!PluginManifest.IsUsableId(request.Id)) throw new MarketplaceException("resolution.id-invalid", request.Id);
        string root = Key(request.Id);
        Dictionary<string, InstalledVersion> current = (installed ?? []).ToDictionary(item => Key(item.Id));
        string marketplace = request.MarketplaceId is { } selected ? Key(selected)
            : current.TryGetValue(root, out InstalledVersion? existing) ? Key(existing.MarketplaceId)
            : SelectPublisher(root);
        var requirements = current.ToDictionary(pair => pair.Key,
            pair => new Requirement(Key(pair.Value.MarketplaceId), [pair.Value.Pinned && !(pair.Key == root && request.Pin is not null) ? "=" + pair.Value.Version : "*"]));
        string requestedRange = request.ExactVersion is { } exact ? "=" + SemVer.Parse(exact) : request.Range;
        _ = SemVerRange.Parse(requestedRange);
        if (requirements.TryGetValue(root, out Requirement? prior) && current[root].Pinned && request.Pin is null)
            requirements[root] = new(marketplace, [.. prior.Ranges, requestedRange]);
        else requirements[root] = new(marketplace, [requestedRange]);
        var refusals = new List<string>();
        Dictionary<string, ResolvedPlugin>? resolved = Search([], requirements);
        if (resolved is null)
            throw new MarketplaceException("resolution.unsatisfied", root, string.Join(";", refusals.Distinct().Take(30)));
        IReadOnlyList<string> order = ActivationOrder(resolved);
        string[] unavailable = resolved.SelectMany(pair => pair.Value.Release.Optional.Where(optional =>
                !resolved.TryGetValue(Key(optional.Key), out ResolvedPlugin? provider)
                || !SemVerRange.Parse(optional.Value).Satisfies(SemVer.Parse(provider.Release.Version)))
            .Select(optional => pair.Key + ":" + optional.Key)).Order(StringComparer.Ordinal).ToArray();
        return new(order.Select(id => resolved[id]).ToArray(), unavailable);

        Dictionary<string, ResolvedPlugin>? Search(Dictionary<string, ResolvedPlugin> chosen, Dictionary<string, Requirement> constraints)
        {
            foreach ((string id, ResolvedPlugin plugin) in chosen)
                if (!Meets(plugin.Release, constraints[id])) return null;
            string? next = constraints.Keys.Where(id => !chosen.ContainsKey(id)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (next is null) return HasRequiredCycle(chosen) ? null : chosen;
            Requirement requirement = constraints[next];
            if (!_catalogues.TryGetValue((requirement.Marketplace, next), out PluginRelease[]? releases))
            { refusals.Add(next + ":missing-provider"); return null; }
            IEnumerable<PluginRelease> candidates = releases.Where(release => Meets(release, requirement));
            if (current.TryGetValue(next, out InstalledVersion? present) && !(next == root && request.Update))
                candidates = candidates.OrderByDescending(release => release.Version == present.Version);
            foreach (PluginRelease release in candidates)
            {
                bool retained = present?.Version == release.Version;
                if (release.Yanked && !retained) continue;
                SemVer version = SemVer.Parse(release.Version);
                if (!retained && version.IsPrerelease && !request.IncludePrerelease
                    && !requirement.Ranges.Any(range => SemVerRange.Parse(range).Satisfies(version))) continue;
                IReadOnlyList<MarketplaceException> gates = ReleaseCompatibility.Check(release, _host);
                if (gates.Count > 0) { refusals.Add(next + ":" + string.Join(',', gates.Select(gate => gate.Code))); continue; }
                ReleaseAsset asset;
                try
                {
                    asset = retained && present?.Asset is { } priorAsset
                        && !(next == root && (request.Update || request.Assets is not null))
                        ? release.Assets.FirstOrDefault(candidate => InstallationLock.SameAsset(candidate, priorAsset))
                            ?? throw new MarketplaceException("resolution.installed-asset-missing", next)
                        : AssetSelector.Select(release, _host.RuntimeTarget, request.Assets);
                }
                catch (MarketplaceException error) { refusals.Add(next + ":" + error.Code); continue; }
                var branch = new Dictionary<string, ResolvedPlugin>(chosen)
                { [next] = new(next, requirement.Marketplace, release, asset, retained ? "resolution.preserve-installed" : "resolution.compatible-candidate") };
                var added = constraints.ToDictionary(pair => pair.Key, pair => new Requirement(pair.Value.Marketplace, [.. pair.Value.Ranges]));
                bool conflict = false;
                foreach ((string dependency, string range) in release.Requires.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    string id = Key(dependency);
                    string source = current.TryGetValue(id, out InstalledVersion? binding) ? Key(binding.MarketplaceId) : requirement.Marketplace;
                    if (added.TryGetValue(id, out Requirement? known))
                    {
                        if (known.Marketplace != source) { conflict = true; refusals.Add(id + ":publisher-conflict"); break; }
                        known.Ranges.Add(range);
                    }
                    else added[id] = new(source, [range]);
                }
                if (conflict || HasRequiredCycle(branch)) continue;
                Dictionary<string, ResolvedPlugin>? result = Search(branch, added);
                if (result is not null) return result;
            }
            refusals.Add(next + ":" + string.Join('&', requirement.Ranges));
            return null;
        }

        bool Meets(PluginRelease release, Requirement requirement)
            => requirement.Ranges.All(range => SemVerRange.Parse(range).Satisfies(SemVer.Parse(release.Version), includePrerelease: true));
    }

    private string SelectPublisher(string id)
    {
        string[] sources = _catalogues.Keys.Where(key => key.Plugin == id).Select(key => key.Marketplace).Distinct().ToArray();
        return sources.Length == 1 ? sources[0] : throw new MarketplaceException(
            sources.Length == 0 ? "resolution.missing-plugin" : "resolution.publisher-selection-required", id);
    }

    private static bool HasRequiredCycle(Dictionary<string, ResolvedPlugin> chosen)
    {
        var active = new HashSet<string>(); var done = new HashSet<string>();
        return chosen.Keys.Any(Visit);
        bool Visit(string id)
        {
            if (!chosen.ContainsKey(id) || done.Contains(id)) return false;
            if (!active.Add(id)) return true;
            if (chosen[id].Release.Requires.Keys.Any(dependency => Visit(Key(dependency)))) return true;
            active.Remove(id); done.Add(id); return false;
        }
    }

    private static IReadOnlyList<string> ActivationOrder(Dictionary<string, ResolvedPlugin> chosen)
    {
        var edges = chosen.ToDictionary(pair => pair.Key, pair => pair.Value.Release.Requires.Keys.Select(Key).ToHashSet());
        foreach ((string consumer, ResolvedPlugin plugin) in chosen.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            foreach ((string optional, string range) in plugin.Release.Optional.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string provider = Key(optional);
                if (chosen.TryGetValue(provider, out ResolvedPlugin? offered)
                    && SemVerRange.Parse(range).Satisfies(SemVer.Parse(offered.Release.Version))
                    && !Reaches(provider, consumer, [])) edges[consumer].Add(provider);
            }
        var order = new List<string>(); var seen = new HashSet<string>();
        foreach (string id in chosen.Keys.Order(StringComparer.Ordinal)) Visit(id);
        return order;
        void Visit(string id) { if (!seen.Add(id)) return; foreach (string dependency in edges[id].Order(StringComparer.Ordinal)) Visit(dependency); order.Add(id); }
        bool Reaches(string id, string target, HashSet<string> visited)
            => id == target || (visited.Add(id) && edges[id].Any(next => Reaches(next, target, visited)));
    }

    private static string Key(string value) => value.ToLowerInvariant();
    private sealed record Requirement(string Marketplace, List<string> Ranges);
}

using System.Text.Json;
using DMCBK.PluginSdk;
using Tomlet;

namespace DMCBK.Marketplace;

/// <summary>Combines release asset fragments and protects published release identities.</summary>
public static class ReleaseCatalogueBuilder
{
    /// <summary>Combines unpublished fragments, requiring matching compatibility metadata for every asset of a version.</summary>
    public static ReleaseCatalogue Compose(IEnumerable<ReleaseCatalogue> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        ReleaseCatalogue[] inputs = fragments.Select(Clone).ToArray();
        if (inputs.Length == 0) throw new MarketplaceException("pack.catalogue-empty");
        string id = inputs[0].Id;
        if (inputs.Any(input => input.Id != id)) throw new MarketplaceException("catalogue.identity-mismatch", id);
        var releases = new Dictionary<string, PluginRelease>(StringComparer.Ordinal);
        foreach (PluginRelease release in inputs.SelectMany(input => input.Releases))
        {
            if (!releases.TryGetValue(release.Version, out PluginRelease? existing))
            {
                releases.Add(release.Version, release);
                continue;
            }
            if (!Equivalent(existing, release, includeAssets: false) || existing.Yanked != release.Yanked)
                throw new MarketplaceException("pack.release-metadata-conflict", id, release.Version);
            foreach (ReleaseAsset asset in release.Assets)
            {
                ReleaseAsset? prior = existing.Assets.Find(item => item.Kind == asset.Kind && item.Target == asset.Target);
                if (prior is null) existing.Assets.Add(asset);
                else if (prior.Url != asset.Url || !prior.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new MarketplaceException("pack.immutable-release-conflict", id, release.Version);
            }
        }
        return Clone(new()
        {
            SchemaVersion = 2,
            Id = id,
            Releases = releases.Values.OrderByDescending(release => SemVer.Parse(release.Version)).ToList()
        });
    }

    /// <summary>Adds new versions to a published history; only the yank flag may change for an existing version.</summary>
    public static ReleaseCatalogue Publish(ReleaseCatalogue? previous, ReleaseCatalogue incoming)
    {
        ReleaseCatalogue next = Clone(incoming);
        if (previous is null) return next;
        ReleaseCatalogue current = Clone(previous);
        if (current.Id != next.Id) throw new MarketplaceException("catalogue.identity-mismatch", current.Id, next.Id);
        foreach (PluginRelease release in next.Releases)
        {
            PluginRelease? prior = current.Releases.Find(item => item.Version == release.Version);
            if (prior is null) current.Releases.Add(release);
            else
            {
                if (!Equivalent(prior, release)) throw new MarketplaceException("pack.immutable-release-conflict", next.Id, release.Version);
                prior.Yanked = release.Yanked;
            }
        }
        current.Releases = current.Releases.OrderByDescending(release => SemVer.Parse(release.Version)).ToList();
        return current;
    }

    /// <summary>Compares compatibility, dependencies and optional asset identities independently of serialization order and yank state.</summary>
    public static bool Equivalent(PluginRelease first, PluginRelease second, bool includeAssets = true)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return Fingerprint(first, includeAssets) == Fingerprint(second, includeAssets);
    }

    private static string Fingerprint(PluginRelease release, bool assets) => JsonSerializer.Serialize(new
    {
        release.Version,
        release.ApiVersion,
        release.Dmcbk,
        release.Umpk,
        release.Framework,
        Needs = release.Needs.Order(StringComparer.Ordinal),
        Requires = release.Requires.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        Optional = release.Optional.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        Hosts = release.Hosts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        Assets = assets ? release.Assets.OrderBy(asset => asset.Kind, StringComparer.Ordinal)
            .ThenBy(asset => asset.Target, StringComparer.Ordinal)
            .Select(asset => new { asset.Kind, asset.Target, asset.Url, Sha256 = asset.Sha256.ToLowerInvariant() }).ToArray() : null,
    });

    private static ReleaseCatalogue Clone(ReleaseCatalogue catalogue) => ReleaseCatalogue.Parse(TomletMain.TomlStringFrom(catalogue));
}

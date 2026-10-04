using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DMCBK.PluginSdk;
using Tomlet;
using Tomlet.Attributes;

namespace DMCBK.Marketplace;

/// <summary>A host's explicit marketplace source bindings.</summary>
public sealed class MarketplaceRegistry
{
    /// <summary>The registry schema.</summary>
    [TomlProperty("schema-version")] public int SchemaVersion { get; set; } = 2;
    /// <summary>The configured publisher bindings.</summary>
    [TomlProperty("marketplaces")] public List<MarketplaceBinding> Marketplaces { get; set; } = [];

    /// <summary>Parses bindings and rejects legacy formats or duplicate publisher IDs.</summary>
    public static MarketplaceRegistry Parse(string text)
    {
        if (!new TomlParser().Parse(text).Entries.ContainsKey("schema-version"))
            throw new MarketplaceException("catalogue.schema-required");
        MarketplaceRegistry registry = TomletMain.To<MarketplaceRegistry>(text);
        if (registry.SchemaVersion != 2 || registry.Marketplaces.Select(binding => binding.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != registry.Marketplaces.Count
            || registry.Marketplaces.Any(binding => !PluginManifest.IsUsableId(binding.Id)
                || string.IsNullOrWhiteSpace(binding.Source) || binding.AutoUpdate is not ("off" or "check" or "apply")))
            throw new MarketplaceException("catalogue.registry-invalid");
        return registry;
    }
}

/// <summary>A stable publisher identity bound to one explicit index location.</summary>
public sealed class MarketplaceBinding
{
    /// <summary>The index's publisher ID.</summary>
    [TomlProperty("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The explicit index URL or local directory.</summary>
    [TomlProperty("source")] public string Source { get; set; } = string.Empty;
    /// <summary>The marketplace update policy: off, check or apply.</summary>
    [TomlProperty("auto-update")] public string AutoUpdate { get; set; } = "off";
}

/// <summary>A validated metadata snapshot; it contains no downloaded plugin payloads.</summary>
/// <param name="Index">The identity index.</param>
/// <param name="Catalogues">The release histories bound to this publisher.</param>
/// <param name="FetchedAt">When metadata was validated.</param>
public sealed record MarketplaceSnapshot(MarketplaceIndex Index, IReadOnlyList<CatalogueSource> Catalogues, DateTimeOffset FetchedAt);

/// <summary>Fetches only indexes and release metadata, independently of payload installation.</summary>
public sealed class MarketplaceCatalogueClient
{
    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly int _metadataLimit;

    /// <summary>Creates a metadata client with an explicit cache root and per-document size limit.</summary>
    public MarketplaceCatalogueClient(HttpClient http, string cacheRoot, int metadataBytes = 4 * 1024 * 1024)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheRoot = Path.GetFullPath(cacheRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(metadataBytes, 1);
        _metadataLimit = metadataBytes;
    }

    /// <summary>Reads a bounded v2 index before creating a publisher binding.</summary>
    public async Task<MarketplaceIndex> ReadIndexAsync(string source, CancellationToken cancellationToken = default)
        => MarketplaceIndex.Parse(await ReadAsync(IndexLocation(source), cancellationToken).ConfigureAwait(false));

    /// <summary>Fetches an index and its catalogues, validates bindings, and records a complete snapshot.</summary>
    public async Task<MarketplaceSnapshot> RefreshAsync(MarketplaceBinding binding, CancellationToken cancellationToken = default)
    {
        string location = IndexLocation(binding.Source);
        string indexText = await ReadAsync(location, cancellationToken).ConfigureAwait(false);
        MarketplaceIndex index = MarketplaceIndex.Parse(indexText);
        if (!index.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase))
            throw new MarketplaceException("catalogue.publisher-mismatch", binding.Id, index.Id);
        var sources = new List<CatalogueSource>();
        var documents = new Dictionary<string, string>();
        foreach (PluginIdentity identity in index.Plugins)
        {
            string text = await ReadAsync(RelativeLocation(location, identity.Releases), cancellationToken).ConfigureAwait(false);
            ReleaseCatalogue catalogue = ReleaseCatalogue.Parse(text);
            if (!catalogue.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
                throw new MarketplaceException("catalogue.identity-mismatch", identity.Id, catalogue.Id);
            sources.Add(new(binding.Id, catalogue)); documents.Add(identity.Id.ToLowerInvariant(), text);
        }
        MarketplaceSnapshot? previous = await ReadCachedAsync(binding, cancellationToken).ConfigureAwait(false);
        foreach (CatalogueSource source in sources)
        {
            ReleaseCatalogue? prior = previous?.Catalogues.FirstOrDefault(item => item.Catalogue.Id == source.Catalogue.Id)?.Catalogue;
            if (prior is not null) _ = ReleaseCatalogueBuilder.Publish(prior, source.Catalogue);
        }
        // A single pointer switches only after every catalogue validates. Previous snapshots remain usable.
        string publisher = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Id.ToLowerInvariant() + "\0" + binding.Source)));
        string cache = Path.Combine(_cacheRoot, publisher);
        Directory.CreateDirectory(cache);
        string generation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(cache, generation); Directory.CreateDirectory(staging);
        DateTimeOffset fetched = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(staging, "index.toml"), indexText, cancellationToken).ConfigureAwait(false);
        foreach ((string id, string text) in documents)
            await File.WriteAllTextAsync(Path.Combine(staging, id + ".toml"), text, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(staging, "fetched.txt"), fetched.ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        string pointer = Path.Combine(cache, generation + ".pointer");
        await File.WriteAllTextAsync(pointer, generation, cancellationToken).ConfigureAwait(false);
        File.Move(pointer, Path.Combine(cache, "current"), overwrite: true);
        return new(index, sources, fetched);
    }

    /// <summary>Reads a validated cached snapshot without network access.</summary>
    public async Task<MarketplaceSnapshot?> ReadCachedAsync(MarketplaceBinding binding, CancellationToken cancellationToken = default)
    {
        string publisher = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Id.ToLowerInvariant() + "\0" + binding.Source)));
        string root = Path.Combine(_cacheRoot, publisher);
        if (!File.Exists(Path.Combine(root, "current"))) return null;
        string generation = await File.ReadAllTextAsync(Path.Combine(root, "current"), cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParseExact(generation, "N", out _)) throw new MarketplaceException("catalogue.cache-invalid");
        string folder = Path.Combine(root, generation);
        MarketplaceIndex index = MarketplaceIndex.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "index.toml"), cancellationToken).ConfigureAwait(false));
        if (!index.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase)) throw new MarketplaceException("catalogue.publisher-mismatch");
        var sources = new List<CatalogueSource>();
        foreach (PluginIdentity identity in index.Plugins)
        {
            ReleaseCatalogue catalogue = ReleaseCatalogue.Parse(await File.ReadAllTextAsync(Path.Combine(folder, identity.Id.ToLowerInvariant() + ".toml"), cancellationToken).ConfigureAwait(false));
            if (!catalogue.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase)) throw new MarketplaceException("catalogue.identity-mismatch");
            sources.Add(new(binding.Id, catalogue));
        }
        DateTimeOffset fetched = DateTimeOffset.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "fetched.txt"), cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return new(index, sources, fetched);
    }

    private async Task<string> ReadAsync(string location, CancellationToken token)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) || uri.IsFile)
        {
            var file = new FileInfo(uri?.IsFile == true ? uri.LocalPath : location);
            if (file.Length > _metadataLimit) throw new MarketplaceException("catalogue.metadata-size");
            return await File.ReadAllTextAsync(file.FullName, token).ConfigureAwait(false);
        }
        if (uri.Scheme is not ("http" or "https")) throw new MarketplaceException("catalogue.source-unsupported", uri.Scheme);
        using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > _metadataLimit) throw new MarketplaceException("catalogue.metadata-size");
        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > _metadataLimit) throw new MarketplaceException("catalogue.metadata-size");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output.ToArray());
    }

    private static string IndexLocation(string source)
    {
        if (Directory.Exists(source)) return Path.Combine(Path.GetFullPath(source), "mcc-marketplace.toml");
        return source;
    }
    private static string RelativeLocation(string index, string relative)
    {
        if (!PluginManifest.IsPackagePath(relative)) throw new MarketplaceException("catalogue.path-invalid", relative);
        if (Uri.TryCreate(index, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
            return new Uri(uri, relative).AbsoluteUri;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : index))!, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}

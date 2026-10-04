using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMCBK.Core.Localization;

/// <summary>
/// Downloads server resource packs and layers their language entries into <see cref="HostTranslations"/>.
/// This restores the retired 1.x <c>ChatParser.LoadResourcePackTranslations</c> path: bounded http/https downloads with SHA-1 verification, extraction of <c>assets/*/lang/*.json</c> for <c>en_us</c> plus the run's own language, and a JSON cache so a rejoin reuses entries without downloading the pack again.
/// Failures are swallowed with a debug log, exactly like before: the server already got its answer on the wire, and missing pack strings only fall back to vanilla.
/// </summary>
public sealed class ResourcePackTranslationLoader
{
    /// <summary>Version stamp inside the cache files; bump when the entry shape changes.</summary>
    internal const string CacheVersion = "1";

    private const long MaxDownloadBytes = 256L * 1024 * 1024;
    private const int DownloadBufferSize = 81920;

    private static readonly HttpClient Downloader = new();

    private readonly HostTranslations _translations;
    private readonly string _cacheDirectory;
    private readonly string _language;
    private readonly ILogger _logger;

    /// <summary>Provides the client runtime operation.</summary>
    public ResourcePackTranslationLoader(
        HostTranslations translations,
        string cacheDirectory,
        string language,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _translations = translations;
        _cacheDirectory = cacheDirectory;
        _language = NormalizeLanguage(language);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The local identifier for one push: the server-provided UUID when present, else the legacy SHA-1 hash, else the URL.
    /// Matches the retired 1.x identifier so a pop still clears the push it belongs to.
    /// </summary>
    public static string PackIdentifier(Guid id, string url, string hash)
        => id != Guid.Empty
            ? id.ToString("D")
            : IsValidSha1(hash)
                ? hash.ToLowerInvariant()
                : url;

    /// <summary>True when a pack URL is worth downloading: an absolute http/https URL.</summary>
    public static bool IsDownloadableUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https";

    /// <summary>
    /// Loads one accepted pack's translations into <c>translations</c>, from cache when possible.
    /// Never throws: every expected failure (network, disk, corrupt zip or JSON) is logged at debug level and leaves the pack unlayered.
    /// </summary>
    public async Task LoadAsync(Guid id, string url, string hash, CancellationToken ct)
    {
        string packId = PackIdentifier(id, url, hash);
        try
        {
            if (!IsDownloadableUrl(url))
                return;

            var uri = new Uri(url, UriKind.Absolute);
            string cachePath = CacheFilePath(uri, hash);
            if (TryLoadCached(cachePath, uri, hash, out Dictionary<string, string>? cached) && cached.Count > 0)
            {
                _translations.SetPackTranslations(packId, cached);
                return;
            }

            Dictionary<string, string> entries = await DownloadAndExtractAsync(uri, hash, ct).ConfigureAwait(false);
            if (entries.Count == 0)
                return;

            _translations.SetPackTranslations(packId, entries);
            SaveCached(cachePath, uri, hash, entries);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException)
        {
            _logger.LogDebug(ex, "Resource pack translations unavailable for {PackId}.", packId);
        }
    }

    internal string CacheFilePath(Uri uri, string hash)
    {
        string key = IsValidSha1(hash)
            ? hash.ToLowerInvariant()
            : "url-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        // _cacheDirectory already ends in "resourcepacks" (ResourcePackPolicyFactory.ResolveCacheDirectory), so no extra segment here.
        return Path.Combine(_cacheDirectory, $"{key}.{_language}.json");
    }

    private bool TryLoadCached(
        string cachePath, Uri uri, string hash, out Dictionary<string, string> translations)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(cachePath))
            return false;

        try
        {
            using FileStream cacheFile = File.OpenRead(cachePath);
            ResourcePackCacheEntry? entry = JsonSerializer.Deserialize<ResourcePackCacheEntry>(cacheFile);
            if (entry is null
                || !string.Equals(entry.CacheVersion, CacheVersion, StringComparison.Ordinal)
                || !string.Equals(entry.Language, _language, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.SourceUrl, uri.AbsoluteUri, StringComparison.Ordinal)
                || !string.Equals(entry.SourceHash ?? string.Empty, hash ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || entry.Translations.Count == 0)
            {
                File.Delete(cachePath);
                return false;
            }

            translations = new Dictionary<string, string>(entry.Translations, StringComparer.Ordinal);
            return true;
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            File.Delete(cachePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private void SaveCached(string cachePath, Uri uri, string hash, Dictionary<string, string> translations)
    {
        if (translations.Count == 0)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrEmpty(directory))
                return;

            Directory.CreateDirectory(directory);
            var entry = new ResourcePackCacheEntry
            {
                CacheVersion = CacheVersion,
                Language = _language,
                SourceUrl = uri.AbsoluteUri,
                SourceHash = hash ?? string.Empty,
                Translations = translations,
            };
            File.WriteAllText(cachePath, JsonSerializer.Serialize(entry), Encoding.UTF8);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not write the resource pack translation cache.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not write the resource pack translation cache.");
        }
    }

    private async Task<Dictionary<string, string>> DownloadAndExtractAsync(Uri uri, string hash, CancellationToken ct)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $".mcc-pack-{Guid.NewGuid():N}.tmp");
        try
        {
            using HttpResponseMessage response = await Downloader
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                throw new HttpRequestException($"Resource pack download failed with {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength is long length && length > MaxDownloadBytes)
                throw new InvalidDataException("Resource pack exceeds the download limit.");

            string actualHash;
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
                long total = 0;
                try
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        total += read;
                        if (total > MaxDownloadBytes)
                            throw new InvalidDataException("Resource pack exceeds the download limit.");

                        sha1.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(ct).ConfigureAwait(false);
                actualHash = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            }

            if (IsValidSha1(hash) && !string.Equals(hash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Resource pack hash mismatch for {uri}.");

            await using FileStream packFile = File.OpenRead(temporary);
            return ExtractTranslations(packFile, _language);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Merges <c>assets/*/lang/*.json</c> entries from an open pack stream: <c>en_us</c> first, then the run's own language over the top.
    /// Matches the retired 1.x extraction exactly.
    /// </summary>
    internal static Dictionary<string, string> ExtractTranslations(Stream packStream, string language)
    {
        string selected = NormalizeLanguage(language);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var selectedTranslations = new Dictionary<string, string>(StringComparer.Ordinal);

        using var archive = new ZipArchive(packStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!TryGetEntryLanguage(entry.FullName, out string? entryLanguage))
                continue;

            if (entryLanguage.Equals("en_us", StringComparison.OrdinalIgnoreCase))
                MergeEntry(entry, merged);
            else if (entryLanguage.Equals(selected, StringComparison.OrdinalIgnoreCase))
                MergeEntry(entry, selectedTranslations);
        }

        foreach ((string key, string value) in selectedTranslations)
            merged[key] = value;

        return merged;
    }

    internal static bool TryGetEntryLanguage(string entryPath, [NotNullWhen(true)] out string? language)
    {
        language = null;
        string[] parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("lang", StringComparison.OrdinalIgnoreCase)
            || !parts[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        language = Path.GetFileNameWithoutExtension(parts[3]);
        return !string.IsNullOrEmpty(language);
    }

    internal static string NormalizeLanguage(string? language)
    {
        string normalized = (language ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "" or "auto" => "en_us",
            "en" => "en_us",
            _ => normalized,
        };
    }

    private static void MergeEntry(ZipArchiveEntry entry, Dictionary<string, string> translations)
    {
        using Stream entryStream = entry.Open();
        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(entryStream);
        if (parsed is null)
            return;

        foreach ((string key, string value) in parsed)
            translations[key] = value;
    }

    private static bool IsValidSha1(string? hash)
        => hash is not null && hash.Length == 40 && hash.All(Uri.IsHexDigit);

    private sealed class ResourcePackCacheEntry
    {
        public string CacheVersion { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
        public Dictionary<string, string> Translations { get; set; } = new(StringComparer.Ordinal);
    }
}

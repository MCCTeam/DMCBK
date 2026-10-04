using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DMCBK.PluginSdk;

namespace DMCBK.Marketplace;

/// <summary>A direct development source captured as one immutable archive.</summary>
/// <param name="Package">The shared manifest and catalogue models.</param>
/// <param name="Source">The explicit source requested by the host.</param>
/// <param name="Revision">The exact Git commit, or archive digest for a local/URL import.</param>
public sealed record DevelopmentImport(PackedPlugin Package, string Source, string Revision);

/// <summary>Imports explicit directories, archives or Git revisions without restoring or building projects.</summary>
public sealed class DevelopmentPackageImporter(HttpClient http, string pluginRoot, PackageLimits? limits = null)
{
    private readonly PackageLimits _limits = limits ?? new();

    /// <summary>Whether a request names a direct development source.</summary>
    public static bool IsDirect(string source) => Directory.Exists(source) || File.Exists(source)
        || Uri.TryCreate(source.Split('#')[0], UriKind.Absolute, out _)
        || source.Contains('/');

    /// <summary>Captures the payload and seeds the checksum-addressed download cache.</summary>
    public async Task<DevelopmentImport> ImportAsync(string source, CancellationToken cancellationToken = default)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "dmcbk-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            PackedPlugin package;
            string? revision = null;
            if (Directory.Exists(source)) package = PackDirectory(Path.GetFullPath(source));
            else if (LooksLikeGit(source))
            {
                (string remote, string? requestedRef) = GitSource(source);
                string checkout = Path.Combine(temporary, "checkout");
                await GitAsync(["clone", "--no-checkout", "--", remote, checkout], cancellationToken).ConfigureAwait(false);
                string commit = (await GitAsync(["-C", checkout, "rev-parse", "--verify", (requestedRef ?? "HEAD") + "^{commit}"], cancellationToken).ConfigureAwait(false)).Trim();
                if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit)) throw new MarketplaceException("development.git-revision");
                await GitAsync(["-C", checkout, "checkout", "--detach", commit], cancellationToken).ConfigureAwait(false);
                revision = commit; package = PackDirectory(checkout);
            }
            else
            {
                string archive = Path.Combine(temporary, "download.zip");
                if (File.Exists(source))
                {
                    if (new FileInfo(source).Length > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
                    File.Copy(source, archive);
                }
                else
                {
                    if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http"))
                        throw new MarketplaceException("development.source-invalid", source);
                    using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
                    await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var output = File.Create(archive); byte[] buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read; if (total > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                using ZipArchive zip = ZipFile.OpenRead(archive);
                ZipArchiveEntry entry = zip.GetEntry(PluginManifest.FileName) ?? throw new MarketplaceException("development.manifest-missing");
                if (entry.Length > 1024 * 1024) throw new MarketplaceException("development.manifest-size");
                using var reader = new StreamReader(entry.Open());
                if (!PluginManifest.TryParse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), out PluginManifest manifest, out string? error))
                    throw new MarketplaceException("pack.manifest-invalid", error ?? string.Empty);
                string digest = await HashAsync(archive, cancellationToken).ConfigureAwait(false);
                string url = Uri.TryCreate(source, UriKind.Absolute, out Uri? remote) && remote.Scheme is "http" or "https"
                    ? source : "https://local.dmcbk.invalid/" + digest + ".zip";
                package = new(archive, manifest, Release(manifest, url, digest));
            }
            if (new FileInfo(package.ArchivePath).Length > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
            ReleaseAsset asset = package.Release.Assets.Single();
            string cacheRoot = Path.Combine(Path.GetFullPath(pluginRoot), "cache", "downloads");
            EnsureNoLinks(cacheRoot); Directory.CreateDirectory(cacheRoot);
            string cache = Path.Combine(cacheRoot, asset.Sha256 + ".zip"); EnsureNoLinks(cache);
            string stage = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(package.ArchivePath, stage); File.Move(stage, cache, overwrite: true); }
            finally { File.Delete(stage); }
            return new(package with { ArchivePath = cache }, source, revision ?? asset.Sha256);

            PackedPlugin PackDirectory(string folder)
            {
                EnsureNoLinks(folder);
                if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)), out PluginManifest manifest, out string? error))
                    throw new MarketplaceException("pack.manifest-invalid", error ?? string.Empty);
                return PluginPackageBuilder.Pack(folder, folder, Path.Combine(temporary, "packages"), manifest.Kind, manifest.Target,
                    new Uri("https://local.dmcbk.invalid/"));
            }
        }
        finally { DeleteTemporaryDirectory(temporary); }
    }

    // Git marks object files read-only on Windows. Clear only that bit in our
    // disposable checkout, and never traverse links into another directory.
    internal static void DeleteTemporaryDirectory(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string file in Directory.EnumerateFiles(path, "*", options))
        {
            FileAttributes attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private static PluginRelease Release(PluginManifest manifest, string url, string digest) => new()
    {
        Version = manifest.Version,
        ApiVersion = manifest.ApiVersion,
        Dmcbk = manifest.Dmcbk,
        Umpk = manifest.Umpk,
        Framework = manifest.Framework,
        Needs = manifest.Needs.ToList(),
        Requires = new(manifest.Requires),
        Optional = new(manifest.Optional),
        Hosts = new(manifest.Hosts),
        Assets = [new() { Kind = manifest.Kind, Target = manifest.Target, Url = url, Sha256 = digest }],
    };
    private static bool LooksLikeGit(string source) => source.Split('#')[0].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("git+", StringComparison.Ordinal) || source.StartsWith("git@", StringComparison.Ordinal)
        || !Uri.TryCreate(source, UriKind.Absolute, out _) && source.Contains('/');
    private static (string Remote, string? Ref) GitSource(string source)
    {
        string[] parts = source.Split('#', 2); string remote = parts[0];
        if (parts.Length == 1 && !remote.Contains(':') && !Path.IsPathRooted(remote) && remote.Contains('@'))
        { int at = remote.LastIndexOf('@'); parts = [remote[..at], remote[(at + 1)..]]; remote = parts[0]; }
        if (remote.StartsWith("git+", StringComparison.Ordinal)) remote = remote[4..];
        if (!remote.Contains(":", StringComparison.Ordinal) && !Path.IsPathRooted(remote)) remote = "https://github.com/" + remote + ".git";
        return (remote, parts.Length == 2 ? parts[1] : null);
    }
    private static async Task<string> GitAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new MarketplaceException("development.git-unavailable");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token), error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string result = await output.ConfigureAwait(false); string diagnostics = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new MarketplaceException("development.git-failed", diagnostics);
        return result;
    }
    private static async Task<string> HashAsync(string file, CancellationToken token)
    { await using Stream stream = File.OpenRead(file); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)); }
    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new MarketplaceException("installation.storage-link", current);
    }
}

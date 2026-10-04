using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DMCBK.PluginSdk;
using Tomlet;

namespace DMCBK.Marketplace;

/// <summary>Plans whole graphs and applies staged, recoverable installation transactions.</summary>
public sealed class MarketplaceInstaller
{
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IPluginInstallationHost _host;
    private readonly DependencyResolver _resolver;
    private readonly PackageLimits _limits;

    /// <summary>Creates an installer with an explicit storage root, catalogue snapshot and runtime seam.</summary>
    public MarketplaceInstaller(string pluginRoot, HttpClient http, IPluginInstallationHost host,
        DependencyResolver resolver, PackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        _root = Path.GetFullPath(pluginRoot);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _limits = limits ?? new();
        if (_limits.DownloadBytes <= 0 || _limits.ExpandedBytes <= 0 || _limits.FileCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    /// <summary>Returns the current exact selection without activating packages.</summary>
    public async Task<InstallationLock> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        string text = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        return text.Length == 0 ? new() : InstallationLock.Parse(text);
    }

    /// <summary>Reads immutable startup packages in activation order after recovery.</summary>
    public async Task<IReadOnlyList<PluginInstallation>> ReadInstallationsAsync(CancellationToken cancellationToken = default)
        => BuildGraph(await ReadLockAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Resolves an install before any downloads, unloads or selection writes.</summary>
    public async Task<InstallPlan> PlanInstallAsync(ResolutionRequest request, CancellationToken cancellationToken = default)
    {
        string previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        InstallationLock current = previous.Length == 0 ? new() : InstallationLock.Parse(previous);
        DependencyResolution graph = _resolver.Resolve(request, current.Plugins.Select(plugin =>
            new InstalledVersion(plugin.Id, plugin.MarketplaceId, plugin.Release.Version, plugin.Pinned, plugin.Asset)));
        var next = new InstallationLock { Revision = checked(current.Revision + 1) };
        foreach (ResolvedPlugin resolved in graph.Plugins)
        {
            LockedPlugin? prior = current.Plugins.Find(plugin => plugin.Id == resolved.Id);
            next.Plugins.Add(new()
            {
                Id = resolved.Id,
                MarketplaceId = resolved.MarketplaceId,
                Release = resolved.Release,
                Asset = resolved.Asset,
                Enabled = prior?.Enabled ?? true,
                SourceLocation = prior?.SourceLocation,
                SourceRevision = prior?.SourceRevision,
                Pinned = resolved.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase) ? request.Pin ?? prior?.Pinned ?? false : prior?.Pinned ?? false,
                UpdatePolicy = prior?.UpdatePolicy ?? "inherit"
            });
        }
        string snapshot = TomletMain.TomlStringFrom(next);
        // Serialization freezes mutable catalogue objects before a caller can alter them.
        next = InstallationLock.Parse(snapshot);
        IReadOnlyList<string> affected = Affected(current, next);
        return new(_root, previous, snapshot, graph.Plugins.Where(plugin => affected.Contains(plugin.Id))
            .Select(plugin => new PluginChange(plugin.Id, current.Plugins.Find(prior => prior.Id == plugin.Id)?.Release.Version,
                plugin.Release.Version, plugin.Asset.Kind, plugin.Asset.Target, plugin.Asset.Sha256,
                SameSelection(current.Plugins.Find(prior => prior.Id == plugin.Id), next.Plugins.Find(item => item.Id == plugin.Id)!)
                    ? "installation.dependent-restart" : plugin.Reason)));
    }

    /// <summary>Plans an update using the same graph validation and pins as installation.</summary>
    public Task<InstallPlan> PlanUpdateAsync(ResolutionRequest request, CancellationToken cancellationToken = default)
        => PlanInstallAsync(request with { Update = true }, cancellationToken);

    /// <summary>Plans enabled state, pin and update-policy changes in the installation lock.</summary>
    public async Task<InstallPlan> PlanPolicyAsync(string id, bool? enabled = null, bool? pinned = null,
        string? updatePolicy = null, CancellationToken cancellationToken = default)
    {
        string previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        InstallationLock next = InstallationLock.Parse(previous);
        LockedPlugin plugin = next.Plugins.Find(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new MarketplaceException("installation.not-installed", id);
        plugin.Enabled = enabled ?? plugin.Enabled;
        plugin.Pinned = pinned ?? plugin.Pinned;
        plugin.UpdatePolicy = updatePolicy ?? plugin.UpdatePolicy;
        next.Revision = checked(next.Revision + 1);
        string snapshot = TomletMain.TomlStringFrom(next);
        _ = InstallationLock.Parse(snapshot);
        return new(_root, previous, snapshot,
            [new(plugin.Id, plugin.Release.Version, plugin.Release.Version, plugin.Asset.Kind,
                plugin.Asset.Target, plugin.Asset.Sha256, "installation.policy")]);
    }

    /// <summary>Plans removal while rejecting installed required dependents and preserving user data.</summary>
    public async Task<InstallPlan> PlanUninstallAsync(string id, CancellationToken cancellationToken = default)
    {
        string previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        InstallationLock next = InstallationLock.Parse(previous);
        LockedPlugin removed = next.Plugins.Find(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new MarketplaceException("installation.not-installed", id);
        if (next.Plugins.RemoveAll(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0)
            throw new MarketplaceException("installation.not-installed", id);
        next.Revision = checked(next.Revision + 1);
        string snapshot = TomletMain.TomlStringFrom(next);
        _ = InstallationLock.Parse(snapshot);
        return new(_root, previous, snapshot,
            [new(removed.Id, removed.Release.Version, string.Empty, removed.Asset.Kind,
                removed.Asset.Target, removed.Asset.Sha256, "installation.uninstall")]);
    }

    /// <summary>Reads only completed local transactions and their previous graph selections.</summary>
    public async Task<IReadOnlyList<DMCBK.Core.Plugins.PluginRollbackInfo>> ReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        string root = SafePath("transactions");
        if (!Directory.Exists(root)) return [];
        var results = new List<DMCBK.Core.Plugins.PluginRollbackInfo>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileName(folder);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            string committed = SafePath("transactions/" + id + "/committed.toml");
            if (!File.Exists(committed)) continue;
            _ = InstallationLock.Parse(await File.ReadAllTextAsync(committed, cancellationToken).ConfigureAwait(false));
            string priorText = await File.ReadAllTextAsync(SafePath("transactions/" + id + "/previous.toml"), cancellationToken).ConfigureAwait(false);
            InstallationLock prior = priorText.Length == 0 ? new() : InstallationLock.Parse(priorText);
            results.Add(new(id, File.GetLastWriteTimeUtc(committed), prior.Plugins.Select(plugin => plugin.Id + " " + plugin.Release.Version).ToArray()));
        }
        return results.OrderByDescending(result => result.CommittedAt).ToArray();
    }

    /// <summary>Plans restoration of a previous transaction selection using cached immutable packages.</summary>
    public async Task<InstallPlan> PlanRollbackAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new MarketplaceException("installation.history-invalid");
        string previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        InstallationLock current = previous.Length == 0 ? new() : InstallationLock.Parse(previous);
        string historical = await File.ReadAllTextAsync(SafePath("transactions/" + transactionId + "/previous.toml"), cancellationToken).ConfigureAwait(false);
        InstallationLock next = historical.Length == 0 ? new() : InstallationLock.Parse(historical);
        next.Revision = checked(current.Revision + 1);
        foreach (LockedPlugin plugin in next.Plugins)
            if (!Directory.Exists(PackageDirectory(plugin))) throw new MarketplaceException("installation.rollback-unavailable", plugin.Id);
        IReadOnlyList<string> affected = Affected(current, next);
        return new(_root, previous, TomletMain.TomlStringFrom(next),
            next.Plugins.Where(plugin => affected.Contains(plugin.Id))
                .Select(plugin => new PluginChange(plugin.Id, current.Plugins.Find(prior => prior.Id == plugin.Id)?.Release.Version,
                    plugin.Release.Version, plugin.Asset.Kind, plugin.Asset.Target, plugin.Asset.Sha256, "installation.rollback"))
                .Concat(current.Plugins.Where(plugin => next.Plugins.All(item => item.Id != plugin.Id))
                    .Select(plugin => new PluginChange(plugin.Id, plugin.Release.Version, string.Empty,
                        plugin.Asset.Kind, plugin.Asset.Target, plugin.Asset.Sha256, "installation.uninstall"))));
    }

    /// <summary>Stages and validates packages, atomically commits the graph, and rolls back failed activation.</summary>
    public async Task<PluginOperationResult> ApplyAsync(InstallPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != _root) throw new MarketplaceException("installation.plan-root-mismatch");
        await using FileStream rootLock = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(Path.Combine(_root, "transactions", "pending.toml")))
            throw new MarketplaceException("installation.recovery-required");
        if (await ReadCurrentAsync(cancellationToken).ConfigureAwait(false) != plan.Previous)
            throw new MarketplaceException("installation.plan-stale");
        InstallationLock previous = plan.Previous.Length == 0 ? new() : InstallationLock.Parse(plan.Previous);
        InstallationLock next = InstallationLock.Parse(plan.Next);
        string transactionId = Guid.NewGuid().ToString("N");
        string transactionRoot = SafePath("transactions/" + transactionId);
        Directory.CreateDirectory(transactionRoot);
        var prepared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LockedPlugin plugin in next.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SameSelection(previous.Plugins.Find(prior => prior.Id == plugin.Id), plugin)
                || plan.Changes.Any(change => change.Id == plugin.Id && change.Reason == "installation.rollback"))
                prepared[plugin.Id] = PackageDirectory(plugin);
            else
            {
                string archive = await DownloadAsync(plugin.Asset, cancellationToken).ConfigureAwait(false);
                string staging = Path.Combine(transactionRoot, "staging", plugin.Id);
                await ExtractAsync(archive, staging, cancellationToken).ConfigureAwait(false);
                prepared[plugin.Id] = staging;
            }
            await ValidateManifestAsync(plugin, prepared[plugin.Id], cancellationToken).ConfigureAwait(false);
        }
        await _host.PrepareAsync(BuildGraph(next, prepared), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await AtomicWriteAsync(Path.Combine(transactionRoot, "previous.toml"), plan.Previous, cancellationToken).ConfigureAwait(false);
        string journal = Path.Combine(_root, "transactions", "pending.toml");
        await AtomicWriteAsync(journal, TomletMain.TomlStringFrom(new TransactionJournal
        { Id = transactionId, Previous = plan.Previous, Next = plan.Next }), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> affected = Affected(previous, next);
        try
        {
            await _host.DeactivateAsync(previous.Plugins.Where(plugin => affected.Contains(plugin.Id))
                .Reverse().Select(plugin => plugin.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (LockedPlugin plugin in next.Plugins)
            {
                string destination = PackageDirectory(plugin);
                string source = prepared[plugin.Id];
                if (source == destination) continue;
                EnsureNoLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination)) { VerifyIdentical(source, destination); Directory.Delete(source, recursive: true); }
                else Directory.Move(source, destination);
            }
            await AtomicWriteAsync(LockPath, plan.Next, cancellationToken).ConfigureAwait(false);
            await _host.ActivateAsync(BuildGraph(next).Where(plugin => affected.Contains(plugin.Manifest.Id)).ToArray(), cancellationToken).ConfigureAwait(false);
            await AtomicWriteAsync(Path.Combine(transactionRoot, "committed.toml"), plan.Next, CancellationToken.None).ConfigureAwait(false);
            File.Delete(journal);
            return new(transactionId, next);
        }
        catch
        {
            // The journal remains if rollback itself fails, so next startup retries restoration.
            await _host.DeactivateAsync(next.Plugins.Where(plugin => affected.Contains(plugin.Id)).Reverse()
                .Select(plugin => plugin.Id).ToArray(), CancellationToken.None).ConfigureAwait(false);
            await RestoreLockAsync(plan.Previous).ConfigureAwait(false);
            await _host.ActivateAsync(BuildGraph(previous).Where(plugin => affected.Contains(plugin.Manifest.Id)).ToArray(), CancellationToken.None).ConfigureAwait(false);
            File.Delete(journal);
            throw;
        }
    }

    /// <summary>Restores an interrupted transaction before startup activation.</summary>
    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using FileStream rootLock = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(_root, "transactions", "pending.toml");
        if (!File.Exists(path)) return false;
        TransactionJournal journal = TomletMain.To<TransactionJournal>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        if (!Guid.TryParseExact(journal.Id, "N", out _)) throw new MarketplaceException("installation.journal-invalid");
        InstallationLock next = InstallationLock.Parse(journal.Next);
        InstallationLock previous = journal.Previous.Length == 0 ? new() : InstallationLock.Parse(journal.Previous);
        await _host.DeactivateAsync(next.Plugins.AsEnumerable().Reverse().Select(plugin => plugin.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        await RestoreLockAsync(journal.Previous).ConfigureAwait(false);
        await _host.ActivateAsync(BuildGraph(previous), cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        return true;
    }

    private string LockPath => Path.Combine(_root, "plugins.lock.toml");
    /// <summary>Purges retained data only while the plugin remains uninstalled and the root lock is held.</summary>
    public async Task PurgeUserDataAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!PluginManifest.IsUsableId(id)) throw new MarketplaceException("installation.plugin-id-invalid", id);
        await using FileStream rootLock = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        InstallationLock selection = await ReadLockAsync(cancellationToken).ConfigureAwait(false);
        if (selection.Plugins.Any(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            throw new MarketplaceException("installation.purge-installed", id);
        string user = SafePath("userdata/" + id.ToLowerInvariant());
        if (Directory.Exists(user)) Directory.Delete(user, recursive: true);
    }

    private string PackageDirectory(LockedPlugin plugin) => SafePath(plugin.PackagePath);
    private async Task<string> ReadCurrentAsync(CancellationToken token)
        => File.Exists(LockPath) ? await File.ReadAllTextAsync(LockPath, token).ConfigureAwait(false) : string.Empty;
    private async Task RestoreLockAsync(string previous)
    {
        if (previous.Length == 0) File.Delete(LockPath);
        else await AtomicWriteAsync(LockPath, previous, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        EnsureNoLinks(_root);
        Directory.CreateDirectory(_root);
        string path = SafePath(".installation.lock");
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200) { await Task.Delay(25, token).ConfigureAwait(false); }
            catch (IOException) { throw new MarketplaceException("installation.root-busy"); }
        }
    }

    private async Task<string> DownloadAsync(ReleaseAsset asset, CancellationToken token)
    {
        string path = SafePath("cache/downloads/" + asset.Sha256.ToLowerInvariant() + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (await MatchesHashAsync(path, asset.Sha256, token).ConfigureAwait(false)) return path;
            File.Delete(path);
        }
        if (new Uri(asset.Url).Host == "local.dmcbk.invalid") throw new MarketplaceException("installation.rollback-unavailable", asset.Sha256);
        using HttpResponseMessage response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920]; long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total = checked(total + read);
                    if (total > _limits.DownloadBytes) throw new MarketplaceException("installation.download-size");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            if (!await MatchesHashAsync(temporary, asset.Sha256, token).ConfigureAwait(false))
                throw new MarketplaceException("installation.hash-mismatch");
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    private async Task ExtractAsync(string archivePath, string staging, CancellationToken token)
    {
        EnsureNoLinks(staging);
        Directory.CreateDirectory(staging);
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > _limits.FileCount) throw new MarketplaceException("installation.archive-file-count");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            bool directory = entry.FullName.EndsWith('/');
            string name = directory ? entry.FullName.TrimEnd('/') : entry.FullName;
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!PluginManifest.IsPackagePath(name) || !PortablePath(name) || !seen.Add(name)
                || unixType is not (0 or 0x8000 or 0x4000)
                || (unixType == 0x4000 && !directory))
                throw new MarketplaceException("installation.archive-path", entry.FullName);
            total = checked(total + entry.Length);
            if (total > _limits.ExpandedBytes) throw new MarketplaceException("installation.archive-expanded-size");
            string destination = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
            if (directory) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using Stream source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920]; long written = 0; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                written = checked(written + read);
                if (written > entry.Length) throw new MarketplaceException("installation.archive-expanded-size");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new MarketplaceException("installation.archive-size-mismatch");
        }
    }

    private static async Task ValidateManifestAsync(LockedPlugin plugin, string folder, CancellationToken token)
    {
        string manifestPath = Path.Combine(folder, PluginManifest.FileName);
        if (!File.Exists(manifestPath)) throw new MarketplaceException("installation.manifest-missing", plugin.Id);
        string text = await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false);
        if (!PluginManifest.TryParse(text, out PluginManifest manifest, out _)
            || manifest.Id != plugin.Id || manifest.Version != plugin.Release.Version
            || manifest.Kind != plugin.Asset.Kind || manifest.Target != plugin.Asset.Target
            || manifest.ApiVersion != plugin.Release.ApiVersion || manifest.Dmcbk != plugin.Release.Dmcbk
            || manifest.Umpk != plugin.Release.Umpk || manifest.Framework != plugin.Release.Framework
            || !manifest.Needs.Order(StringComparer.Ordinal).SequenceEqual(plugin.Release.Needs.Order(StringComparer.Ordinal))
            || !SameTable(manifest.Requires, plugin.Release.Requires) || !SameTable(manifest.Optional, plugin.Release.Optional)
            || !SameTable(manifest.Hosts, plugin.Release.Hosts))
            throw new MarketplaceException("installation.catalogue-manifest-mismatch", plugin.Id);
        foreach (string file in manifest.Deps.Append(manifest.Entry).Concat(manifest.Exports.Assemblies.Where(file => file != PluginExports.EntryToken)))
            if (!File.Exists(Path.Combine(folder, file))) throw new MarketplaceException("installation.package-file-missing", plugin.Id, file);
    }

    private IReadOnlyList<PluginInstallation> BuildGraph(InstallationLock selection, IReadOnlyDictionary<string, string>? prepared = null)
        => selection.Plugins.Select(plugin =>
        {
            string folder = prepared is not null ? prepared[plugin.Id] : PackageDirectory(plugin);
            if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)), out PluginManifest manifest, out _))
                throw new MarketplaceException("installation.manifest-invalid", plugin.Id);
            return new PluginInstallation(manifest, folder, SafePath("userdata/" + plugin.Id), plugin.Enabled);
        }).ToArray();

    private string SafePath(string relative)
    {
        if (!PluginManifest.IsPackagePath(relative) || !PortablePath(relative)) throw new MarketplaceException("installation.path-invalid", relative);
        string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MarketplaceException("installation.path-invalid", relative);
        EnsureNoLinks(path);
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new MarketplaceException("installation.storage-link", current);
    }

    private static bool PortablePath(string path) => path.Split('/').All(part =>
        part.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0 && !part.EndsWith('.') && !part.EndsWith(' ')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase));
    private static bool SameTable(Dictionary<string, string> left, Dictionary<string, string> right)
        => left.Count == right.Count && left.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    private static bool SameSelection(LockedPlugin? left, LockedPlugin right) => left is not null
        && left.Release.Version == right.Release.Version && left.MarketplaceId == right.MarketplaceId && InstallationLock.SameAsset(left.Asset, right.Asset);
    private static IReadOnlyList<string> Affected(InstallationLock previous, InstallationLock next)
    {
        var changed = previous.Plugins.Where(plugin => next.Plugins.Find(item => item.Id == plugin.Id) is not { } other
                || !SameSelection(plugin, other) || plugin.Enabled != other.Enabled).Select(plugin => plugin.Id)
            .Concat(next.Plugins.Where(plugin => previous.Plugins.All(prior => prior.Id != plugin.Id)).Select(plugin => plugin.Id)).ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (LockedPlugin plugin in previous.Plugins.Concat(next.Plugins))
                if (plugin.Release.Requires.Keys.Concat(plugin.Release.Optional.Keys).Any(id => changed.Contains(id.ToLowerInvariant())))
                    added |= changed.Add(plugin.Id);
        } while (added);
        return changed.ToArray();
    }
    private static async Task<bool> MatchesHashAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task AtomicWriteAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
    private static void VerifyIdentical(string source, string destination)
    {
        string[] incoming = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(source, path)).Order(StringComparer.Ordinal).ToArray();
        string[] existing = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(destination, path)).Order(StringComparer.Ordinal).ToArray();
        if (!incoming.SequenceEqual(existing)) throw new MarketplaceException("installation.immutable-conflict");
        foreach (string path in incoming)
        {
            EnsureNoLinks(Path.Combine(destination, path));
            if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(source, path))).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, path)))))
                throw new MarketplaceException("installation.immutable-conflict");
        }
    }
}

internal sealed class TransactionJournal
{
    public string Id { get; set; } = string.Empty;
    public string Previous { get; set; } = string.Empty;
    public string Next { get; set; } = string.Empty;
}

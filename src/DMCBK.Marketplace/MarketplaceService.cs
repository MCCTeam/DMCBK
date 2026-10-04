using System.Globalization;
using DMCBK.Core;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Tomlet;

namespace DMCBK.Marketplace;

/// <summary>Host-facing marketplace operations backed exclusively by v2 plans and transactions.</summary>
public sealed partial class MarketplaceService : IPluginMarket, IDisposable, IAsyncDisposable
{
    private readonly Client _client;
    private readonly string _root;
    private readonly string _registryPath;
    private readonly HttpClient _http;
    private readonly IPluginInstallationHost _runtime;
    private readonly MarketplaceCatalogueClient _catalogues;

    /// <summary>Creates a service using explicit plugin, registry, transport and runtime ownership.</summary>
    public MarketplaceService(Client client, string pluginsRoot, string registryPath, HttpClient http, IPluginInstallationHost runtime)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _root = Path.GetFullPath(pluginsRoot); _registryPath = Path.GetFullPath(registryPath);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _catalogues = new(http, Path.Combine(_root, "cache", "catalogues"));
        _client.AttachPluginMarket(this);
        _client.StatusChanged += OnAutomaticStatusChanged;
        if (runtime is IManagedPluginHost managed)
        {
            managed.InstallationSource = ReadInstallationsAsync;
            managed.ChangeEnabled = SetEnabledAsync;
        }
    }

    internal bool OwnsHttpClient { get; set; }
    /// <summary>Releases the HTTP client only when created by module composition.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.StatusChanged -= OnAutomaticStatusChanged;
        _shutdown.Cancel();
        if (OwnsHttpClient) _http.Dispose();
    }

    /// <inheritdoc/>
    public Func<InstallConfirmation, CancellationToken, ValueTask<bool>> Confirm { get; set; } = (_, _) => ValueTask.FromResult(false);
    /// <summary>Explicit source selection and mixed-release fallback policy.</summary>
    public AssetPolicy Assets { get; set; } = new();
    /// <summary>Whether ordinary requests may select prereleases.</summary>
    public bool IncludePrerelease { get; set; }
    /// <summary>The client-specific diagnostic culture.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>Recovers interrupted transactions and reads startup selections without fetching metadata.</summary>
    public async Task<IReadOnlyList<PluginInstallation>> ReadInstallationsAsync(CancellationToken cancellationToken = default)
    {
        MarketplaceInstaller installer = EmptyInstaller();
        await installer.RecoverAsync(cancellationToken).ConfigureAwait(false);
        return await installer.ReadInstallationsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plans a graph using the latest available metadata without changing packages.</summary>
    public async Task<InstallPlan> PlanInstallAsync(ResolutionRequest request, CancellationToken cancellationToken = default)
        => await (await InstallerAsync(cancellationToken).ConfigureAwait(false)).PlanInstallAsync(request, cancellationToken).ConfigureAwait(false);
    /// <summary>Plans a dependency-aware update.</summary>
    public async Task<InstallPlan> PlanUpdateAsync(ResolutionRequest request, CancellationToken cancellationToken = default)
        => await (await InstallerAsync(cancellationToken).ConfigureAwait(false)).PlanUpdateAsync(request, cancellationToken).ConfigureAwait(false);
    /// <summary>Applies an already reviewed graph using its frozen catalogue metadata.</summary>
    public Task<PluginOperationResult> ApplyAsync(InstallPlan plan, CancellationToken cancellationToken = default)
        => EmptyInstaller().ApplyAsync(plan, cancellationToken);

    /// <summary>Changes enabled state through the same transaction engine as installs and updates.</summary>
    public Task<PluginActionResult> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
        => OperationAsync(async () =>
        {
            MarketplaceInstaller installer = EmptyInstaller();
            await installer.ApplyAsync(await installer.PlanPolicyAsync(id, enabled: enabled, cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return new(true, Text(enabled ? "operation.enabled" : "operation.disabled", id));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> InstallAsync(string what, string? version = null, bool assumeYes = false, CancellationToken ct = default)
        => InstallAsync(what, new PluginInstallOptions(Assets.PreferSource, Assets.AllowSourceFallback, IncludePrerelease), version, assumeYes, ct);

    /// <inheritdoc/>
    public Task<PluginActionResult> InstallAsync(string what, PluginInstallOptions options, string? version = null, bool assumeYes = false, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            DevelopmentImport? imported = DevelopmentPackageImporter.IsDirect(what)
                ? await new DevelopmentPackageImporter(_http, _root).ImportAsync(what, ct).ConfigureAwait(false) : null;
            (string id, string? marketplace) = imported is null ? Identity(what) : (imported.Package.Manifest.Id, "development");
            var request = Request(id, marketplace, version) with
            {
                Assets = new AssetPolicy(options.PreferSource, options.AllowSourceFallback),
                IncludePrerelease = options.IncludePrerelease,
            };
            MarketplaceInstaller installer = await InstallerAsync(ct, imported is null ? null : new CatalogueSource("development",
                new ReleaseCatalogue { SchemaVersion = 2, Id = id, Releases = [imported.Package.Release] })).ConfigureAwait(false);
            InstallPlan plan = await installer.PlanInstallAsync(request, ct).ConfigureAwait(false);
            if (imported is not null)
            {
                InstallationLock selection = InstallationLock.Parse(plan.Next);
                LockedPlugin plugin = selection.Plugins.Single(plugin => plugin.Id == id);
                plugin.SourceLocation = imported.Source; plugin.SourceRevision = imported.Revision;
                plan = new(plan.Owner, plan.Previous, TomletMain.TomlStringFrom(selection), plan.Changes);
            }
            if (!assumeYes && !await ConfirmPlanAsync(plan, id, marketplace, what, ct).ConfigureAwait(false))
                return new(false, Text("operation.cancelled"));
            PluginOperationResult result = await ApplyAsync(plan, ct).ConfigureAwait(false);
            return new(true, Text("operation.installed", id, result.Selection.Plugins.Find(plugin => plugin.Id == id)!.Release.Version));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> UninstallAsync(string id, bool purge = false, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            MarketplaceInstaller installer = EmptyInstaller();
            await installer.ApplyAsync(await installer.PlanUninstallAsync(id, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (purge) await installer.PurgeUserDataAsync(id, ct).ConfigureAwait(false);
            return new(true, Text("operation.uninstalled", id));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> UpdateAsync(string? id = null, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            InstallationLock selection = await EmptyInstaller().ReadLockAsync(ct).ConfigureAwait(false);
            string[] targets = id is null or "all" ? selection.Plugins.Where(plugin => !plugin.Pinned).Select(plugin => plugin.Id).ToArray() : [id.ToLowerInvariant()];
            foreach (string target in targets)
            {
                InstallPlan plan = await PlanUpdateAsync(Request(target, null, null), ct).ConfigureAwait(false);
                if (plan.Changes.Count == 0) continue;
                if (!await ConfirmPlanAsync(plan, target, null, target, ct).ConfigureAwait(false)) return new(false, Text("operation.cancelled"));
                await ApplyAsync(plan, ct).ConfigureAwait(false);
            }
            return new(true, Text("operation.updated", targets.Length));
        });

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PluginUpdateInfo>> OutdatedAsync(CancellationToken ct = default)
    {
        InstallationLock installed = await EmptyInstaller().ReadLockAsync(ct).ConfigureAwait(false);
        MarketplaceInstaller installer = await InstallerAsync(ct).ConfigureAwait(false);
        var results = new List<PluginUpdateInfo>();
        foreach (LockedPlugin plugin in installed.Plugins)
        {
            try
            {
                InstallPlan plan = await installer.PlanUpdateAsync(Request(plugin.Id, null, null), ct).ConfigureAwait(false);
                string available = InstallationLock.Parse(plan.Next).Plugins.Find(selected => selected.Id == plugin.Id)!.Release.Version;
                if (available != plugin.Release.Version)
                    results.Add(new(plugin.Id, plugin.Release.Version, available, plugin.Pinned, null, null));
            }
            catch (MarketplaceException failure) { results.Add(new(plugin.Id, plugin.Release.Version, null, plugin.Pinned, Diagnostic(failure), null)); }
        }
        return results;
    }

    /// <inheritdoc/>
    public Task<PluginActionResult> PinAsync(string id, string? version = null, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            MarketplaceInstaller installer = version is null ? EmptyInstaller() : await InstallerAsync(ct).ConfigureAwait(false);
            InstallPlan plan = version is null ? await installer.PlanPolicyAsync(id, pinned: true, cancellationToken: ct).ConfigureAwait(false)
                : await installer.PlanInstallAsync(Request(id, null, version) with { Pin = true }, ct).ConfigureAwait(false);
            await installer.ApplyAsync(plan, ct).ConfigureAwait(false);
            return new(true, Text("operation.pinned", id));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> UnpinAsync(string id, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            MarketplaceInstaller installer = EmptyInstaller();
            await installer.ApplyAsync(await installer.PlanPolicyAsync(id, pinned: false, cancellationToken: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return new(true, Text("operation.unpinned", id));
        });

    /// <summary>Restores a previously selected cached graph.</summary>
    public Task<PluginActionResult> RollbackAsync(string transactionId, CancellationToken cancellationToken = default)
        => OperationAsync(async () =>
        {
            MarketplaceInstaller installer = EmptyInstaller();
            await installer.ApplyAsync(await installer.PlanRollbackAsync(transactionId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return new(true, Text("operation.rolled-back", transactionId));
        });

    /// <inheritdoc/>
    public async Task<PluginMarketInfo?> InfoAsync(string id, CancellationToken ct = default)
    {
        InstallationLock selection = await EmptyInstaller().ReadLockAsync(ct).ConfigureAwait(false);
        LockedPlugin? plugin = selection.Plugins.Find(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (plugin is null) return null;
        string folder = Path.Combine(_root, plugin.PackagePath);
        if (!PluginManifest.TryParse(await File.ReadAllTextAsync(Path.Combine(folder, "plugin.toml"), ct).ConfigureAwait(false), out PluginManifest manifest, out string? error))
            throw new MarketplaceException("installation.manifest-invalid", id, error ?? string.Empty);
        PluginInfo? runtime = _client.PluginHost?.List().FirstOrDefault(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        string languages = Path.Combine(folder, "lang");
        return new(manifest.Id, manifest.Version, manifest.IsSourceEntry ? PluginEntryKind.Source : PluginEntryKind.Compiled,
            plugin.Enabled, runtime?.Loaded ?? false, runtime?.Status, folder, manifest.Meta.Description, manifest.Meta.Homepage,
            manifest.Meta.Tags, manifest.Meta.Uses, Table(manifest.Requires), Table(manifest.Optional),
            selection.Plugins.Where(other => other.Release.Requires.Keys.Contains(id, StringComparer.OrdinalIgnoreCase)).Select(other => other.Id).ToArray(),
            manifest.Exports.Assemblies, Directory.Exists(languages) ? Directory.GetFiles(languages, "*.toml").Select(path => Path.GetFileNameWithoutExtension(path)).ToArray() : [],
            manifest.Man, plugin.MarketplaceId == "development", plugin.MarketplaceId, plugin.SourceLocation ?? plugin.Asset.Url, null, plugin.Pinned, plugin.Pinned ? manifest.Version : null,
            plugin.UpdatePolicy, manifest.Services.Offline, runtime?.FaultCount ?? 0, runtime?.LastError, plugin.Asset.Target, plugin.Asset.Sha256);
    }

    /// <inheritdoc/>
    public Task<PluginActionResult> MarketplaceAddAsync(string source, string? name = null, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            MarketplaceIndex index = await _catalogues.ReadIndexAsync(source, ct).ConfigureAwait(false);
            if (name is not null && !name.Equals(index.Id, StringComparison.OrdinalIgnoreCase))
                throw new MarketplaceException("catalogue.publisher-mismatch", name, index.Id);
            var binding = new MarketplaceBinding { Id = index.Id.ToLowerInvariant(), Source = Directory.Exists(source) ? Path.GetFullPath(source) : source };
            await _catalogues.RefreshAsync(binding, ct).ConfigureAwait(false);
            await EditRegistryAsync(registry =>
            {
                if (registry.Marketplaces.Any(existing => existing.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new MarketplaceException("catalogue.publisher-already-bound", binding.Id);
                registry.Marketplaces.Add(binding);
            }, ct).ConfigureAwait(false);
            return new(true, Text("operation.marketplace-added", binding.Id));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> MarketplaceRemoveAsync(string name, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            await EditRegistryAsync(registry =>
            {
                if (registry.Marketplaces.RemoveAll(binding => binding.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
                    throw new MarketplaceException("catalogue.publisher-missing", name);
            }, ct).ConfigureAwait(false);
            return new(true, Text("operation.marketplace-removed", name));
        });

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MarketplaceInfo>> MarketplacesAsync(CancellationToken ct = default)
    {
        var result = new List<MarketplaceInfo>();
        foreach (MarketplaceBinding binding in (await RegistryAsync(ct).ConfigureAwait(false)).Marketplaces)
        {
            try
            {
                MarketplaceSnapshot? snapshot = await _catalogues.ReadCachedAsync(binding, ct).ConfigureAwait(false);
                result.Add(new(binding.Id, binding.Source, snapshot?.Index.Name, snapshot?.Index.Plugins.Count ?? 0,
                    binding.AutoUpdate, snapshot?.FetchedAt, Directory.Exists(binding.Source), null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { result.Add(new(binding.Id, binding.Source, null, 0, binding.AutoUpdate, null, Directory.Exists(binding.Source), exception.Message)); }
        }
        return result;
    }

    /// <inheritdoc/>
    public Task<PluginActionResult> MarketplaceRefreshAsync(string? name = null, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            MarketplaceRegistry registry = await RegistryAsync(ct).ConfigureAwait(false);
            MarketplaceBinding[] targets = registry.Marketplaces.Where(binding => name is null || binding.Id.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (name is not null && targets.Length == 0) throw new MarketplaceException("catalogue.publisher-missing", name);
            foreach (MarketplaceBinding binding in targets) await _catalogues.RefreshAsync(binding, ct).ConfigureAwait(false);
            return new(true, Text("operation.catalogues-refreshed", targets.Length));
        });

    /// <inheritdoc/>
    public Task<PluginActionResult> MarketplaceAutoUpdateAsync(string name, string policy, CancellationToken ct = default)
        => OperationAsync(async () =>
        {
            if (policy is not ("off" or "check" or "apply")) throw new MarketplaceException("catalogue.policy-invalid", policy);
            await EditRegistryAsync(registry =>
            {
                MarketplaceBinding binding = registry.Marketplaces.Find(binding => binding.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new MarketplaceException("catalogue.publisher-missing", name);
                binding.AutoUpdate = policy;
            }, ct).ConfigureAwait(false);
            return new(true, Text("operation.policy-updated", name, policy));
        });

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MarketplaceSearchResult>> SearchAsync(string text, string? marketplace = null, CancellationToken ct = default)
    {
        InstallationLock installed = await EmptyInstaller().ReadLockAsync(ct).ConfigureAwait(false);
        var result = new List<MarketplaceSearchResult>();
        foreach (MarketplaceBinding binding in (await RegistryAsync(ct).ConfigureAwait(false)).Marketplaces.Where(binding => marketplace is null || binding.Id.Equals(marketplace, StringComparison.OrdinalIgnoreCase)))
        {
            MarketplaceSnapshot? snapshot = await _catalogues.ReadCachedAsync(binding, ct).ConfigureAwait(false);
            if (snapshot is null) continue;
            foreach (PluginIdentity identity in snapshot.Index.Plugins.Where(plugin => new[] { plugin.Id, plugin.Description }.Concat(plugin.Tags)
                .Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase))))
            {
                PluginRelease? release = snapshot.Catalogues.Single(source => source.Catalogue.Id == identity.Id).Catalogue.Releases
                    .Where(release => !release.Yanked && (IncludePrerelease || !SemVer.Parse(release.Version).IsPrerelease))
                    .OrderByDescending(release => SemVer.Parse(release.Version)).FirstOrDefault();
                if (release is null) continue;
                string? incompatibility = ReleaseCompatibility.Check(release, HostInfo.FromClient(_client)).FirstOrDefault() is { } failure ? Diagnostic(failure) : null;
                if (incompatibility is null)
                    try { _ = AssetSelector.Select(release, HostInfo.FromClient(_client).RuntimeTarget, Assets); }
                    catch (MarketplaceException assetFailure) { incompatibility = Diagnostic(assetFailure); }
                result.Add(new(identity.Id, binding.Id, release.Version, identity.Description, identity.Tags, binding.Source,
                    incompatibility, installed.Plugins.Find(plugin => plugin.Id == identity.Id)?.Release.Version));
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PluginReleaseInfo>> VersionsAsync(string id, string? marketplace = null, CancellationToken ct = default)
    {
        if (!PluginManifest.IsUsableId(id)) throw new MarketplaceException("resolution.id-invalid", id);
        var results = new List<PluginReleaseInfo>();
        foreach (MarketplaceBinding binding in (await RegistryAsync(ct).ConfigureAwait(false)).Marketplaces
            .Where(binding => marketplace is null || binding.Id.Equals(marketplace, StringComparison.OrdinalIgnoreCase)))
        {
            MarketplaceSnapshot snapshot = await _catalogues.ReadCachedAsync(binding, ct).ConfigureAwait(false)
                ?? await _catalogues.RefreshAsync(binding, ct).ConfigureAwait(false);
            ReleaseCatalogue? catalogue = snapshot.Catalogues.FirstOrDefault(source => source.Catalogue.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Catalogue;
            if (catalogue is null) continue;
            foreach (PluginRelease release in catalogue.Releases)
            {
                string? incompatibility = ReleaseCompatibility.Check(release, HostInfo.FromClient(_client)).FirstOrDefault() is { } failure
                    ? Diagnostic(failure) : null;
                results.Add(new(release.Version, binding.Id, release.Yanked, incompatibility,
                    release.Assets.Select(asset => new PluginAssetInfo(asset.Kind, asset.Target)).ToArray()));
            }
        }
        return results.OrderByDescending(release => SemVer.Parse(release.Version)).ThenBy(release => release.Marketplace, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PluginRollbackInfo>> RollbackHistoryAsync(CancellationToken ct = default)
        => EmptyInstaller().ReadHistoryAsync(ct);

    private async Task<MarketplaceInstaller> InstallerAsync(CancellationToken token, CatalogueSource? development = null)
    {
        var sources = new Dictionary<(string Publisher, string Id), CatalogueSource>();
        foreach (MarketplaceBinding binding in (await RegistryAsync(token).ConfigureAwait(false)).Marketplaces)
        {
            MarketplaceSnapshot snapshot = await _catalogues.ReadCachedAsync(binding, token).ConfigureAwait(false)
                ?? await _catalogues.RefreshAsync(binding, token).ConfigureAwait(false);
            foreach (CatalogueSource source in snapshot.Catalogues) sources.Add((binding.Id.ToLowerInvariant(), source.Catalogue.Id.ToLowerInvariant()), source);
        }
        if (development is not null) sources[(development.MarketplaceId, development.Catalogue.Id)] = development;
        // Installed snapshots remain resolvable if a registry binding is removed or a release is yanked.
        foreach (LockedPlugin plugin in (await EmptyInstaller().ReadLockAsync(token).ConfigureAwait(false)).Plugins)
        {
            var key = (plugin.MarketplaceId.ToLowerInvariant(), plugin.Id);
            if (sources.TryGetValue(key, out CatalogueSource? source))
            {
                PluginRelease? known = source.Catalogue.Releases.Find(release => release.Version == plugin.Release.Version);
                if (known is null) source.Catalogue.Releases.Add(plugin.Release);
                else if (!ReleaseCatalogueBuilder.Equivalent(known, plugin.Release))
                    throw new MarketplaceException("catalogue.release-mutated", plugin.Id, plugin.Release.Version);
            }
            else sources[key] = new(plugin.MarketplaceId, new() { SchemaVersion = 2, Id = plugin.Id, Releases = [plugin.Release] });
        }
        return new(_root, _http, _runtime, new(HostInfo.FromClient(_client), sources.Values));
    }

    private MarketplaceInstaller EmptyInstaller() => new(_root, _http, _runtime, new(HostInfo.FromClient(_client), []));
    private async Task<MarketplaceRegistry> RegistryAsync(CancellationToken token)
        => File.Exists(_registryPath) ? MarketplaceRegistry.Parse(await File.ReadAllTextAsync(_registryPath, token).ConfigureAwait(false)) : new();
    private async Task EditRegistryAsync(Action<MarketplaceRegistry> edit, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        FileStream? lease = null;
        for (int attempt = 0; lease is null; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { lease = new FileStream(_registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200) { await Task.Delay(25, token).ConfigureAwait(false); }
            catch (IOException) { throw new MarketplaceException("catalogue.registry-busy"); }
        }
        await using (lease)
        {
            MarketplaceRegistry registry = await RegistryAsync(token).ConfigureAwait(false); edit(registry);
            string text = TomletMain.TomlStringFrom(registry); _ = MarketplaceRegistry.Parse(text);
            string temporary = _registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, text, token).ConfigureAwait(false); File.Move(temporary, _registryPath, overwrite: true); }
            finally { File.Delete(temporary); }
        }
    }

    private async Task<bool> ConfirmPlanAsync(InstallPlan plan, string id, string? publisher, string source, CancellationToken token)
    {
        InstallationLock selection = InstallationLock.Parse(plan.Next);
        LockedPlugin plugin = selection.Plugins.Find(plugin => plugin.Id == id)!;
        PluginChange? change = plan.Changes.FirstOrDefault(change => change.Id == id);
        string[] also = plan.Changes.Where(change => change.Id != id).Select(change => change.Id + " " + change.Version + " [" + change.Target + "]").ToArray();
        string body = Text("operation.confirm", id, plugin.Release.Version, plugin.Asset.Kind, plugin.Asset.Target, string.Join(", ", also));
        PluginPlannedChange[] changes = plan.Changes.Select(item => new PluginPlannedChange(item.Id,
            selection.Plugins.Find(selected => selected.Id == item.Id)!.MarketplaceId,
            item.PreviousVersion, item.Version, item.Kind, item.Target, item.Sha256, item.Reason)).ToArray();
        body += "\n" + string.Join("\n", changes.Select(item => Text("operation.change", item.Id, item.Marketplace,
            item.PreviousVersion ?? Text("operation.new-selection"), item.Version, item.Kind, item.Target, item.Sha256)));
        return await Confirm(new(id, plugin.Release.Version, source, publisher, plugin.Asset.Kind == "source" ? PluginEntryKind.Source : PluginEntryKind.Compiled,
            0, [], Table(plugin.Release.Requires), change?.PreviousVersion is not null, change?.PreviousVersion, body, also, changes), token).ConfigureAwait(false);
    }
    private ResolutionRequest Request(string id, string? publisher, string? version)
        => new(id.ToLowerInvariant(), publisher, version is not null && !SemVer.TryParse(version, out _) ? version : "*",
            version is not null && SemVer.TryParse(version, out _) ? version : null, IncludePrerelease: IncludePrerelease, Assets: Assets);
    private static (string Id, string? Publisher) Identity(string what)
    {
        string[] parts = what.Split('@');
        if (parts.Length > 2 || !PluginManifest.IsUsableId(parts[0]) || (parts.Length == 2 && !PluginManifest.IsUsableId(parts[1])))
            throw new MarketplaceException("installation.development-source-required", what);
        return (parts[0].ToLowerInvariant(), parts.Length == 2 ? parts[1].ToLowerInvariant() : null);
    }
    private async Task<PluginActionResult> OperationAsync(Func<Task<PluginActionResult>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (MarketplaceException failure) { return new(false, Diagnostic(failure)); }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException or FormatException or InvalidOperationException)
        { return new(false, Text("operation.failed", exception.Message)); }
    }
    private string Diagnostic(MarketplaceException failure) => MarketplaceDiagnostics.Format(failure, Culture);
    private string Text(string key, params object?[] values) => MarketplaceDiagnostics.Text(key, Culture, values);
    private static string[] Table(Dictionary<string, string> table) => table.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + pair.Value).ToArray();
}

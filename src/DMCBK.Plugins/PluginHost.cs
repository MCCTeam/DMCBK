using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging;
using DMCBK.Core.Manual;

namespace DMCBK.PluginSdk;

/// <summary>
/// The plugin host: discovery from a <c>plugins/</c> root, <c>plugin.toml</c> parsing, compiled-<c>.dll</c> and single-file-<c>.cs</c> loading into a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, the api-version compatibility gate, and the settings pipeline.
/// It implements the core <see cref="IPluginHost"/> seam so the in-core <c>plugins</c> / <c>plugin load</c> / <c>reload</c> commands drive it.
/// Plugins run in-process with NO sandbox; only load plugins you trust.
/// <para>
/// The client-lifetime lifecycle (activate once, re-attach per session/reconnect, deactivate once on unload) runs through one <see cref="McPluginExtension"/> per loaded plugin on <see cref="Client.Extensions"/>: UMPK's own <see cref="Umpk.Client.ClientExtensionCollection"/> drives that plugin's activation, its per-session attach/detach, and its bounded-wait deactivation.
/// Each plugin is its own extension, so unloading one stops exactly that plugin without touching the others.
/// </para>
/// </summary>
public sealed partial class PluginHost : IPluginHost, IAsyncDisposable
{
    private readonly Client _client;
    private readonly DMCBK.Core.Commands.ICommandDispatcher _commands;
    private readonly string _pluginsRoot;

    /// <summary>
    /// The resolved UI culture: every plugin's <see cref="PluginLocalization"/>, its settings comments and its manual summary read from it.
    /// Mutable because <c>/lang &lt;tag&gt;</c> retargets it live through <see cref="UseCulture"/>; guarded by <see cref="_gate"/> on write.
    /// </summary>
    private CultureInfo _uiCulture;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Umpk.Text.ITranslationSource _translations;
    private readonly VariableStore _variables;
    private readonly ILogger _logger;
    private readonly PluginMessengerHub _messengerHub;
    private readonly PluginServiceHub _serviceHub;
    private readonly object _gate = new();
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginCrashMonitor _crashes;
    private Task _crashDisables = Task.CompletedTask;

    /// <summary>
    /// Builds the host over a client and a plugins root directory, and attaches itself as the client's plugin host (so the plugin commands reach it).
    /// Call <see cref="LoadAllAsync"/> before <see cref="Client.StartAsync"/>.
    /// </summary>
    /// <param name="client">The client the plugins are loaded into, and whose plugin host this becomes.</param>
    /// <param name="pluginsRoot">The folder plugins are discovered from and scaffolded into.</param>
    /// <param name="loggerFactory">Where the host and each plugin log.</param>
    /// <param name="translations">Minecraft's own translation table, for rendering what the server sent.</param>
    /// <param name="variables">The shared <c>%variable%</c> store.</param>
    /// <param name="uiCulture">
    /// The resolved UI culture, so every plugin's <c>lang/</c> table, settings comments and manual pages follow the configured language rather than the operating system's.
    /// Null means the ambient UI culture, which is what a host that has not resolved one yet would get anyway.
    /// </param>
    /// <param name="plugins">
    /// The <c>[Plugins]</c> section of <c>client.toml</c>: how often a plugin may throw before the host disables it.
    /// Null takes the defaults.
    /// </param>
    public PluginHost(
        Client client,
        string pluginsRoot,
        ILoggerFactory loggerFactory,
        Umpk.Text.ITranslationSource translations,
        VariableStore variables,
        CultureInfo? uiCulture = null,
        DMCBK.Core.Configuration.PluginsConfig? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(variables);

        _client = client;
        CompilationReferences = new CompilationReferenceProvider(AppContext.BaseDirectory,
            includeAspNetCore: client.AvailableCapabilities.Contains("aspnetcore"));
        _commands = client.Commands;
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _uiCulture = uiCulture ?? CultureInfo.CurrentUICulture;
        _loggerFactory = loggerFactory;
        _translations = translations;
        _variables = variables;
        _logger = loggerFactory.CreateLogger("Mcc.Plugins.Host");
        _messengerHub = new PluginMessengerHub(_logger) { IsExchangeable = IsExportedContract, CanCommunicate = CanCommunicate };
        _serviceHub = new PluginServiceHub(_logger) { IsExchangeable = IsExportedContract, CanCommunicate = CanCommunicate };

        DMCBK.Core.Configuration.PluginsConfig limits = plugins ?? new DMCBK.Core.Configuration.PluginsConfig();
        _crashes = new PluginCrashMonitor(limits.CrashThreshold, TimeSpan.FromSeconds(limits.CrashWindowSeconds));
        _commands.CommandFaulted += OnCommandFaulted;

        // Said once, here, rather than once per refused plugin: an engine with no readable version reads as 0.0.0, and every manifest that declares a `umpk` range would then be refused with no explanation of why the number is zero.
        UmpkVersionProbe.LogIfUnresolved(_logger);

        _client.AttachPluginHost(this);
    }

    private bool CanCommunicate(string first, string second)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(first, out LoadedPlugin? consumer) || !_plugins.TryGetValue(second, out LoadedPlugin? provider)) return true;
            return Admits(consumer, provider) && Admits(provider, consumer);
        }
        static bool Admits(LoadedPlugin consumer, LoadedPlugin provider)
            => !consumer.Manifest.Optional.TryGetValue(provider.Manifest.Id, out string? range)
                || SemVerRange.Parse(range).Satisfies(SemVer.Parse(provider.Manifest.Version), includePrerelease: true);
    }

    private Task? _disposal;

    /// <summary>Unloads plugins and releases all host registrations once.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposal ??= DisposeHostAsync());
    }

    private async Task DisposeHostAsync()
    {
        _commands.CommandFaulted -= OnCommandFaulted;
        await _crashDisables.ConfigureAwait(false);
        LoadedPlugin[] records;
        lock (_gate) records = _plugins.Values.ToArray();
        List<Exception>? failures = null;
        foreach (LoadedPlugin record in InDependencyOrder(records).Reverse())
        {
            try { await UnloadRecordAsync(record).ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        lock (_gate) _plugins.Clear();
        if (failures is not null) throw new AggregateException(failures);
    }

    /// <summary>
    /// How long <see cref="IPlugin.DeactivateAsync"/> is given before the host cancels its token, logs a warning naming the plugin, and continues the unload anyway.
    /// Long enough for a network client to close politely; short enough that a wedged plugin cannot hold <c>unload</c> or <c>reload</c> hostage.
    /// </summary>
    public TimeSpan TeardownTimeout { get; internal set; } = TimeSpan.FromSeconds(5);

    /// <summary>The resolved plugins root directory.</summary>
    public string PluginsRoot => _pluginsRoot;

    /// <summary>Explicit managed reference assets used by source plugins.</summary>
    public ICompilationReferenceProvider CompilationReferences { get; set; } = new CompilationReferenceProvider(AppContext.BaseDirectory);

    /// <summary>The client this host serves, so the market can attach itself to it the way this host does.</summary>
    internal Client Client => _client;

    /// <summary>
    /// Discovers and loads every enabled plugin under the plugins root.
    /// Safe to call when the folder is missing (it loads nothing).
    /// Returns a summary of how many of the discovered plugins loaded.
    /// </summary>
    public async Task<PluginActionResult> LoadAllAsync(CancellationToken ct = default)
    {
        if (InstallationSource is not null)
        {
            IReadOnlyList<PluginInstallation> graph = await InstallationSource(ct).ConfigureAwait(false);
            try { await ActivateCoreAsync(graph, strict: false, ct).ConfigureAwait(false); }
            catch (InvalidOperationException exception) { return new PluginActionResult(false, exception.Message); }
        }
        if (!Directory.Exists(_pluginsRoot))
        {
            _logger.LogInformation("No plugins directory at {Root}; skipping plugin load.", _pluginsRoot);
            return new PluginActionResult(true, PluginStrings.LoadedCount(0, 0));
        }

        _logger.LogInformation("{Notice}", PluginStrings.InProcessNotice);

        // Register every discovered plugin first, then load in dependency order.
        // The [requires] gate has to tell "not installed" from "not loaded yet", which it cannot do while records appear one at a time.
        var records = new List<LoadedPlugin>();
        foreach (string folder in DevelopmentPluginFolders)
            if (TryResolveEntry(folder, out DiscoveredPlugin discovered, out _)) records.Add(RegisterRecord(discovered));

        int loaded = 0;
        int total = records.Count;
        foreach (LoadedPlugin record in InDependencyOrder(records))
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Enabled)
            {
                record.Status = PluginStrings.StateDisabled;
                continue;
            }

            PluginActionResult result = await LoadRecordAsync(record, ct).ConfigureAwait(false);
            if (result.Success)
                loaded++;
        }

        return new PluginActionResult(true, PluginStrings.LoadedCount(List().Count(plugin => plugin.Loaded), List().Count));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginInfo> List()
    {
        lock (_gate)
        {
            var list = new List<PluginInfo>(_plugins.Count);
            foreach (LoadedPlugin record in _plugins.Values)
                list.Add(record.ToInfo(StateOf(record)));

            list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }

    /// <inheritdoc/>
    public async Task<PluginActionResult> EnableAsync(string id, CancellationToken ct = default)
    {
        if (_managedIds.Contains(id) && ChangeEnabled is not null)
            return await ChangeEnabled(id, true, ct).ConfigureAwait(false);
        LoadedPlugin? record = Find(id);
        if (record is null)
            return new PluginActionResult(false, PluginStrings.NotFound(id));

        if (record.Loaded)
        {
            PersistEnabled(record, true);
            return new PluginActionResult(true, PluginStrings.AlreadyLoaded(id));
        }

        record.Enabled = true;
        PersistEnabled(record, true);
        return await LoadRecordAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PluginActionResult> DisableAsync(string id, CancellationToken ct = default)
    {
        if (_managedIds.Contains(id) && ChangeEnabled is not null)
            return await ChangeEnabled(id, false, ct).ConfigureAwait(false);
        LoadedPlugin? record = Find(id);
        if (record is null)
            return new PluginActionResult(false, PluginStrings.NotFound(id));

        if (!record.Loaded)
        {
            record.Enabled = false;
            PersistEnabled(record, false);
            return new PluginActionResult(true, PluginStrings.AlreadyDisabled(id));
        }

        if (LoadedDependentsOf(record.Manifest.Id) is { Count: > 0 } dependents)
        {
            return new PluginActionResult(
                false, PluginStrings.DependentsLoadedDisable(record.Manifest.Id, dependents));
        }

        await UnloadRecordAsync(record).ConfigureAwait(false);
        record.Enabled = false;
        record.Status = PluginStrings.StateDisabled;
        PersistEnabled(record, false);
        return new PluginActionResult(true, PluginStrings.Disabled(id));
    }

    /// <inheritdoc/>
    public async Task<PluginActionResult> UnloadAsync(string id, CancellationToken ct = default)
    {
        LoadedPlugin? record = Find(id);
        if (record is null)
            return new PluginActionResult(false, PluginStrings.NotFound(id));

        if (!record.Loaded)
            return new PluginActionResult(true, PluginStrings.AlreadyDisabled(id));

        if (LoadedDependentsOf(record.Manifest.Id) is { Count: > 0 } dependents)
        {
            return new PluginActionResult(
                false, PluginStrings.DependentsLoadedUnload(record.Manifest.Id, dependents));
        }

        await UnloadRecordAsync(record).ConfigureAwait(false);
        record.Status = PluginStrings.StateDiscovered;
        return new PluginActionResult(true, PluginStrings.Unloaded(id));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reloading cascades: a plugin's loaded dependents are reloaded after it, in dependency order.
    /// A dependent linked against the exporter's assemblies holds types out of the load context this reload just unloaded, so leaving it running would leave it pointing at a dead context.
    /// Reloading an exporter usually means its code changed, which its dependents have to pick up anyway.
    /// </remarks>
    public async Task<PluginActionResult> ReloadAsync(string id, CancellationToken ct = default)
    {
        LoadedPlugin? record = Find(id);
        if (record is null)
            return new PluginActionResult(false, PluginStrings.NotFound(id));

        // Snapshot the dependents before the unload.
        // The cascade below is what unloads them, and this list decides its order.
        IReadOnlyList<string> dependents = LoadedDependentsOf(record.Manifest.Id);

        PluginActionResult result = await ReloadOneAsync(record, ct).ConfigureAwait(false);
        if (!result.Success || dependents.Count == 0)
            return result;

        // Dependents-first is the unload order.
        // Reloading walks it backwards, so the plugin nearest the reloaded one comes back first and its own dependents find it already loaded.
        var reloaded = new List<string>(dependents.Count);
        for (int i = dependents.Count - 1; i >= 0; i--)
        {
            if (Find(dependents[i]) is not { } dependent)
                continue;

            PluginActionResult cascade = await ReloadOneAsync(dependent, ct).ConfigureAwait(false);
            if (cascade.Success)
                reloaded.Add(dependents[i]);
        }

        return reloaded.Count == 0
            ? result
            : new PluginActionResult(true, PluginStrings.ReloadedCascade(record.Manifest.Id, reloaded));
    }

    /// <summary>
    /// Unloads a plugin and every loaded plugin that requires it, dependents first, and returns their ids in that order.
    /// Unlike <see cref="UnloadAsync"/> this does not refuse, because the caller is REPLACING the plugin rather than removing it and is expected to bring the dependents back with <see cref="ReloadDependentsAsync"/>.
    /// It is the same cascade <see cref="ReloadAsync"/> performs, split in two so a folder swap fits between the halves.
    /// </summary>
    internal async Task<IReadOnlyList<string>> UnloadForSwapAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } record)
            return [];

        IReadOnlyList<string> dependents = LoadedDependentsOf(record.Manifest.Id);
        foreach (string dependent in dependents)
        {
            if (Find(dependent) is { Loaded: true } loaded)
            {
                await UnloadRecordAsync(loaded).ConfigureAwait(false);
                loaded.Status = PluginStrings.StateDiscovered;
            }
        }

        if (record.Loaded)
        {
            await UnloadRecordAsync(record).ConfigureAwait(false);
            record.Status = PluginStrings.StateDiscovered;
        }

        return dependents;
    }

    /// <summary>
    /// Loads back what <see cref="UnloadForSwapAsync"/> unloaded, walking its list backwards so the plugin nearest the swapped one comes back first.
    /// Returns the ids that came back.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ReloadDependentsAsync(
        IReadOnlyList<string> dependents, CancellationToken ct)
    {
        var back = new List<string>(dependents.Count);
        for (int i = dependents.Count - 1; i >= 0; i--)
        {
            if (Find(dependents[i]) is not { } dependent)
                continue;

            if ((await ReloadOneAsync(dependent, ct).ConfigureAwait(false)).Success)
                back.Add(dependents[i]);
        }

        return back;
    }

    /// <summary>Unload-then-load of one record, with the manifest re-read in between. The reload primitive.</summary>
    private async Task<PluginActionResult> ReloadOneAsync(LoadedPlugin record, CancellationToken ct)
    {
        if (record.Loaded)
            await UnloadRecordAsync(record).ConfigureAwait(false);

        // Re-parse the manifest so a config edit is picked up (folder plugins only).
        RefreshManifest(record);
        record.Enabled = true;
        PluginActionResult result = await LoadRecordAsync(record, ct).ConfigureAwait(false);
        return result.Success
            ? new PluginActionResult(true, PluginStrings.Reloaded(record.Manifest.Id))
            : result;
    }

    /// <inheritdoc/>
    public async Task<PluginActionResult> LoadAsync(string folderOrEntry, CancellationToken ct = default)
    {
        if (!TryResolveEntry(folderOrEntry, out DiscoveredPlugin discovered, out string? error))
            return new PluginActionResult(false, error!);

        LoadedPlugin? existing = Find(discovered.Manifest.Id);
        if (existing is { Loaded: true })
            return new PluginActionResult(false, PluginStrings.AlreadyLoaded(discovered.Manifest.Id));

        LoadedPlugin record = RegisterRecord(discovered);
        record.Enabled = true;
        return await LoadRecordAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PluginActionResult> ReloadAllAsync(CancellationToken ct = default)
    {
        LoadedPlugin[] records;
        lock (_gate)
            records = [.. _plugins.Values];

        if (records.Length == 0)
            return new PluginActionResult(true, PluginStrings.ReloadedNone);

        int reloaded = 0;
        foreach (LoadedPlugin record in InDependencyOrder(records))
        {
            ct.ThrowIfCancellationRequested();
            if (record.Loaded)
                await UnloadRecordAsync(record).ConfigureAwait(false);

            RefreshManifest(record);
            if (!record.Enabled)
                continue;

            PluginActionResult result = await LoadRecordAsync(record, ct).ConfigureAwait(false);
            if (result.Success)
                reloaded++;
        }

        return new PluginActionResult(true, PluginStrings.ReloadedAll(reloaded));
    }

    /// <inheritdoc/>
    public Task<PluginActionResult> RegenerateSettingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(RewriteSettings(id, reset: false));

    /// <inheritdoc/>
    public Task<PluginActionResult> ResetSettingsAsync(string id, CancellationToken ct = default)
        => Task.FromResult(RewriteSettings(id, reset: true));

    /// <inheritdoc/>
    public IReadOnlyList<PluginLanguageInfo> Languages()
    {
        LoadedPlugin[] records;
        lock (_gate)
            records = [.. _plugins.Values];

        var list = new List<PluginLanguageInfo>(records.Length);
        foreach (LoadedPlugin record in records)
        {
            if (record.Localization is not { } localization)
                continue;

            PluginLocalizationReport report = localization.Report(_uiCulture);
            list.Add(new PluginLanguageInfo(
                record.Manifest.Id, report.Languages, report.Active, report.MissingKeys.Count));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <inheritdoc/>
    public PluginDependencyInfo? DependenciesOf(string id)
    {
        if (Find(id) is not { } record)
            return null;

        var missing = new List<string>();
        foreach (string required in record.Manifest.RequiredRanges.Keys)
        {
            if (Find(required) is null)
                missing.Add(required);
        }

        List<PluginManifest> manifests = SnapshotManifests();
        return new PluginDependencyInfo(
            record.Manifest.Id,
            [.. record.Manifest.RequiredRanges.Select(entry => $"{entry.Key} {entry.Value.Text}")],
            [.. record.Manifest.OptionalRanges.Select(entry => $"{entry.Key} {entry.Value.Text}")],
            PluginDependencyGraph.DependentsOf(record.Manifest.Id, manifests),
            missing);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginCheckResult> Validate(string? folder = null)
    {
        if (folder is { Length: > 0 })
        {
            string target = Path.IsPathRooted(folder) ? folder : Path.Combine(_pluginsRoot, folder);
            return [ToCheck(PluginValidator.Validate(target))];
        }

        var results = new List<PluginCheckResult>();
        foreach (string candidate in PluginFolders())
            results.Add(ToCheck(PluginValidator.Validate(candidate)));

        return results;
    }

    /// <inheritdoc/>
    public PluginActionResult Scaffold(string id)
    {
        if (!PluginScaffold.TryCreate(_pluginsRoot, id, out PluginScaffoldResult scaffold, out string? error))
            return new PluginActionResult(false, error ?? PluginStrings.ScaffoldFailed(id ?? string.Empty, "unknown"));

        if (TryResolveEntry(scaffold.Folder, out DiscoveredPlugin discovered, out _))
            RegisterRecord(discovered).Enabled = false;

        // Not logged as well as returned: the command that asked for this prints the result, and the log would put the same four-line block on screen twice.
        return new PluginActionResult(true, PluginStrings.Scaffolded(scaffold.Id, scaffold.Folder, scaffold.Files));
    }

    /// <inheritdoc/>
    public PluginDoctorReport Doctor()
    {
        LoadedPlugin[] records = [.. Records().OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)];
        var notes = new List<PluginDoctorNote>();

        IEnumerable<string> folders = Directory.Exists(_pluginsRoot)
            ? Directory.EnumerateDirectories(_pluginsRoot).Order(StringComparer.Ordinal)
            : [];
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (!name.StartsWith('.') && name is not ("versions" or "userdata" or "cache" or "transactions") && !File.Exists(Path.Combine(folder, PluginManifest.FileName)))
                notes.Add(new PluginDoctorNote(name, PluginStrings.DoctorNoManifest(name), false));
        }

        foreach (LoadedPlugin record in records)
            InspectPlugin(record, notes);

        return new PluginDoctorReport(records.Length, records.Count(r => r.Loaded), notes);
    }

    private void InspectPlugin(
        LoadedPlugin record, List<PluginDoctorNote> notes)
    {
        string id = record.Manifest.Id;
        string? refusal = null;
        if (!record.Loaded)
        {
            refusal = TryGateVersions(record, out string? gate) ? null : gate;
            refusal ??= record.Enabled && IsRefusal(record.Status) ? record.Status : null;
        }

        if (refusal is { Length: > 0 })
            // Verbatim: the loader already said why, in the words the listing uses.
            notes.Add(new PluginDoctorNote(id, refusal, true));
        else if (DependenciesOf(id) is { Missing.Count: > 0 } deps)
            notes.Add(new PluginDoctorNote(id, PluginStrings.DoctorMissingDependencies(deps.Missing), true));

        if (record.Localization is { } localization && !IsEnglish(_uiCulture))
        {
            PluginLocalizationReport report = localization.Report(_uiCulture);
            if (report.Active is null)
                notes.Add(new PluginDoctorNote(id, PluginStrings.DoctorLangNone(_uiCulture.Name), false));
            else if (report.MissingKeys.Count > 0)
            {
                notes.Add(new PluginDoctorNote(
                    id, PluginStrings.DoctorLangBehind(report.Active, report.MissingKeys.Count), false));
            }
        }

        (int stale, string cacheFolder) = CsPluginCompiler.StaleOutputs(
            Path.Combine(_pluginsRoot, "cache", "source", record.Manifest.Id), record.EntryKind == PluginEntryKind.Source);
        if (stale > 0)
            notes.Add(new PluginDoctorNote(id, PluginStrings.DoctorStaleCache(stale, cacheFolder), false));

        if (record.FaultCount > 0)
        {
            string last = record.LastFault?.Describe() ?? string.Empty;
            notes.Add(record.CrashDisabled
                ? new PluginDoctorNote(id, PluginStrings.DoctorDisabledByCrash(last), true)
                : new PluginDoctorNote(id, PluginStrings.DoctorFaults(record.FaultCount, last), false));
        }
    }

    /// <summary>
    /// Counts one exception a plugin threw, keeps it for <c>info</c> and <c>doctor</c>, and disables the plugin once it has thrown too often inside the window.
    /// The notification falls back to the log, because the unattended bot is the case this exists for and it has no host UI to tell.
    /// </summary>
    internal void ReportFault(string id, PluginFaultSurface surface, string? detail, Exception ex)
    {
        var fault = new PluginFault(surface, detail, $"{ex.GetType().Name}: {ex.Message}", DateTimeOffset.UtcNow);
        LoadedPlugin? record = Find(id);
        if (record is null)
            return;

        lock (_gate)
        {
            record.LastFault = fault;
            record.FaultCount++;
        }

        _logger.LogWarning(ex, "{Message}", PluginStrings.PluginThrew(id, fault.Describe()));

        (int count, bool overBudget) = _crashes.Record(id);
        if (!overBudget)
            return;

        _crashes.Forget(id);
        record.CrashDisabled = true;
        string message = PluginStrings.CrashDisabled(id, count, (int)_crashes.Window.TotalSeconds, fault.Describe());
        Notify(message, ex);

        // The fault arrives inside the plugin's own handler, so the disable cannot be awaited here.
        // It is kept instead of dropped, so a caller can wait for it and a failure is logged rather than lost.
        Task disable = Task.Run(async () =>
        {
            try
            {
                await DisableAsync(id).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _logger.LogWarning(failure, "{Message}", PluginStrings.PluginThrew(id, fault.Describe()));
            }
        });

        lock (_gate)
            _crashDisables = _crashDisables.IsCompleted ? disable : Task.WhenAll(_crashDisables, disable);
    }

    /// <summary>Completes when every disable a crash budget triggered has finished.</summary>
    internal Task CrashDisablesAsync()
    {
        lock (_gate)
            return _crashDisables;
    }

    private void Notify(string message, Exception ex)
    {
        if (_client.HostUi is not { } ui || !ui.TryNotify(new HostNotification(message)))
            _logger.LogWarning(ex, "{Message}", message);
    }

    /// <summary>
    /// Attributes a faulted command to the plugin that registered it.
    /// A command the host itself owns has no plugin behind it and is left alone.
    /// </summary>
    private void OnCommandFaulted(object? sender, CommandFaultedEventArgs e)
    {
        if (e.Command is null)
            return;

        foreach (LoadedPlugin record in Records())
        {
            if (!record.Loaded || record.Context is not { } context)
                continue;

            bool owns = (context.Commands as PluginCommandScope)?.Owns(e.Command) == true
                || (context.CurrentSession as SessionScope)?.CommandScope.Owns(e.Command) == true;
            if (owns)
            {
                ReportFault(record.Manifest.Id, PluginFaultSurface.Command, e.Command.CmdName, e.Exception);
                return;
            }
        }
    }

    private static bool IsEnglish(CultureInfo culture)
        => culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);

    private static bool IsRefusal(string? status)
        => status is { Length: > 0 }
            && status != PluginStrings.StateDiscovered
            && status != PluginStrings.StateDisabled
            && status != PluginStrings.StateLoaded;

    private static PluginCheckResult ToCheck(PluginValidationReport report) => new(
        report.Id ?? Path.GetFileName(report.Folder),
        report.Folder,
        [.. report.Problems.Select(p => new PluginCheckProblem(
            p.Severity == PluginValidationSeverity.Error, p.File, p.Line, p.Message))]);

    /// <summary>Every folder under the plugins root that carries a manifest, whether or not it loaded.</summary>
    private IReadOnlyList<string> PluginFolders()
    {
        if (!Directory.Exists(_pluginsRoot))
            return [];

        return
        [
            .. Directory.EnumerateDirectories(_pluginsRoot)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Where(f => File.Exists(Path.Combine(f, PluginManifest.FileName)))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc/>
    public void UseCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        lock (_gate)
        {
            _uiCulture = culture;
            foreach (LoadedPlugin record in _plugins.Values)
            {
                if (record.Localization is { } localization)
                    localization.Culture = culture;
            }
        }
    }

    /// <summary>
    /// The shared body of <c>settings regen</c> and <c>settings reset</c>.
    /// Both go through <see cref="PluginSettings.Load{T}"/>, which writes the file when it is absent, so reset is exactly regen with the old file deleted first.
    /// </summary>
    private PluginActionResult RewriteSettings(string id, bool reset)
    {
        LoadedPlugin? record = Find(id);
        if (record is null)
            return new PluginActionResult(false, PluginStrings.NotFound(id));

        PluginSettings? settings;
        Type? settingsType;
        lock (_gate)
        {
            settings = record.Settings;
            settingsType = record.SettingsType;
        }

        if (settings is null)
            return new PluginActionResult(false, PluginStrings.SettingsNotLoaded(id));

        if (settingsType is null)
            return new PluginActionResult(false, PluginStrings.SettingsNone(id));

        try
        {
            if (reset && File.Exists(settings.FilePath))
                File.Delete(settings.FilePath);

            // Load materializes defaults and writes the file when it is missing; when it is present it deserializes, validates, and hands the object back, and the explicit Save is what puts the regenerated comments on disk.
            // One path for both verbs.
            MethodInfo load = typeof(PluginSettings).GetMethod(nameof(PluginSettings.Load))!.MakeGenericMethod(settingsType);
            object? value = load.Invoke(settings, null);
            if (value is not null)
            {
                MethodInfo save = typeof(PluginSettings).GetMethod(nameof(PluginSettings.Save))!.MakeGenericMethod(settingsType);
                save.Invoke(settings, [value]);
            }

            string message = reset ? PluginStrings.SettingsReset(id) : PluginStrings.SettingsRegenerated(id);
            _logger.LogInformation("{Message}", message);
            return new PluginActionResult(true, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = PluginStrings.SettingsFailed(id, (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
            _logger.LogWarning(ex, "{Message}", message);
            return new PluginActionResult(false, message);
        }
    }

    /// <summary>Returns the weak handle to a plugin's collectible load context (unit-test unload proof).</summary>
    internal WeakReference? GetLoadContextRef(string id) => Find(id)?.LoadContextRef;

    /// <summary>Returns a loaded plugin's context, or null when it is not currently loaded (test seam).</summary>
    internal PluginContext? GetContext(string id) => Find(id)?.Context;

    /// <summary>The record for one discovered plugin, or null. The market reads manifests and folders here.</summary>
    internal LoadedPlugin? FindRecord(string id) => Find(id);

    /// <summary>Every discovered record, snapshotted. The market walks it to find managed plugins.</summary>
    internal IReadOnlyList<LoadedPlugin> Records()
    {
        lock (_gate)
            return [.. _plugins.Values];
    }

    /// <summary>
    /// Drops an unloaded plugin's record, so an uninstalled folder stops appearing in the listing.
    /// Refuses while the plugin is loaded: forgetting it would strand its extension, its commands and its load context with nothing holding the handle that releases them.
    /// </summary>
    internal bool Forget(string id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out LoadedPlugin? record)
                && !record.Loaded
                && _plugins.Remove(id);
        }
    }

    /// <summary>
    /// Re-reads one folder's manifest into the host's records after the installer swapped it, without loading it.
    /// A fresh install has no record at all, an update has one holding the old manifest.
    /// </summary>
    internal bool TryRegisterFolder(string folder, out string? error)
    {
        if (!TryResolveEntry(folder, out DiscoveredPlugin discovered, out error))
            return false;

        LoadedPlugin record = RegisterRecord(discovered);
        RefreshManifest(record);
        return true;
    }

    #region internals

    private async Task<PluginActionResult> LoadRecordAsync(LoadedPlugin record, CancellationToken ct)
    {
        string id = record.Manifest.Id;
        if (!TryGateVersions(record, out string? refusal) || !TryGateDependencies(record, out refusal))
        {
            record.Loaded = false;
            record.Status = refusal;
            _logger.LogWarning("{Message}", record.Status);
            return new PluginActionResult(false, record.Status);
        }

        lock (_gate)
        {
            record.FaultCount = 0;
            record.LastFault = null;
            record.CrashDisabled = false;
        }

        try
        {
            (IPlugin instance, System.Runtime.Loader.AssemblyLoadContext alc, WeakReference alcRef,
                IReadOnlyList<PluginExportedAssembly> exports) = InstantiatePlugin(record);
            lock (_gate) { record.LoadContext = alc; record.LoadContextRef = alcRef; }

            // The short form for single-file plugins: attributed static methods become the same records as explicit registration.
            // Scanned before activation so a bad signature fails this load fast, naming the plugin plus the signature, never the first call at night.
            IReadOnlyList<BeaconFunction> scannedFunctions =
                BeaconFunctionScanner.Scan(instance.GetType().Assembly, id);

            var descriptor = new PluginDescriptor
            {
                Id = id,
                Version = record.Manifest.Version,
                ApiVersion = PluginApiVersion.TryParseMajor(record.Manifest.ApiVersion, out int major) ? major : PluginApiVersion.Major,
            };
            instance.Configure(descriptor);

            ILogger logger = _loggerFactory.CreateLogger($"Plugin.{id}");

            // The plugin's own translated strings, and its manual pages.
            // Both are per-language files in the plugin folder, because a plugin cannot add to MCC's generated resx corpus: strings in lang/<language>.toml, pages in man/<language>/<topic>.md. Same fallback rules as the corpus.
            // Built BEFORE the settings pipeline, not after, because the settings file's $key$ comments are resolved out of this table: generating settings.toml first would freeze them in English.
            var localization = new PluginLocalization(record.Folder, _uiCulture);
            var settings = new PluginSettings(Path.Combine(record.UserDirectory ?? Path.Combine(_pluginsRoot, "userdata", id), "settings.toml"), logger, localization, Path.Combine(record.Folder, "defaults", "settings.toml"));
            var storage = new PluginStorage(Path.Combine(record.UserDirectory ?? Path.Combine(_pluginsRoot, "userdata", id), "data"), logger);
            var messenger = new PluginMessenger(_messengerHub, id);
            var services = new PluginServices(_serviceHub, id);

            RunSettingsPipeline(descriptor, settings, logger);

            IDisposable? manualRegistration = RegisterManualPages(record, logger);
            lock (_gate)
            {
                record.ManualRegistration = manualRegistration; record.Messenger = messenger; record.Services = services;
                record.Storage = storage; record.Settings = settings; record.Localization = localization;
            }

            // Published before activation, and again with the rest of the record below.
            // ActivateAsync is where a plugin subscribes and registers services, and where the "nobody can reach this contract" warning is decided.
            // An exporter whose exports were still unpublished would warn about the very types it is exporting.
            lock (_gate)
                record.Exports = exports;

            // One UMPK extension per plugin: it builds the plugin's PluginContext (Cron sourced from the extension's own per-plugin scheduler), calls ActivateAsync, and forwards session attach/detach for as long as the plugin stays registered, across every reconnect.
            var extension = new McPluginExtension(
                id, _client, instance, settings, storage, logger, _logger, _translations,
                new ChatClassifier(_translations), messenger, services, localization, _variables,
                TeardownTimeout);

            try { await _client.Extensions.AddAsync(extension, ct).ConfigureAwait(false); }
            finally { lock (_gate) record.Context = extension.Context; }

            lock (_gate)
            {
                record.Instance = instance;
                record.Context = extension.Context;
                record.ManualRegistration = manualRegistration;
                if (extension.Context is { } context)
                {
                    context.Strings = localization;
                    context.Faults = (surface, detail, ex) => ReportFault(id, surface, detail, ex);
                    if (context.Beacon is PluginBeaconHost beacon)
                    {
                        foreach (BeaconFunction scanned in scannedFunctions)
                            beacon.AddScanned(scanned);
                    }
                }
                record.Messenger = messenger;
                record.Services = services;
                record.Exports = exports;
                record.Storage = storage;
                record.Localization = localization;
                record.Settings = settings;
                record.SettingsType = descriptor.SettingsType;
                record.LoadContext = alc;
                record.LoadContextRef = alcRef;
                record.Loaded = true;
                record.Status = PluginStrings.StateLoaded;
            }

            _logger.LogInformation("{Message}", PluginStrings.Loaded(id));
            return new PluginActionResult(true, PluginStrings.Loaded(id));
        }
        catch (PluginCompileException ex)
        {
            record.Loaded = false;
            record.Status = PluginStrings.StateFailed;
            string message = PluginStrings.CompileFailed(id, ex.Message);
            _logger.LogError("{Message}", message);
            return new PluginActionResult(false, message);
        }
        catch (Exception ex)
        {
            record.Loaded = false;
            record.Status = PluginStrings.StateFailed;
            string message = PluginStrings.LoadFailed(id, ex.Message);
            _logger.LogError(ex, "{Message}", message);
            return new PluginActionResult(false, message);
        }
        finally
        {
            if (!record.Loaded) await UnloadRecordAsync(record).ConfigureAwait(false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (IPlugin Instance, System.Runtime.Loader.AssemblyLoadContext Context, WeakReference Ref,
        IReadOnlyList<PluginExportedAssembly> Exports) InstantiatePlugin(LoadedPlugin record)
    {
        string entryPath = Path.Combine(record.Folder, record.Manifest.Entry);
        if (!File.Exists(entryPath))
            throw new FileNotFoundException(PluginStrings.EntryMissing(record.Manifest.Entry), entryPath);

        List<string> deps = ResolveDeps(record);

        // What this plugin's [requires] export: references for the compile, delegation targets for the load context.
        // That is what makes a type declared in a required plugin one type on both sides.
        IReadOnlyList<PluginExportedAssembly> imported = ImportedExports(record);

        Assembly assembly;
        System.Runtime.Loader.AssemblyLoadContext alc;
        byte[]? image = null;

        PluginAssemblyPolicy.ValidatePackage(record.Folder, record.Manifest.Needs.Contains("aspnetcore"));
        if (record.Manifest.IsSourceEntry)
        {
            CsPluginCompiler.CompileResult compiled = CsPluginCompiler.Compile(entryPath, deps, imported,
                CompilationReferences, Path.Combine(_pluginsRoot, "cache", "source", record.Manifest.Id));
            if (!compiled.Success)
                throw new PluginCompileException(compiled.Error ?? "unknown compile error");

            image = compiled.Assembly!;
            alc = new CollectiblePluginLoadContext(
                $"plugin:{record.Manifest.Id}", entryAssemblyPath: null, deps, imported, record.Manifest.Needs.Contains("aspnetcore"));
            using var stream = new MemoryStream(image);
            try { assembly = alc.LoadFromStream(stream); }
            catch { alc.Unload(); throw; }
        }
        else
        {
            alc = new CollectiblePluginLoadContext($"plugin:{record.Manifest.Id}", entryPath, deps, imported, record.Manifest.Needs.Contains("aspnetcore"));
            try { assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(entryPath)); }
            catch { alc.Unload(); throw; }
        }

        try
        {
            Type pluginType = FindPluginType(assembly)
                ?? throw new InvalidOperationException(PluginStrings.NoPluginType(record.Manifest.Entry));
            var instance = (IPlugin)Activator.CreateInstance(pluginType)!;
            return (instance, alc, new WeakReference(alc), ResolveExports(record, alc, assembly, entryPath, image));
        }
        catch { alc.Unload(); throw; }
    }

    /// <summary>
    /// True when every plugin-private part of <paramref name="type"/> comes from an assembly some loaded plugin exports.
    /// A dependent resolves such a type in the exporter's context instead of compiling a second copy, so both sides see one runtime type.
    /// Without this the "nobody else can reach this contract" warning fires on the setup <c>[exports]</c> exists to make work.
    /// </summary>
    internal bool IsExportedContract(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (Type part in ContractParts(type))
        {
            if (!PluginContract.IsShared(part) && !IsExportedAssembly(part))
                return false;
        }

        return true;
    }

    /// <summary>Every type whose assembly decides the contract: elements, generic arguments, the type.</summary>
    private static IEnumerable<Type> ContractParts(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (Type inner in ContractParts(element))
                yield return inner;

            yield break;
        }

        if (type.IsGenericType)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in ContractParts(argument))
                    yield return inner;
            }

            yield break;
        }

        yield return type;
    }

    /// <summary>Whether this exact assembly, in this exact load context, is on some plugin's export list.</summary>
    private bool IsExportedAssembly(Type type)
    {
        System.Runtime.Loader.AssemblyLoadContext? context =
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly);
        if (context is null)
            return false;

        string name = type.Assembly.GetName().Name ?? string.Empty;

        // Called from a plugin's ActivateAsync, on whatever thread the extension host used, so the record table is read under the same gate every other reader takes.
        lock (_gate)
        {
            foreach (LoadedPlugin record in _plugins.Values)
            {
                foreach (PluginExportedAssembly export in record.Exports)
                {
                    if (ReferenceEquals(export.Owner, context)
                        && string.Equals(export.Name, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The exports of every plugin in this one's <c>[requires]</c>.
    /// The dependency gate has already proven each is loaded, so anything missing here is a plugin that exports nothing, which is the default.
    /// </summary>
    private IReadOnlyList<PluginExportedAssembly> ImportedExports(LoadedPlugin record)
    {
        if (record.Manifest.RequiredRanges.Count == 0)
            return [];

        var imported = new List<PluginExportedAssembly>();
        foreach (string dependency in record.Manifest.RequiredRanges.Keys)
        {
            if (Find(dependency) is { Loaded: true } other)
                imported.AddRange(other.Exports);
        }

        return imported;
    }

    /// <summary>
    /// Turns this plugin's <c>[exports] assemblies</c> into resolvable exports.
    /// <c>"entry"</c> is the assembly just loaded, the only thing a single-file plugin can export; any other value is a file name in the plugin folder.
    /// A named file that is not there is a warning, not a failed load: the plugin itself is fine, its dependents are the ones that will notice.
    /// </summary>
    private IReadOnlyList<PluginExportedAssembly> ResolveExports(
        LoadedPlugin record,
        System.Runtime.Loader.AssemblyLoadContext alc,
        Assembly entryAssembly,
        string entryPath,
        byte[]? entryImage)
    {
        List<string> declared = record.Manifest.Exports.Assemblies;
        if (declared.Count == 0)
            return [];

        var exports = new List<PluginExportedAssembly>(declared.Count);
        foreach (string declaredName in declared)
        {
            string name = declaredName.Trim();
            if (name.Length == 0)
                continue;

            if (string.Equals(name, PluginExports.EntryToken, StringComparison.OrdinalIgnoreCase))
            {
                exports.Add(new PluginExportedAssembly(
                    entryAssembly.GetName().Name ?? record.Manifest.Id,
                    entryImage is null ? Path.GetFullPath(entryPath) : null,
                    entryImage,
                    alc,
                    record.Manifest.Id));
                continue;
            }

            string path = Path.GetFullPath(Path.Combine(record.Folder, name));
            if (!File.Exists(path))
            {
                _logger.LogWarning("{Message}", PluginStrings.ExportMissing(record.Manifest.Id, name));
                continue;
            }

            exports.Add(new PluginExportedAssembly(
                Path.GetFileNameWithoutExtension(path), path, null, alc, record.Manifest.Id));
        }

        return exports;
    }

    private void RunSettingsPipeline(PluginDescriptor descriptor, PluginSettings settings, ILogger logger)
    {
        if (descriptor.SettingsType is not { } settingsType)
            return;

        try
        {
            MethodInfo load = typeof(PluginSettings).GetMethod(nameof(PluginSettings.Load))!.MakeGenericMethod(settingsType);
            load.Invoke(settings, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The declared settings type '{Type}' could not be materialized.", settingsType.Name);
        }
    }

    /// <summary>
    /// Tears a loaded plugin down by removing its extension from <see cref="Client.Extensions"/>: UMPK ends its live session first (so the plugin releases its per-session holdings via <see cref="PluginContext.DetachSession"/>), then runs <see cref="McPluginExtension.DeactivateAsync"/> (the plugin's own <see cref="IPlugin.DeactivateAsync"/> under a bounded wait) WHILE ITS CODE IS STILL LOADED, then this drops the host-owned registrations, flushes storage, and only then unloads the collectible context.
    /// Teardown running after the unload would call into assemblies that are already gone.
    /// </summary>
    /// <summary>
    /// Registers a plugin's manual pages, if it declares any, so <c>/man &lt;topic&gt;</c> reaches them.
    /// Returns the handle that removes them again on unload, or null when the plugin ships none.
    /// </summary>
    /// <remarks>
    /// Pages live at <c>man/&lt;language&gt;/&lt;topic&gt;.md</c> inside the plugin folder, with a flat <c>man/&lt;topic&gt;.md</c> accepted as the English-only form.
    /// That is the same layout MCC's own pages use before the generator folds them into the corpus, so translating a plugin's manual is the same job as translating MCC's: copy the folder, translate the files, ship them alongside.
    /// </remarks>
    private IDisposable? RegisterManualPages(LoadedPlugin record, ILogger logger)
    {
        if (record.Manifest.Man.Count == 0)
            return null;

        string root = Path.Combine(record.Folder, "man");
        if (!Directory.Exists(root))
        {
            logger.LogWarning("{Message}", PluginStrings.ManFolderMissing(record.Manifest.Id, root));
            return null;
        }

        var topics = new List<ManualTopic>();
        foreach (string id in record.Manifest.Man)
        {
            string topicId = Path.GetFileNameWithoutExtension(id.Trim());
            if (topicId.Length == 0)
                continue;

            // A plugin's summary cannot come from MCC's corpus, so it is taken from the page's own first paragraph.
            // That keeps the index line and the page from drifting apart, and means a plugin author writes the summary once, in the place they were already writing.
            topics.Add(new ManualTopic(topicId, ManualGroup.Plugins, Summarize(root, topicId, record.Manifest.Id, _uiCulture)));
        }

        return topics.Count == 0 ? null : _client.Manuals.Register(new DirectoryManualSource(root, topics));
    }

    /// <summary>
    /// The index summary for a plugin page: its first paragraph, trimmed to one line.
    /// Falls back to the plugin id when the page cannot be read, so the row still says who it belongs to.
    /// </summary>
    /// <remarks>
    /// The page is read in the UI culture, walking the culture chain and ending at English, so a plugin that ships a translated page gets a translated index line too.
    /// </remarks>
    internal static string Summarize(string manRoot, string topicId, string pluginId, CultureInfo culture)
    {
        var source = new DirectoryManualSource(manRoot, []);
        string? text = null;
        for (CultureInfo? c = culture; text is null && c is not null && c != CultureInfo.InvariantCulture; c = c.Parent)
        {
            if (c.Name.Length > 0)
                text = source.Read(topicId, c.Name);
        }

        text ??= source.Read(topicId, "en");
        if (text is null)
            return pluginId;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith('>'))
                continue;

            // First sentence only: the index is one column and a paragraph does not fit it.
            int stop = line.IndexOf(". ", StringComparison.Ordinal);
            string summary = stop > 0 ? line[..(stop + 1)] : line;
            return summary.Length <= 64 ? summary : summary[..61] + "...";
        }

        return pluginId;
    }

    private async Task UnloadRecordAsync(LoadedPlugin record)
    {
        string id = record.Manifest.Id;
        PluginContext? context;
        PluginMessenger? messenger;
        PluginServices? services;
        PluginStorage? storage;
        System.Runtime.Loader.AssemblyLoadContext? alc;
        lock (_gate)
        {
            context = record.Context;
            messenger = record.Messenger;
            services = record.Services;
            storage = record.Storage;
            alc = record.LoadContext;
            record.Instance = null;
            record.ManualRegistration?.Dispose();
            record.ManualRegistration = null;
            record.Context = null;
            record.Messenger = null;
            record.Services = null;

            // The exports hold this plugin's load context and, for a single-file plugin, the image it was compiled from.
            // Both would root the context past the unload.
            record.Exports = [];
            record.Storage = null;
            record.Localization = null;
            record.Settings = null;

            // The settings type belongs to the plugin's own collectible context.
            // Keeping the Type here past the unload would root that context forever and quietly turn every unload into a leak, which is exactly what the ALC-collect test exists to catch.
            record.SettingsType = null;
            record.LoadContext = null;
            record.Loaded = false;
        }

        // A reload is a fresh start: the counter measures what the running instance is doing, and carrying a window across the unload would disable the replacement for what its predecessor did.
        _crashes.Forget(id);

        // Detach the plugin's own session view up front.
        // In production this is idempotent with the real detach ClientExtensionContext.SessionEnded drives during RemoveAsync below (whichever runs first wins; DetachSession is a no-op once the session is already gone), and it is what keeps a plugin unloaded while disconnected from leaking a stale session view either way.
        try
        {
            context?.DetachSession();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin '{Id}' threw during session teardown.", id);
        }

        try
        {
            await _client.Extensions.RemoveAsync(id).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            // A client shutdown deactivates every extension but keeps its entry, so removing one afterwards tears the same entry down a second time and finds its token source already disposed.
            // The plugin is stopped either way, and the rest of this teardown still has to run.
            _logger.LogDebug(ex, "Plugin '{Id}' was already deactivated by the client shutdown.", id);
        }

        // Host-owned registrations.
        // The plugin-lifetime command scope goes here rather than in the plugin's own teardown: it is the host that handed it out, and leaving it registered would keep the command (and through it the plugin instance and its whole load context) rooted in the dispatcher forever.
        // The per-plugin cron scheduler is UMPK's own extension-scoped instance, disposed by RemoveAsync above; the host does not dispose it here.
        (context?.Commands as PluginCommandScope)?.DisposeAll();
        messenger?.DisposeAll();
        services?.DisposeAll();
        (context?.Beacon as PluginBeaconHost)?.DisposeAll();
        try
        {
            storage?.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin '{Id}' storage flush failed.", id);
        }

        alc?.Unload();
    }

    private LoadedPlugin RegisterRecord(DiscoveredPlugin discovered)
    {
        lock (_gate)
        {
            if (_plugins.TryGetValue(discovered.Manifest.Id, out LoadedPlugin? existing))
                return existing;

            var record = new LoadedPlugin(discovered.Manifest, discovered.Folder, discovered.EntryKind)
            {
                Status = PluginStrings.StateDiscovered,
            };
            _plugins[discovered.Manifest.Id] = record;
            return record;
        }
    }

    private void RefreshManifest(LoadedPlugin record)
    {
        string manifestPath = Path.Combine(record.Folder, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
            return;

        if (PluginManifest.TryParse(File.ReadAllText(manifestPath), out PluginManifest manifest, out _))
        {
            record.Manifest.SchemaVersion = manifest.SchemaVersion;
            record.Manifest.Kind = manifest.Kind;
            record.Manifest.Target = manifest.Target;
            record.Manifest.Framework = manifest.Framework;
            record.Manifest.Hosts = manifest.Hosts;
            record.Manifest.Needs = manifest.Needs;
            record.Manifest.Version = manifest.Version;
            record.Manifest.Entry = manifest.Entry;
            record.Manifest.ApiVersion = manifest.ApiVersion;
            record.Manifest.Deps = manifest.Deps;
            record.Manifest.Dmcbk = manifest.Dmcbk;
            record.Manifest.Umpk = manifest.Umpk;
            record.Manifest.Meta = manifest.Meta;
            record.Manifest.Requires = manifest.Requires;
            record.Manifest.Optional = manifest.Optional;
            record.Manifest.Exports = manifest.Exports;
            record.Manifest.Services = manifest.Services;
            record.Manifest.AdoptRanges(manifest);
        }
    }

    /// <summary>
    /// Writes the toggled <c>enabled</c> value back into the plugin's own <c>plugin.toml</c>, so the choice outlives the process.
    /// Only that one line changes.
    /// A folder with no manifest (a bare <c>.cs</c> entry loaded by path) has nothing to write to and is skipped; a write that fails is a warning, never a failed enable, because the toggle itself already took effect for this run.
    /// </summary>
    private static void PersistEnabled(LoadedPlugin record, bool enabled)
    {
        // Development selections are process-local; managed selections use ChangeEnabled and the lock.
        record.Enabled = enabled;
    }

    /// <summary>True when the manifest at <paramref name="path"/> is the one that declares <paramref name="id"/>.</summary>
    private static bool DeclaresPlugin(string path, string id)
    {
        try
        {
            return PluginManifest.TryParse(File.ReadAllText(path), out PluginManifest manifest, out _)
                && string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The three load-time compatibility gates, checked before anything is instantiated: the SDK contract (<c>api-version</c>), the application (<c>mcc</c>) and the engine (<c>umpk</c>).
    /// Each refusal names both what the manifest asked for and what this client actually has, because the person reading it has to decide whether to update the client or the plugin.
    /// </summary>
    private bool TryGateVersions(LoadedPlugin record, [NotNullWhen(false)] out string? refusal)
    {
        PluginManifest manifest = record.Manifest;
        IReadOnlyList<PluginCompatibilityFailure> failures = PluginCompatibility.Check(manifest.ApiVersion,
            manifest.Dmcbk, manifest.Umpk, manifest.Framework, manifest.Needs, manifest.Hosts, HostInfo.FromClient(_client), manifest.Target);
        refusal = failures.Count == 0 ? null : PluginStrings.Compatibility(failures[0]);
        return refusal is null;
    }

    /// <summary>
    /// The <c>[requires]</c> gate, and the cycle refusal, checked at the same moment the version gates are: before anything is instantiated, on EVERY load path (startup, <c>enable</c>, <c>reload</c>, <c>load &lt;path&gt;</c>), so a dependency cannot be got around by loading in the wrong order by hand.
    /// </summary>
    private bool TryGateDependencies(LoadedPlugin record, [NotNullWhen(false)] out string? refusal)
    {
        string id = record.Manifest.Id;
        if (record.Manifest.RequiredRanges.Count > 0 || record.Manifest.OptionalRanges.Count > 0)
        {
            PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(SnapshotManifests());
            if (graph.Cycles.TryGetValue(id, out IReadOnlyList<string>? members))
            {
                refusal = PluginStrings.DependencyCycle(id, members);
                return false;
            }
        }

        foreach ((string dependency, SemVerRange range) in record.Manifest.RequiredRanges)
        {
            LoadedPlugin? other = Find(dependency);
            if (other is null)
            {
                refusal = PluginStrings.RequiresMissing(id, dependency, range.Text);
                return false;
            }

            // The installed version decides the range, not the loaded one: a plugin whose version does not satisfy the range is the author's problem, and saying so before "it is not loaded" is the more useful of the two answers.
            if (!range.IsAny
                && (!SemVer.TryParse(other.Manifest.Version, out SemVer version) || !range.Satisfies(version)))
            {
                refusal = PluginStrings.RequiresVersion(id, dependency, range.Text, other.Manifest.Version);
                return false;
            }

            if (!other.Loaded)
            {
                refusal = PluginStrings.RequiresNotLoaded(id, dependency);
                return false;
            }
        }

        refusal = null;
        return true;
    }

    /// <summary>Every registered manifest, snapshotted under the gate so the graph is built off a stable set.</summary>
    private List<PluginManifest> SnapshotManifests()
    {
        lock (_gate)
        {
            var manifests = new List<PluginManifest>(_plugins.Count);
            foreach (LoadedPlugin record in _plugins.Values)
                manifests.Add(record.Manifest);

            return manifests;
        }
    }

    /// <summary>
    /// The loaded plugins that transitively require <paramref name="id"/>, dependents first.
    /// Empty when nothing depends on it, which is the case that lets <c>unload</c> and <c>disable</c> proceed.
    /// </summary>
    private IReadOnlyList<string> LoadedDependentsOf(string id)
    {
        List<PluginManifest> loaded;
        lock (_gate)
            loaded = [.. _plugins.Values.Where(r => r.Loaded).Select(r => r.Manifest)];

        return PluginDependencyGraph.DependentsOf(id, loaded);
    }

    /// <summary>Orders records dependencies-first for a bulk load or reload.</summary>
    private static IReadOnlyList<LoadedPlugin> InDependencyOrder(IReadOnlyList<LoadedPlugin> records)
    {
        if (records.Count < 2)
            return records;

        var byId = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (LoadedPlugin record in records)
            byId[record.Manifest.Id] = record;

        PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(byId.Values.Select(r => r.Manifest));
        var ordered = new List<LoadedPlugin>(records.Count);
        foreach (string id in graph.Order)
        {
            if (byId.TryGetValue(id, out LoadedPlugin? record))
                ordered.Add(record);
        }

        return ordered;
    }

    private IReadOnlyList<DiscoveredPlugin> Discover()
    {
        var found = new List<DiscoveredPlugin>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.EnumerateDirectories(_pluginsRoot))
        {
            // A dot folder is the host's own working space, never a plugin: .staging and .removed belong to the installer, .marketplaces to the catalogues, .cache to the Roslyn compile output.
            if (Path.GetFileName(folder).StartsWith('.'))
                continue;

            string manifestPath = Path.Combine(folder, PluginManifest.FileName);
            if (!File.Exists(manifestPath))
                continue;

            if (!PluginManifest.TryParse(File.ReadAllText(manifestPath), out PluginManifest manifest, out string? error))
            {
                _logger.LogWarning("{Message}", PluginStrings.LoadFailed(Path.GetFileName(folder), error ?? "invalid manifest"));
                continue;
            }

            if (!ids.Add(manifest.Id))
            {
                _logger.LogWarning("{Message}", PluginStrings.DuplicateId(manifest.Id));
                continue;
            }

            found.Add(new DiscoveredPlugin(manifest, folder,
                manifest.IsSourceEntry ? PluginEntryKind.Source : PluginEntryKind.Compiled));
        }

        return found;
    }

    private bool TryResolveEntry(string folderOrEntry, out DiscoveredPlugin discovered, out string? error)
    {
        discovered = default!;
        error = null;
        string full = Path.GetFullPath(folderOrEntry);

        if (Directory.Exists(full))
        {
            string manifestPath = Path.Combine(full, PluginManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                error = PluginStrings.ManifestMissing(full);
                return false;
            }

            if (!PluginManifest.TryParse(File.ReadAllText(manifestPath), out PluginManifest manifest, out error))
                return false;

            discovered = new DiscoveredPlugin(manifest, full,
                manifest.IsSourceEntry ? PluginEntryKind.Source : PluginEntryKind.Compiled);
            return true;
        }

        if (File.Exists(full) && full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // A bare .cs entry without a manifest: synthesize one from the file name.
            string id = Path.GetFileNameWithoutExtension(full);
            var manifest = new PluginManifest
            {
                Id = id,
                Version = "1.0.0",
                Entry = Path.GetFileName(full),
                ApiVersion = PluginApiVersion.Current,
                SchemaVersion = 2,
                Kind = "source",
                Target = "any",
                Framework = "net10.0",
                Dmcbk = "*",
                Umpk = "*",
            };
            discovered = new DiscoveredPlugin(manifest, Path.GetDirectoryName(full)!, PluginEntryKind.Source);
            return true;
        }

        error = PluginStrings.PathNotFound(folderOrEntry);
        return false;
    }

    private List<string> ResolveDeps(LoadedPlugin record)
    {
        var deps = new List<string>();
        foreach (string dep in record.Manifest.Deps)
        {
            string path = Path.IsPathRooted(dep) ? dep : Path.Combine(record.Folder, dep);
            deps.Add(Path.GetFullPath(path));
        }

        return deps;
    }

    private LoadedPlugin? Find(string id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out LoadedPlugin? record) ? record : null;
        }
    }

    /// <summary>
    /// What <see cref="List"/> reports for one record.
    /// An enabled plugin that is not loaded keeps the REASON the load path recorded (an api/mcc/umpk refusal, a missing or out-of-range dependency, a cycle, <c>failed</c>) rather than being flattened to the state word: that row is the only one whose state a reader cannot infer from the enabled glyph, and it is the row the listing exists to explain.
    /// </summary>
    private static string StateOf(LoadedPlugin record)
    {
        if (record.Loaded)
            return PluginStrings.StateLoaded;

        if (!record.Enabled)
            return PluginStrings.StateDisabled;

        return string.IsNullOrWhiteSpace(record.Status) ? PluginStrings.StateDiscovered : record.Status;
    }

    private static Type? FindPluginType(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type is { IsAbstract: false, IsInterface: false }
                && typeof(IPlugin).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
                return type;
        }

        return null;
    }

    private readonly record struct DiscoveredPlugin(PluginManifest Manifest, string Folder, PluginEntryKind EntryKind);
    #endregion
}

/// <summary>Thrown internally when a <c>.cs</c> plugin fails to compile; carries the formatted diagnostics.</summary>
internal sealed class PluginCompileException : InvalidOperationException
{
    internal PluginCompileException(string message)
        : base(message)
    {
    }
}

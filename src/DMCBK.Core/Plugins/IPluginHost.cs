using System.Globalization;

namespace DMCBK.Core.Plugins;

/// <summary>
/// The core-side seam the plugin host implements so the in-core <c>plugins</c> / <c>plugin load</c> commands (and the config <c>reload</c>) can drive plugin management without DMCBK.Core referencing the plugin SDK assembly.
/// The concrete host lives in DMCBK.PluginSdk (which references DMCBK.Core so the author-facing <c>PluginContext</c> can expose the real <see cref="Client"/>); the host is attached onto <see cref="Client.PluginHost"/> by the embedding host after discovery/load.
/// Every returned message is already-localized text (the host owns its own string table); commands render it verbatim.
/// </summary>
public interface IPluginHost
{
    /// <summary>Snapshots every discovered plugin and its current state.</summary>
    IReadOnlyList<PluginInfo> List();

    /// <summary>The folder plugins are discovered from and scaffolded into (settings live beneath it).</summary>
    string PluginsRoot { get; }

    /// <summary>Enables a previously disabled plugin (re-runs load + activation).</summary>
    Task<PluginActionResult> EnableAsync(string id, CancellationToken ct = default);

    /// <summary>Disables a loaded plugin (detach + unload; it stays discovered).</summary>
    Task<PluginActionResult> DisableAsync(string id, CancellationToken ct = default);

    /// <summary>Unloads a plugin (detach + collectible-context unload); it stays discovered but inactive.</summary>
    Task<PluginActionResult> UnloadAsync(string id, CancellationToken ct = default);

    /// <summary>Reloads a single plugin: re-run its config + settings pipeline and re-activate.</summary>
    Task<PluginActionResult> ReloadAsync(string id, CancellationToken ct = default);

    /// <summary>Loads a plugin from a folder (with a <c>plugin.toml</c>) or a bare <c>.cs</c> entry file.</summary>
    Task<PluginActionResult> LoadAsync(string folderOrEntry, CancellationToken ct = default);

    /// <summary>Reloads every loaded plugin (wired into the config <c>reload</c> command).</summary>
    Task<PluginActionResult> ReloadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Rewrites a loaded plugin's <c>settings.toml</c> from the file it already has: the values are read into the settings type and written straight back, so every value the schema knows about survives and the comments come out in the current language.
    /// This is the explicit user action that unfreezes comments, which are otherwise fixed at generation time exactly as <c>client.toml</c>'s are.
    /// </summary>
    Task<PluginActionResult> RegenerateSettingsAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Deletes a loaded plugin's <c>settings.toml</c> and writes it again from the type's defaults.
    /// Values the user edited are LOST; this is the "start over" verb, where <see cref="RegenerateSettingsAsync"/> is the "keep my values" one.
    /// </summary>
    Task<PluginActionResult> ResetSettingsAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// What each loaded plugin's translation looks like in the current UI language: which tags it ships and how far behind English the active one is.
    /// <c>/lang</c> renders it.
    /// </summary>
    IReadOnlyList<PluginLanguageInfo> Languages();

    /// <summary>
    /// What one plugin requires, what it prefers, and what requires it (<c>plugins deps</c>).
    /// Null when no discovered plugin has that id.
    /// A loader question, not a marketplace one, hence its home here.
    /// </summary>
    PluginDependencyInfo? DependenciesOf(string id);

    /// <summary>
    /// Checks one plugin folder, or every discovered plugin when <paramref name="folder"/> is null, without loading anything.
    /// What it reports are the failures that are otherwise silent: a string key the code asks for that the language file nests under a table, a missing English table, a settings placeholder nothing answers.
    /// </summary>
    IReadOnlyList<PluginCheckResult> Validate(string? folder = null);

    /// <summary>
    /// Scaffolds a new plugin folder under the plugins root: manifest, single-file entry, English strings and a manual page, disabled until the author enables it.
    /// </summary>
    PluginActionResult Scaffold(string id);

    /// <summary>
    /// One report over everything the host knows is wrong or worth saying: refused plugins, missing dependencies, translation coverage, stale compile caches, folders that are not plugins, and the exception counters.
    /// </summary>
    PluginDoctorReport Doctor();

    /// <summary>
    /// Retargets every loaded plugin's <c>lang/</c> table at a new UI language, so <c>/lang &lt;tag&gt;</c> takes effect without a restart.
    /// Files already read are reused; only the language they answer in changes.
    /// Settings comments are NOT rewritten, because they are on disk: that is what <see cref="RegenerateSettingsAsync"/> is for.
    /// </summary>
    void UseCulture(CultureInfo culture);
}

/// <summary>One loaded plugin's translation coverage, as <c>/lang</c> shows it.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Languages">Every language tag the plugin ships a file for, ordered.</param>
/// <param name="Active">The tag that answers in the current UI language, or null when it ships none.</param>
/// <param name="MissingKeys">How many keys English has that the active language does not.</param>
public sealed record PluginLanguageInfo(
    string Id, IReadOnlyList<string> Languages, string? Active, int MissingKeys);

/// <summary>How a plugin's entry point is deployed.</summary>
public enum PluginEntryKind
{
    /// <summary>A precompiled <c>.dll</c> assembly.</summary>
    Compiled,

    /// <summary>A single <c>.cs</c> source file compiled at load time.</summary>
    Source,
}

/// <summary>A discovered plugin and its live state.</summary>
/// <param name="Id">The manifest id.</param>
/// <param name="Version">The manifest version.</param>
/// <param name="Entry">Whether the entry is a compiled dll or a source file.</param>
/// <param name="Enabled">Whether the plugin is enabled (config/manifest driven).</param>
/// <param name="Loaded">Whether the plugin is currently loaded and activated.</param>
/// <param name="Status">An optional already-localized status/diagnostic line (for example a load error).</param>
/// <param name="Offline">Whether the manifest's <c>[services] offline</c> claims it works with no session.</param>
/// <param name="FaultCount">Exceptions this plugin has thrown since it loaded, across every surface.</param>
/// <param name="LastError">The last exception, already rendered, or null when it has thrown none.</param>
public sealed record PluginInfo(
    string Id,
    string Version,
    PluginEntryKind Entry,
    bool Enabled,
    bool Loaded,
    string? Status,
    bool Offline = false,
    int FaultCount = 0,
    string? LastError = null);

/// <summary>The outcome of a plugin management action, carrying an already-localized message.</summary>
/// <param name="Success">Whether the action succeeded.</param>
/// <param name="Message">The already-localized result line the command renders.</param>
public sealed record PluginActionResult(bool Success, string Message);

/// <summary>One finding from <see cref="IPluginHost.Validate"/>.</summary>
/// <param name="Error">True when the plugin will not load, or will print key names instead of text.</param>
/// <param name="File">The file it is in, relative to the plugin folder, or empty for the folder itself.</param>
/// <param name="Line">The one-based line, or 0 when the finding is not on a line.</param>
/// <param name="Message">The already-localized description.</param>
public sealed record PluginCheckProblem(bool Error, string File, int Line, string Message);

/// <summary>What <see cref="IPluginHost.Validate"/> found in one plugin folder.</summary>
/// <param name="Id">The manifest id, or the folder name when the manifest could not be read.</param>
/// <param name="Folder">The folder that was checked.</param>
/// <param name="Problems">Everything found, manifest problems first.</param>
public sealed record PluginCheckResult(string Id, string Folder, IReadOnlyList<PluginCheckProblem> Problems)
{
    /// <summary>True when nothing found was an error.</summary>
    public bool IsValid => !Problems.Any(p => p.Error);
}

/// <summary>One row of <c>plugins doctor</c>: a plugin, or a folder that is not one.</summary>
/// <param name="Id">The plugin id, or the folder name for a folder with no manifest.</param>
/// <param name="Detail">The already-localized line describing what is wrong or worth knowing.</param>
/// <param name="Error">True when this stops the plugin working, false when it is an observation.</param>
public sealed record PluginDoctorNote(string Id, string Detail, bool Error);

/// <summary>Everything <c>plugins doctor</c> collected in one pass.</summary>
/// <param name="Discovered">How many plugin folders the host knows about.</param>
/// <param name="Loaded">How many of them are loaded right now.</param>
/// <param name="Notes">The findings, in the order they should be printed.</param>
public sealed record PluginDoctorReport(int Discovered, int Loaded, IReadOnlyList<PluginDoctorNote> Notes)
{
    /// <summary>True when nothing found was an error.</summary>
    public bool IsHealthy => !Notes.Any(n => n.Error);
}

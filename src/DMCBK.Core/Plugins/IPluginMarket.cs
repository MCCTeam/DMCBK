namespace DMCBK.Core.Plugins;

/// <summary>
/// The core-side seam for installing plugins, beside <see cref="IPluginHost"/> and shaped the same way: the <c>plugins</c> command talks only to this, the implementation lives in DMCBK.Marketplace, and every returned message is already-localized text the command renders verbatim.
/// <para>
/// Whether the user agrees is the one thing this seam cannot decide.
/// <see cref="Confirm"/> comes from the host, because how you ask is a host question: the classic console reads a line, the TUI asks on its input line, a scripted run answers with the <c>yes</c> keyword.
/// The default declines, which is the only safe answer for a host that never wired one up.
/// </para>
/// </summary>
public interface IPluginMarket
{
    /// <summary>
    /// How the host asks the user to confirm an install or an update.
    /// Called once per plugin.
    /// False aborts and nothing is swapped in.
    /// </summary>
    Func<InstallConfirmation, CancellationToken, ValueTask<bool>> Confirm { get; set; }

    /// <summary>
    /// Installs a plugin from <paramref name="what"/>: a plugin id, <c>id@marketplace</c>, GitHub <c>owner/repo[@ref]</c>, a Git URL with an optional <c>#ref</c>, an archive URL, or a local folder.
    /// <paramref name="version"/> is an optional exact version or range the fetched manifest must satisfy.
    /// <paramref name="assumeYes"/> answers the confirmation, for scripted runs.
    /// </summary>
    Task<PluginActionResult> InstallAsync(
        string what, string? version = null, bool assumeYes = false, CancellationToken ct = default);

    /// <summary>Installs with explicit source selection for this request.</summary>
    Task<PluginActionResult> InstallAsync(string what, PluginInstallOptions options,
        string? version = null, bool assumeYes = false, CancellationToken ct = default)
        => InstallAsync(what, version, assumeYes, ct);

    /// <summary>Returns historical versions and their declared platform assets without downloading payloads.</summary>
    Task<IReadOnlyList<PluginReleaseInfo>> VersionsAsync(string id, string? marketplace = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PluginReleaseInfo>>([]);

    /// <summary>Returns completed local transactions that can be selected for rollback.</summary>
    Task<IReadOnlyList<PluginRollbackInfo>> RollbackHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PluginRollbackInfo>>([]);

    /// <summary>Restores a cached package graph identified by its transaction ID.</summary>
    Task<PluginActionResult> RollbackAsync(string transactionId, CancellationToken ct = default)
        => throw new NotSupportedException(nameof(RollbackAsync));

    /// <summary>
    /// Removes an installed plugin's folder.
    /// Without <paramref name="purge"/> its <c>settings.toml</c> and <c>data/</c> are moved to <c>plugins/.removed/&lt;id&gt;/</c> instead of being deleted.
    /// </summary>
    Task<PluginActionResult> UninstallAsync(string id, bool purge = false, CancellationToken ct = default);

    /// <summary>
    /// Updates one plugin, or every managed plugin when <paramref name="id"/> is null or <c>all</c>.
    /// A pinned plugin is skipped, and a version this client is too old for is reported, never applied.
    /// </summary>
    Task<PluginActionResult> UpdateAsync(string? id = null, CancellationToken ct = default);

    /// <summary>What has a newer version available, and what is held back and why. Installs nothing.</summary>
    Task<IReadOnlyList<PluginUpdateInfo>> OutdatedAsync(CancellationToken ct = default);

    /// <summary>Holds a plugin at its current version, or at <paramref name="version"/> when one is named.</summary>
    Task<PluginActionResult> PinAsync(string id, string? version = null, CancellationToken ct = default);

    /// <summary>Releases a pin.</summary>
    Task<PluginActionResult> UnpinAsync(string id, CancellationToken ct = default);

    /// <summary>Everything known about one installed plugin, or null when no plugin has that id.</summary>
    Task<PluginMarketInfo?> InfoAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Adds a marketplace: a GitHub <c>owner/repo</c>, a Git URL, an HTTPS URL to the catalogue file, or a local folder.
    /// Without <paramref name="name"/> the catalogue's own name is used.
    /// </summary>
    Task<PluginActionResult> MarketplaceAddAsync(
        string source, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Forgets a marketplace.
    /// Plugins installed from it stay where they are and become unmanaged, which the message says.
    /// </summary>
    Task<PluginActionResult> MarketplaceRemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Every added marketplace, with its source, plugin count, policy and last refresh.</summary>
    Task<IReadOnlyList<MarketplaceInfo>> MarketplacesAsync(CancellationToken ct = default);

    /// <summary>Re-fetches one marketplace's catalogue, or every one when <paramref name="name"/> is null.</summary>
    Task<PluginActionResult> MarketplaceRefreshAsync(string? name = null, CancellationToken ct = default);

    /// <summary>Sets a marketplace's auto-update policy to <c>off</c>, <c>check</c> or <c>apply</c>.</summary>
    Task<PluginActionResult> MarketplaceAutoUpdateAsync(
        string name, string policy, CancellationToken ct = default);

    /// <summary>
    /// Searches the added marketplaces' catalogues, or one of them, by id, description and tags.
    /// Reads what is on disk: nothing is fetched, so an empty answer can mean a stale catalogue.
    /// </summary>
    Task<IReadOnlyList<MarketplaceSearchResult>> SearchAsync(
        string text, string? marketplace = null, CancellationToken ct = default);

    /// <summary>
    /// Runs the per-marketplace auto-update pass: a short random delay, then a refresh and a report or a staged update for each marketplace whose policy asks for one.
    /// Returns when the pass is over, so a caller may await it or not.
    /// </summary>
    Task StartAutoUpdateAsync(CancellationToken ct = default);
}

/// <summary>
/// What the user is being asked to agree to.
/// <see cref="Text"/> is the whole thing already rendered, so every host asks the same question in the same words.
/// The fields are there for a host that would rather lay it out itself.
/// </summary>
/// <param name="Id">The plugin id from the fetched manifest.</param>
/// <param name="Version">The version from the fetched manifest.</param>
/// <param name="Source">The source, rendered ("github:someone/mcc-xray@v0.3").</param>
/// <param name="Marketplace">The marketplace it came from, or null for a direct install.</param>
/// <param name="Entry">Whether the entry is a source file or a compiled assembly.</param>
/// <param name="EntryLines">Line count of a <c>.cs</c> entry, so the user knows how much they could read.</param>
/// <param name="Uses">The capabilities the manifest's <c>[meta] uses</c> declares. Disclosure, not enforcement.</param>
/// <param name="Requires">Plugin ids this one requires, with their ranges, as text.</param>
/// <param name="IsUpdate">True when a plugin with this id is already installed.</param>
/// <param name="PreviousVersion">The version being replaced, when this is an update.</param>
/// <param name="Text">The rendered confirmation, ending in the unsandboxed-code warning.</param>
/// <param name="AlsoInstalling">
/// Dependencies that will be installed with it, already fetched and rendered as <c>id version</c>.
/// Empty for an install that pulls nothing along.
/// </param>
/// <param name="Changes">Every selected package change, including its publisher, target, digest and resolver reason.</param>
public sealed record InstallConfirmation(
    string Id,
    string Version,
    string Source,
    string? Marketplace,
    PluginEntryKind Entry,
    int EntryLines,
    IReadOnlyList<string> Uses,
    IReadOnlyList<string> Requires,
    bool IsUpdate,
    string? PreviousVersion,
    string Text,
    IReadOnlyList<string>? AlsoInstalling = null,
    IReadOnlyList<PluginPlannedChange>? Changes = null);

/// <summary>One row of <c>plugins outdated</c>.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="InstalledVersion">The version on disk.</param>
/// <param name="AvailableVersion">The version the source offers, or null when it could not be read.</param>
/// <param name="Pinned">True while a pin holds this plugin at its version.</param>
/// <param name="BlockedReason">
/// Why this client cannot install the available version, meaning it needs a newer SDK API, MCC or UMPK, or null when nothing blocks it.
/// </param>
/// <param name="Error">Why the check itself failed, when it did.</param>
public sealed record PluginUpdateInfo(
    string Id,
    string InstalledVersion,
    string? AvailableVersion,
    bool Pinned,
    string? BlockedReason,
    string? Error);

/// <summary>Everything <c>plugins info</c> shows about one installed plugin.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Entry">The entry kind.</param>
/// <param name="Enabled">Whether the manifest has it enabled.</param>
/// <param name="Loaded">Whether it is loaded right now.</param>
/// <param name="Status">The host's status line for it.</param>
/// <param name="Folder">The folder it lives in.</param>
/// <param name="Description">The <c>[meta] description</c>, when it has one.</param>
/// <param name="Homepage">The <c>[meta] homepage</c>, when it has one.</param>
/// <param name="Tags">The <c>[meta] tags</c>.</param>
/// <param name="Uses">The <c>[meta] uses</c> disclosure.</param>
/// <param name="Requires">Required plugin ids with their ranges, as text.</param>
/// <param name="Optional">Optional plugin ids with their ranges, as text.</param>
/// <param name="Dependents">Installed plugins that require this one.</param>
/// <param name="Exports">Assemblies this plugin exports to its dependents.</param>
/// <param name="Languages">Language tags the plugin ships.</param>
/// <param name="ManualTopics">Manual topics the plugin contributes.</param>
/// <param name="IsLocal">True when installed through the explicit development source binding.</param>
/// <param name="Marketplace">The marketplace it was installed from, when it was.</param>
/// <param name="Source">The rendered source from the lock file, when there is one.</param>
/// <param name="Installed">When it was installed, from the lock file.</param>
/// <param name="Pinned">Whether a pin holds it.</param>
/// <param name="PinnedVersion">The version the pin holds at, when the pin named one.</param>
/// <param name="AutoUpdate">The per-plugin auto-update policy from the lock file.</param>
/// <param name="Offline">Whether the manifest's <c>[services] offline</c> claims it works with no session.</param>
/// <param name="FaultCount">Exceptions it has thrown since it loaded.</param>
/// <param name="LastError">The last exception, already rendered, or null when it has thrown none.</param>
/// <param name="Target">The installed process target.</param>
/// <param name="AssetHash">The immutable archive SHA-256.</param>
public sealed record PluginMarketInfo(
    string Id,
    string Version,
    PluginEntryKind Entry,
    bool Enabled,
    bool Loaded,
    string? Status,
    string Folder,
    string? Description,
    string? Homepage,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Uses,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Optional,
    IReadOnlyList<string> Dependents,
    IReadOnlyList<string> Exports,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> ManualTopics,
    bool IsLocal,
    string? Marketplace,
    string? Source,
    DateTimeOffset? Installed,
    bool Pinned,
    string? PinnedVersion,
    string? AutoUpdate,
    bool Offline = false,
    int FaultCount = 0,
    string? LastError = null,
    string? Target = null,
    string? AssetHash = null);

/// <summary>One row of <c>plugins marketplace list</c>.</summary>
/// <param name="Name">The name plugins are qualified with.</param>
/// <param name="Source">Where the catalogue comes from, as <c>marketplaces.toml</c> holds it.</param>
/// <param name="Description">The catalogue's own description, when it could be read.</param>
/// <param name="PluginCount">How many entries the catalogue holds.</param>
/// <param name="AutoUpdate">The policy: <c>off</c>, <c>check</c> or <c>apply</c>.</param>
/// <param name="Refreshed">When the catalogue was last fetched, or null when it never was.</param>
/// <param name="IsLocal">True for a folder on this machine, which is read where it stands.</param>
/// <param name="Error">Why the catalogue could not be read, when it could not.</param>
public sealed record MarketplaceInfo(
    string Name,
    string Source,
    string? Description,
    int PluginCount,
    string AutoUpdate,
    DateTimeOffset? Refreshed,
    bool IsLocal,
    string? Error);

/// <summary>One row of <c>plugins search</c>.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Marketplace">The marketplace the entry came from.</param>
/// <param name="Version">The version the catalogue offers.</param>
/// <param name="Description">The catalogue's one-line description.</param>
/// <param name="Tags">The entry's tags.</param>
/// <param name="Source">The entry's source, rendered.</param>
/// <param name="Incompatibility">Why this client cannot install it, or null when it can.</param>
/// <param name="Installed">The installed version of this id, or null when it is not installed.</param>
public sealed record MarketplaceSearchResult(
    string Id,
    string Marketplace,
    string Version,
    string Description,
    IReadOnlyList<string> Tags,
    string Source,
    string? Incompatibility,
    string? Installed);

/// <summary>What a plugin depends on and what depends on it, as <c>plugins deps</c> renders it.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Requires">Required plugin ids with their ranges, as text.</param>
/// <param name="Optional">Optional plugin ids with their ranges, as text.</param>
/// <param name="Dependents">Installed plugins that require this one, dependents first.</param>
/// <param name="Missing">Required ids that are not installed at all.</param>
public sealed record PluginDependencyInfo(
    string Id,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Optional,
    IReadOnlyList<string> Dependents,
    IReadOnlyList<string> Missing);

/// <summary>Explicit source policy, scoped to one installation request.</summary>
public sealed record PluginInstallOptions(bool PreferSource = false, bool AllowSourceFallback = false, bool IncludePrerelease = false);

/// <summary>A historical plugin release with host compatibility and declared payload identities.</summary>
/// <param name="Version">The exact semantic version.</param>
/// <param name="Marketplace">The explicit publisher binding.</param>
/// <param name="Yanked">Whether normal installs exclude this version.</param>
/// <param name="Incompatibility">A localized host compatibility failure, when present.</param>
/// <param name="Assets">The declared kind and target of each payload.</param>
public sealed record PluginReleaseInfo(string Version, string Marketplace, bool Yanked,
    string? Incompatibility, IReadOnlyList<PluginAssetInfo> Assets);

/// <summary>A declared release payload identity.</summary>
/// <param name="Kind">Source or compiled.</param>
/// <param name="Target">The process target or any.</param>
public sealed record PluginAssetInfo(string Kind, string Target);

/// <summary>A completed local graph transaction and its previous versions.</summary>
/// <param name="TransactionId">The exact rollback identifier.</param>
/// <param name="CommittedAt">The local commit timestamp.</param>
/// <param name="PreviousVersions">The graph that rollback restores, as plugin id and exact version.</param>
public sealed record PluginRollbackInfo(string TransactionId, DateTimeOffset CommittedAt, IReadOnlyList<string> PreviousVersions);

/// <summary>An immutable package change presented by any installation host.</summary>
/// <param name="Id">The plugin identity.</param>
/// <param name="Marketplace">The explicit publisher binding.</param>
/// <param name="PreviousVersion">The previous selected version, when installed.</param>
/// <param name="Version">The requested or resolved version.</param>
/// <param name="Kind">Source or compiled.</param>
/// <param name="Target">The selected process target.</param>
/// <param name="Sha256">The archive digest.</param>
/// <param name="Reason">The resolver's reason identifier.</param>
public sealed record PluginPlannedChange(string Id, string Marketplace, string? PreviousVersion,
    string Version, string Kind, string Target, string Sha256, string Reason);

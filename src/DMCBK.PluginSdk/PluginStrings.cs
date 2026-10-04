using System.Globalization;

namespace DMCBK.PluginSdk;

/// <summary>
/// The single home for the plugin host's user-facing text (discovery diagnostics, load/enable/disable/reload results, api-version refusal, compile errors).
/// Centralized like the core's <c>CommandStrings</c> so a future localization sweep can replace it wholesale.
/// No hardcoded literals in the host classes; no em dashes.
/// </summary>
internal static class PluginStrings
{
    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // list
    public static string ListNone => TextResources.Get("plugin.text.literal_10b4291260e6");
    public static string ListHeader(int count) => F(TextResources.Get("plugin.text.literal_58129c8b2e59"), count);
    public static string ListEntry(string id, string version, string entry, string state)
        => F("  {0} {1} [{2}] - {3}", id, version, entry, state);
    public static string ManFolderMissing(string id, string path)
        => F(TextResources.Get("plugin.text.literal_0807a17ec42d"), id, path);

    public static string StateLoaded => "loaded";
    public static string StateDisabled => "disabled";
    public static string StateFailed => "failed";
    public static string StateDiscovered => "discovered";
    public static string EntryCompiled => "dll";
    public static string EntrySource => "cs";

    // load / discovery
    public static string Loaded(string id) => F(TextResources.Get("plugin.text.literal_44a1a6980426"), id);
    public static string LoadedCount(int loaded, int total) => F(TextResources.Get("plugin.text.literal_47b5a3e1cd32"), loaded, total);
    public static string AlreadyLoaded(string id) => F(TextResources.Get("plugin.text.literal_8344eae76341"), id);
    public static string NotFound(string id) => F(TextResources.Get("plugin.text.literal_a13b8bbe8925"), id);
    public static string PathNotFound(string path) => F(TextResources.Get("plugin.text.literal_20c18cbaa462"), path);
    public static string NoPluginsRoot => TextResources.Get("plugin.text.literal_2ee493d8a3a1");
    public static string ManifestMissing(string folder) => F(TextResources.Get("plugin.text.literal_71b7ebc10285"), folder);
    public static string ManifestInvalid(string reason) => F(TextResources.Get("plugin.text.literal_53c9b585d437"), reason);
    public static string ManifestIdInvalid(string id)
        => F(TextResources.Get("plugin.text.literal_ac37212dfc4b"),
            string.IsNullOrWhiteSpace(id) ? "" : id);
    public static string EntryMissing(string entry) => F(TextResources.Get("plugin.text.literal_003a2c794b0a"), entry);
    public static string DuplicateId(string id) => F(TextResources.Get("plugin.text.literal_beb9a1e6df9a"), id);
    public static string NoPluginType(string entry) => F(TextResources.Get("plugin.text.literal_a86dc467ed8a"), entry);

    // validation (plugins validate)
    public static string ManPageMissing(string topic)
        => F(TextResources.Get("plugin.text.literal_64c2e931f388"), topic);
    public static string LangUnparsable(string language, string reason)
        => F(TextResources.Get("plugin.text.literal_24b9a1dab455"),
            language, reason);
    public static string LangEnglishMissing(IReadOnlyList<string> languages)
        => F(TextResources.Get("plugin.text.literal_1a0156126d19"), string.Join(", ", languages));
    public static string LangKeyNested(string key, string table)
        => F(TextResources.Get("plugin.text.literal_60b063b5c9c5"),
            key, table);
    public static string LangKeyMissing(string key)
        => F(TextResources.Get("plugin.text.literal_f7d21c37d5aa"), key);
    public static string LangPlaceholderMissing(string key, string id)
        => F(TextResources.Get("plugin.text.literal_e548dc65850c"), key, id);

    // scaffolding (plugins new)
    public static string ScaffoldIdInvalid(string id)
        => F(TextResources.Get("plugin.text.literal_263ae2f945fc"),
            string.IsNullOrWhiteSpace(id) ? string.Empty : id);
    public static string ScaffoldExists(string folder)
        => F(TextResources.Get("plugin.text.literal_da5a42e18149"), folder);
    public static string ScaffoldFailed(string id, string reason)
        => F(TextResources.Get("plugin.text.literal_d836eb94555b"), id, reason);
    public static string Scaffolded(string id, string folder, IReadOnlyList<string> files)
        => F(TextResources.Get("plugin.text.literal_0b7e8ae46ef8"),
            id, folder, string.Join("\n  ", files));

    // crash isolation
    public static string CrashDisabled(string id, int count, int seconds, string reason)
        => F(TextResources.Get("plugin.text.literal_524f89d31bdf"), id, count, seconds, reason);
    public static string CrashSurfaceEvent(string name) => F(TextResources.Get("plugin.text.literal_b8131314901f"), name);
    public static string CrashSurfaceCommand => TextResources.Get("plugin.text.literal_b2ac90ec3c2e");
    public static string CrashSurfaceScheduler => TextResources.Get("plugin.text.literal_706f9f12c50c");
    public static string LastError(string surface, string message) => F("{0}: {1}", surface, message);
    public static string CrashSurfaceCommandNamed(string name) => F(TextResources.Get("plugin.text.literal_0238ad564cd9"), name);
    public static string PluginThrew(string id, string what) => F(TextResources.Get("plugin.text.literal_c57b683ebcc5"), id, what);

    // doctor (plugins doctor)
    public static string DoctorNoManifest(string folder)
        => F(TextResources.Get("plugin.text.literal_e539740e3751"), folder);
    public static string DoctorMissingDependencies(IReadOnlyList<string> missing)
        => F(TextResources.Get("plugin.text.literal_ce2bc8f9155c"), string.Join(", ", missing));
    public static string DoctorLangBehind(string language, int missing)
        => F(TextResources.Get("plugin.text.literal_33bfdb1a2b6c"), language, missing);
    public static string DoctorLangNone(string language)
        => F(TextResources.Get("plugin.text.literal_90c97a9b4f66"), language);
    public static string DoctorStaleCache(int count, string folder)
        => F(TextResources.Get("plugin.text.literal_cf33f165d3da"), count, folder);
    public static string DoctorLockMarketplaceGone(string marketplace)
        => F(TextResources.Get("plugin.text.literal_b2632e120900"), marketplace);
    public static string DoctorLockUnreadable(string reason) => F(TextResources.Get("plugin.text.literal_22962e4c7d77"), reason);
    public static string DoctorFaults(int count, string last)
        => F(TextResources.Get("plugin.text.literal_5038e4541709"), count, last);
    public static string DoctorDisabledByCrash(string last)
        => F(TextResources.Get("plugin.text.literal_e62433e61c71"), last);

    // settings regeneration (plugins settings <id> regen|reset)
    public static string SettingsRegenerated(string id)
        => F(TextResources.Get("plugin.text.literal_5168f7f7ebb4"), id);
    public static string SettingsReset(string id)
        => F(TextResources.Get("plugin.text.literal_f67bec1be939"), id);
    public static string SettingsNotLoaded(string id)
        => F(TextResources.Get("plugin.text.literal_8528bfb5a2b5"), id);
    public static string SettingsNone(string id)
        => F(TextResources.Get("plugin.text.literal_726370de4a29"), id);
    public static string SettingsFailed(string id, string reason)
        => F(TextResources.Get("plugin.text.literal_1fb423730a9a"), id, reason);

    /// <summary>Formats a structured compatibility failure with its requested and actual versions.</summary>
    public static string Compatibility(PluginCompatibilityFailure failure)
        => F(TextResources.Get(failure.Code), failure.Values.Cast<object>().ToArray());

    // api-version
    public static string ApiIncompatible(string id, string declared, string current)
        => F(TextResources.Get("plugin.text.literal_1c6c96757967"),
            id, declared, current);

    // version ranges (mcc / umpk / semver parsing)
    public static string SemVerInvalid(string text) => F(TextResources.Get("plugin.text.literal_4a338396c457"), text);
    public static string RangeInvalid(string text) => F(TextResources.Get("plugin.text.literal_995be97668b2"), text);
    public static string ManifestRangeInvalid(string key, string text)
        => F(TextResources.Get("plugin.text.literal_9381d40f8b40"), text, key);
    public static string ManifestDependencyIdEmpty(string table)
        => F(TextResources.Get("plugin.text.literal_2f04e8089732"), table);
    public static string MccIncompatible(string id, string required, string actual)
        => F(TextResources.Get("plugin.text.literal_2e2be98ae225"), id, required, actual);
    public static string UmpkIncompatible(string id, string required, string actual)
        => F(TextResources.Get("plugin.text.literal_d96f2aba6ce5"), id, required, actual);
    public static string UmpkVersionUnknown(string informational)
        => F(TextResources.Get("plugin.text.literal_ff2f1d29816c"),
            string.IsNullOrWhiteSpace(informational) ? "none" : informational);

    // plugin-to-plugin dependencies ([requires] / [optional] / [exports])
    public static string RequiresMissing(string id, string dependency, string range)
        => F(TextResources.Get("plugin.text.literal_0fd80ea64ada"),
            id, dependency, range);
    public static string RequiresNotLoaded(string id, string dependency)
        => F(TextResources.Get("plugin.text.literal_ee47c1b65807"),
            id, dependency);
    public static string RequiresVersion(string id, string dependency, string range, string actual)
        => F(TextResources.Get("plugin.text.literal_ce5cdb1d5916"),
            id, dependency, range, actual);
    public static string DependencyCycle(string id, IReadOnlyList<string> members)
        => F(TextResources.Get("plugin.text.literal_b299f68779df"), id, string.Join(" -> ", [.. members, members[0]]));
    public static string DependentsLoadedUnload(string id, IReadOnlyList<string> dependents)
        => F(TextResources.Get("plugin.text.literal_2b3b8256e17f"),
            id, string.Join(", ", dependents), Chain(TextResources.Get("plugin.text.literal_d34207639b07"), dependents, id));
    public static string DependentsLoadedDisable(string id, IReadOnlyList<string> dependents)
        => F(TextResources.Get("plugin.text.literal_9cbc755ee985"),
            id, string.Join(", ", dependents), Chain(TextResources.Get("plugin.text.literal_b5b60e9231a8"), dependents, id));
    public static string ReloadedCascade(string id, IReadOnlyList<string> dependents)
        => F(TextResources.Get("plugin.text.literal_dbc8fab93bd6"), id, string.Join(", ", dependents));
    public static string ExportMissing(string id, string assembly)
        => F(TextResources.Get("plugin.text.literal_a9475ae61c62"),
            id, assembly);

    /// <summary>The chained command a refusal prints: dependents first, in order, then the plugin itself.</summary>
    private static string Chain(string verb, IReadOnlyList<string> dependents, string id)
        => string.Join("; ", [.. dependents.Select(d => $"{verb} {d}"), $"{verb} {id}"]);

    // enabled write-back (plugins enable / plugins disable)
    public static string EnabledWriteBackFailed(string id, string reason)
        => F(TextResources.Get("plugin.text.literal_db575c52e54c"), id, reason);

    // install sources (plugins install <what>)
    public static string ReferenceEmpty => TextResources.Get("plugin.text.literal_dad737473512");
    public static string ReferenceUnreadable(string text)
        => F(TextResources.Get("plugin.text.literal_f3990eb3f10f"), text);
    public static string LockInvalid(string path, string reason)
        => F(TextResources.Get("plugin.text.literal_cf08df1127a0"), path, reason);
    public static string ArchiveUnknownFormat =>
        TextResources.Get("plugin.text.literal_21fe83bd359b");
    public static string ArchiveEscapes(string entry)
        => F(TextResources.Get("plugin.text.literal_4768027f505f"), entry);
    public static string ManifestMissingInFetch =>
        TextResources.Get("plugin.text.literal_d961c41cc81a");
    public static string FetchIsACatalogue =>
        TextResources.Get("plugin.text.literal_941fb4a064e8");
    public static string DownloadTooLarge(string url, int limitMegabytes)
        => F(TextResources.Get("plugin.text.literal_6a7cba404f6d"), url, limitMegabytes);
    public static string FolderNotDeleted(string folder) => F(TextResources.Get("plugin.text.literal_4462c16e699b"), folder);
    public static string StagedUpdateFailed =>
        TextResources.Get("plugin.text.literal_bb9cf61fd8e7");
    public static string DownloadFailed(string url, int status)
        => F(TextResources.Get("plugin.text.literal_e0276268e3c9"), url, status);
    public static string DownloadUnreachable(string url, string reason)
        => F(TextResources.Get("plugin.text.literal_d6e6e97cf6e0"), url, reason);
    public static string DownloadTimedOut(string url) => F(TextResources.Get("plugin.text.literal_0aee1a8de785"), url);
    public static string HashMismatch(string expected, string actual)
        => F(TextResources.Get("plugin.text.literal_16da53dde2fe"),
            expected, actual);
    public static string GitMissing(string executable)
        => F(TextResources.Get("plugin.text.literal_6e047dd94382"), executable);
    public static string GitCloneFailed(string url, string reason)
        => F(TextResources.Get("plugin.text.literal_63b792ac9bcf"), url, string.IsNullOrWhiteSpace(reason) ? TextResources.Get("plugin.text.literal_625f8ca31f09") : reason);

    // install / update / uninstall (IPluginMarket)
    public static string NoMarketplaces(string what)
        => F(TextResources.Get("plugin.text.literal_0de94660360c"), what);
    public static string InstallFailed(string what, string reason)
        => F(TextResources.Get("plugin.text.literal_3b91b140db57"), what, reason);
    public static string Installed(string id, string version)
        => F(TextResources.Get("plugin.text.literal_b08a39242c87"), id, version);
    public static string Updated(string id, string from, string to)
        => F(TextResources.Get("plugin.text.literal_df238e31d26e"), id, from, to);
    public static string InstallDeclined(string id) => F(TextResources.Get("plugin.text.literal_6c8102e6ebe9"), id);
    public static string VersionNotOffered(string id, string wanted, string offered)
        => F(TextResources.Get("plugin.text.literal_c9e60f148360"), id, wanted, offered);
    public static string RequiresNotInstalled(string id, IReadOnlyList<string> missing)
        => F(TextResources.Get("plugin.text.literal_570c95458daa"), id, string.Join(", ", missing));
    public static string LocalPlugin(string id, string verb)
        => F(TextResources.Get("plugin.text.literal_50ba1ec14392"), id, verb);
    public static string LockHasNoSource(string id)
        => F(TextResources.Get("plugin.text.literal_f7fe75652421"), id);
    public static string NothingManaged =>
        TextResources.Get("plugin.text.literal_7032fd2b1cb8");
    public static string UpToDate(string id, string version) => F(TextResources.Get("plugin.text.literal_01f9db238bcf"), id, version);
    public static string ReloadedDependents(IReadOnlyList<string> ids)
        => F(TextResources.Get("plugin.text.literal_f5949d0a30b9"), string.Join(", ", ids));
    public static string UpdateFailed(string id, string reason) => F(TextResources.Get("plugin.text.literal_6486213cc514"), id, reason);
    public static string UpdateSkippedPinned(string id, string version)
        => F(TextResources.Get("plugin.text.literal_163dfa437245"), id, version);
    public static string Pinned(string id, string version) => F(TextResources.Get("plugin.text.literal_ef8d23db63f2"), id, version);
    public static string Unpinned(string id) => F(TextResources.Get("plugin.text.literal_6f480ea55e4e"), id);
    public static string Uninstalled(string id) => F(TextResources.Get("plugin.text.literal_eb8f5916c74c"), id);
    public static string UninstalledKeeping(string id, string parked)
        => F(TextResources.Get("plugin.text.literal_5e13cec3b17b"),
            id, parked);
    public static string SwapFailed(string id, string reason)
        => F(TextResources.Get("plugin.text.literal_96d4aa8e37cd"),
            id, reason);
    public static string SwapRolledBack(string folder)
        => F(TextResources.Get("plugin.text.literal_eb006328fb7c"), folder);
    public static string SwapRollbackFailed(string target, string aside)
        => F(TextResources.Get("plugin.text.literal_d534f64609e9"), target, aside);

    // marketplace catalogues (mcc-marketplace.toml)
    public static string CatalogInvalid(string reason) => F(TextResources.Get("plugin.text.literal_ea2f15794efc"), reason);
    public static string CatalogMissing(string where)
        => F(TextResources.Get("plugin.text.literal_52fb73aae549"), where);
    public static string CatalogNameInvalid(string name)
        => F(TextResources.Get("plugin.text.literal_e643ff34c6c2"),
            string.IsNullOrWhiteSpace(name) ? "" : name);
    public static string CatalogDuplicateEntry(string id)
        => F(TextResources.Get("plugin.text.literal_23903a10ad58"), id);
    public static string CatalogEntryNoId => TextResources.Get("plugin.text.literal_660d8ab0e956");
    public static string CatalogEntryNoVersion(string id) => F(TextResources.Get("plugin.text.literal_86360ad47ecf"), id);
    public static string CatalogEntryRangeInvalid(string id, string key, string text)
        => F(TextResources.Get("plugin.text.literal_edd979044b5c"), id, key, text);
    public static string CatalogEntrySourceInvalid(string id)
        => F(TextResources.Get("plugin.text.literal_13dea4288253"), id);
    public static string CatalogEntryPathEscapes(string id, string path)
        => F(TextResources.Get("plugin.text.literal_0fa73a4029bd"), id, path);
    public static string CatalogEntryUrlInvalid(string id, string key, string value)
        => F(TextResources.Get("plugin.text.literal_94eef2eca946"), id, key, value);
    public static string CatalogEntryPathMissing(string id, string path)
        => F(TextResources.Get("plugin.text.literal_b8bf8204dcc5"), id, path);

    // marketplaces.toml and the marketplace verbs
    public static string MarketplaceSourceEmpty =>
        TextResources.Get("plugin.text.literal_f186f479f8d0");
    public static string MarketplaceSourceUnreadable(string text)
        => F(TextResources.Get("plugin.text.literal_893c03cf01c4"), text);
    public static string MarketplacesFileInvalid(string path, string reason)
        => F(TextResources.Get("plugin.text.literal_cf08df1127a0"), path, reason);
    public static string MarketplaceAdded(string name, string source, int count)
        => F(TextResources.Get("plugin.text.literal_71d06c2ebf7a"), name, source, count);
    public static string MarketplaceAddFailed(string source, string reason)
        => F(TextResources.Get("plugin.text.literal_2fb2d3d4826b"), source, reason);
    public static string MarketplaceExists(string name)
        => F(TextResources.Get("plugin.text.literal_b85eb111ae11"), name);
    public static string MarketplaceNameReserved(string name)
        => F(TextResources.Get("plugin.text.literal_78af86891326"), name, "MCCTeam/DMCBK-Plugins");
    public static string MarketplaceNotFound(string name)
        => F(TextResources.Get("plugin.text.literal_423534994bc4"), name);
    public static string MarketplaceNoneAdded => TextResources.Get("plugin.text.literal_5c820c59051e");
    public static string MarketplaceRemoved(string name) => F(TextResources.Get("plugin.text.literal_6b026d369d27"), name);
    public static string MarketplaceRemovedOrphans(string name, IReadOnlyList<string> ids)
        => F(TextResources.Get("plugin.text.literal_3a15936027c7"), name, string.Join(", ", ids));
    public static string MarketplaceRefreshed(string name, int count)
        => F(TextResources.Get("plugin.text.literal_e0d5145990bb"), name, count);
    public static string MarketplaceRefreshFailed(string name, string reason)
        => F(TextResources.Get("plugin.text.literal_4d9c44f31a77"), name, reason);
    public static string MarketplacePolicySet(string name, string policy)
        => F(TextResources.Get("plugin.text.literal_4ae4b80e0b8d"), name, policy);
    public static string MarketplacePolicyInvalid(string policy)
        => F(TextResources.Get("plugin.text.literal_e8b5da200c29"), policy);

    // resolving a plugin id against the catalogues
    public static string IdNotInMarketplaces(string id)
        => F(TextResources.Get("plugin.text.literal_fc241eb8568c"), id);
    public static string IdNotInMarketplace(string id, string name)
        => F(TextResources.Get("plugin.text.literal_f056362f85ec"), name, id);
    public static string IdAmbiguous(string id, IReadOnlyList<string> candidates)
        => F(TextResources.Get("plugin.text.literal_037c07f3eb47"),
            id, string.Join("\n  ", candidates.Select(c => $"plugins install {c}")));
    public static string CatalogVersionMismatch(string id, string offered, string fetched)
        => F(TextResources.Get("plugin.text.literal_8524ec777f90"), id, offered, fetched);
    public static string RequiresUnresolved(string id, string dependency, string range)
        => F(TextResources.Get("plugin.text.literal_a25e034cc25a"), id, dependency, range);
    public static string InstalledWithDependencies(string id, string version, IReadOnlyList<string> pulled)
        => F(TextResources.Get("plugin.text.literal_cd5f6e9179ad"), id, version, string.Join(", ", pulled));

    // auto-update
    public static string AutoUpdateAvailable(int count)
        => F(TextResources.Get("plugin.text.literal_37919ca51290"), count);
    public static string AutoUpdateApplied(IReadOnlyList<string> ids)
        => F(TextResources.Get("plugin.text.literal_21835d76e292"), string.Join(", ", ids));
    public static string AutoUpdateDeferred(int count)
        => F(TextResources.Get("plugin.text.literal_c1251a4748b8"), count);
    public static string AutoUpdateBlocked(string reason) => F(TextResources.Get("plugin.text.literal_2ae644e95570"), reason);

    // the client-too-old rule
    public static string NeedsNewerApi(string id, string version, string required, string current)
        => F(TextResources.Get("plugin.text.literal_015c88880111"),
            id, version, required, current);
    public static string NeedsNewerMcc(string id, string version, string required, string current)
        => F(TextResources.Get("plugin.text.literal_256046916f5d"),
            id, version, required, current);
    public static string NeedsNewerUmpk(string id, string version, string required, string current)
        => F(TextResources.Get("plugin.text.literal_2e1c80085c02"),
            id, version, required, current);

    /// <summary>
    /// The one confirmation every host shows, rendered here so the classic console, the TUI and a scripted run ask the same question in the same words.
    /// It ends with the no-sandbox warning.
    /// </summary>
    public static string ConfirmInstall(
        string id,
        string version,
        string source,
        string? marketplace,
        string entry,
        bool isSource,
        int lines,
        IReadOnlyList<string> uses,
        IReadOnlyList<string> requires,
        string? replacing,
        IReadOnlyList<string> alsoInstalling)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(replacing is null
            ? F(TextResources.Get("plugin.text.literal_535634a9c42f"), id, version)
            : F(TextResources.Get("plugin.text.literal_67b6ea115e75"), id, replacing, version));
        builder.Append(F(TextResources.Get("plugin.text.literal_80eeb92fec3e"), source));
        if (marketplace is { Length: > 0 })
            builder.Append(F(TextResources.Get("plugin.text.literal_098a41b8d083"), marketplace));

        builder.Append(isSource
            ? F(TextResources.Get("plugin.text.literal_5b43c753902b"), entry, lines)
            : F(TextResources.Get("plugin.text.literal_9bd2860d2ea5"), entry));
        if (uses.Count > 0)
            builder.Append(F(TextResources.Get("plugin.text.literal_5e87af09fd28"), string.Join(", ", uses)));

        if (requires.Count > 0)
            builder.Append(F(TextResources.Get("plugin.text.literal_421213c750cb"), string.Join(", ", requires)));

        if (alsoInstalling.Count > 0)
            builder.Append(F(TextResources.Get("plugin.text.literal_85f1730ff770"), string.Join(", ", alsoInstalling)));

        builder.Append('\n').Append(InProcessNotice);
        return builder.ToString();
    }

    // enable / disable / unload / reload
    public static string Enabled(string id) => F(TextResources.Get("plugin.text.literal_8472f8260439"), id);
    public static string Disabled(string id) => F(TextResources.Get("plugin.text.literal_d091df840796"), id);
    public static string Unloaded(string id) => F(TextResources.Get("plugin.text.literal_c8af96f2f95d"), id);
    public static string AlreadyDisabled(string id) => F(TextResources.Get("plugin.text.literal_76f652b16079"), id);
    public static string Reloaded(string id) => F(TextResources.Get("plugin.text.literal_07c664e7b1b3"), id);
    public static string ReloadedAll(int count) => F(TextResources.Get("plugin.text.literal_a5ae22b79767"), count);
    public static string ReloadedNone => TextResources.Get("plugin.text.literal_f2cafcea8c9d");

    // failures
    public static string LoadFailed(string id, string reason) => F(TextResources.Get("plugin.text.literal_98b4c4e19d68"), id, reason);
    public static string ActivateFailed(string id, string reason) => F(TextResources.Get("plugin.text.literal_1033b947c138"), id, reason);
    public static string CompileFailed(string id, string reason) => F(TextResources.Get("plugin.text.literal_6952a26e3908"), id, reason);
    public static string InProcessNotice => TextResources.Get("plugin.text.literal_870e9cb736ba");

    // teardown (IPlugin.DeactivateAsync)
    public static string TeardownTimedOut(string id, int seconds)
        => F(TextResources.Get("plugin.text.literal_fa46316087b2"), id, seconds);
    public static string TeardownFailed(string id, string reason)
        => F(TextResources.Get("plugin.text.literal_a769869e4a83"), id, reason);

    // command scopes
    public static string SessionCommandsUnowned =>
        TextResources.Get("plugin.text.literal_2636d7538028");

    // messenger (IPluginMessenger contract identity)
    public static string MessengerSubscriberThrew(string owner, string contract)
        => F(TextResources.Get("plugin.text.literal_a4b5ce3cfcda"), owner, contract);
    public static string MessengerResponderTaken(string contract)
        => F(TextResources.Get("plugin.text.literal_902322e3195b"), contract);
    public static string MessengerContractMismatch(
        string contract, string firstOwner, string firstContext, string secondOwner, string secondContext)
        => F(TextResources.Get("plugin.text.literal_e74d69f576ee"),
            contract, firstOwner, firstContext, secondOwner, secondContext);
    // services (IPluginServices)
    public static string ServiceTaken(string contract, string owner)
        => F(TextResources.Get("plugin.text.literal_51a3d5ee0186"), owner, contract);
    public static string ServicePrivateContract(string owner, string contract, string context)
        => F(TextResources.Get("plugin.text.literal_04573ba90c64"),
            owner, contract, context);

    public static string MessengerPrivateContract(string owner, string contract, string context)
        => F(TextResources.Get("plugin.text.literal_774e810ee49b"), owner, contract, context);
}

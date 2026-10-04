using System.Globalization;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// Text the <c>plugins</c> command owns, kept beside the command the way the movement and world commands keep theirs.
/// The listing and the older verbs still read from <see cref="CommandStrings"/> and the corpus; this file is where the verbs added by the plugin-platform work put their lines, so the localization sweep has one place to find them and the command file itself stays free of literals.
/// </summary>
internal static class PluginsCommandText
{
    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>The header above the per-plugin results of a <c>settings all</c> run.</summary>
    public static string SettingsHeader(int count) => F("Settings ({0} plugin(s)):", count);

    /// <summary>One result row under that header.</summary>
    public static string SettingsRow(string message) => F("  {0}", message);

    /// <summary>Said when <c>settings all</c> found no loaded plugin to act on.</summary>
    public const string SettingsNoneLoaded = "No plugin is loaded, so there is no settings file to rewrite.";

    /// <summary>The suggestion token that means "every loaded plugin".</summary>
    public const string All = "all";

    /// <summary>Said when the host attached no plugin market, so nothing can be installed.</summary>
    public const string NoMarket =
        "This host has no plugin market attached, so plugins can only be loaded from disk.";

    // outdated
    public const string OutdatedNone = "Every managed plugin is up to date.";
    public static string OutdatedHeader(int count) => F("Updates available ({0}):", count);
    public static string OutdatedRow(string id, string installed, string available)
        => F("  {0} {1} -> {2}", id, installed, available);
    public static string OutdatedBlocked(string reason) => F("      held back: {0}", reason);
    public static string OutdatedPinned(string id) => F("      pinned: run 'plugins unpin {0}' to move it", id);
    public static string OutdatedError(string id, string reason) => F("  {0}: {1}", id, reason);

    // info
    public static string InfoHeader(string id, string version) => F("{0} {1}", id, version);
    public static string InfoRow(string label, string value) => F("  {0,-11}{1}", label, value);
    public const string InfoLabelState = "state";
    public const string InfoLabelEntry = "entry";
    public const string InfoLabelFolder = "folder";
    public const string InfoLabelSource = "source";
    public const string InfoLabelMarket = "market";
    public const string InfoLabelInstalled = "installed";
    public const string InfoLabelPinned = "pinned";
    public const string InfoLabelDescription = "about";
    public const string InfoLabelHomepage = "homepage";
    public const string InfoLabelTags = "tags";
    public const string InfoLabelUses = "uses";
    public const string InfoLabelRequires = "requires";
    public const string InfoLabelOptional = "optional";
    public const string InfoLabelDependents = "needed by";
    public const string InfoLabelExports = "exports";
    public const string InfoLabelLanguages = "languages";
    public const string InfoLabelManual = "manual";
    public const string InfoLabelOffline = "offline";
    public const string InfoLabelErrors = "errors";
    public const string InfoOffline = "yes: it does useful work with no server connected";
    public static string InfoFaults(int count, string last) => F("{0} since it loaded. {1}", count, last);

    /// <summary>The marker <c>list</c> puts on a plugin that declares <c>[services] offline</c>.</summary>
    public const string ListOffline = " (offline)";
    public const string InfoLocal = "local (installed by hand, not managed by MCC)";
    public const string InfoLoaded = "loaded";
    public const string InfoDisabled = "disabled";
    public const string InfoNotLoaded = "not loaded";
    public static string InfoPinnedAt(string version) => F("held at {0}", version);

    // ui
    /// <summary>Said when the host has no dialog UI for the visual plugin manager.</summary>
    public const string UiNoDialog =
        "The plugin manager needs a host with dialogs. Use the TUI, or the text verbs above.";

    // deps
    public static string DepsHeader(string id) => F("{0} dependencies:", id);
    public const string DepsNone = "  nothing: it needs no other plugin, and no other plugin needs it";
    public static string DepsRequires(string list) => F("  requires   {0}", list);
    public static string DepsOptional(string list) => F("  optional   {0}", list);
    public static string DepsDependents(string list) => F("  needed by  {0}", list);
    public static string DepsMissing(string list) => F("  MISSING    {0}", list);

    // new
    /// <summary>Said when the word after <c>new</c> is not a usable plugin id.</summary>
    public const string NewUsage =
        "Usage: plugins new <id>. The id is lower-case letters, digits and '-', starting with a letter.";

    // validate
    public const string ValidateNothing = "No plugin folder to check.";
    public static string ValidateHeader(int count, int bad)
        => bad == 0
            ? F("Checked {0} plugin(s); all of them are sound.", count)
            : F("Checked {0} plugin(s); {1} of them will not work as written.", count, bad);
    public static string ValidateSubject(string id) => F("  {0}:", id);
    public static string ValidateSubjectOk(string id) => F("  {0}: ok", id);
    public static string ValidateProblem(string severity, string where, string message)
        => F("    {0}{1}{2}", severity, where.Length == 0 ? "  " : F(" {0}  ", where), message);
    public const string SeverityError = "error";
    public const string SeverityWarning = "warn ";
    public static string ValidateAt(string file, int line)
        => line > 0 ? F("{0}:{1}", file, line) : file;

    // doctor
    public static string DoctorHeader(int discovered, int loaded)
        => F("Plugins: {0} discovered, {1} loaded.", discovered, loaded);
    public const string DoctorHealthy = "  Nothing to report.";
    public static string DoctorRow(string severity, string id, string detail)
        => F("  {0} {1}: {2}", severity, id, detail);
    public const string DoctorNote = "note ";

    // list filters
    public const string FilterOutdated = "outdated";
    public const string FilterLocal = "local";
    public const string FilterEnabled = "enabled";
    public const string FilterDisabled = "disabled";
    public static string ListFilteredNone(string filter) => F("No plugin is {0}.", filter);

    /// <summary>The token that answers the install confirmation from a script.</summary>
    public const string Yes = "yes";

    /// <summary>Said when the words after <c>install</c> are not a source, an optional version and a yes.</summary>
    public const string InstallUsage =
        "Usage: plugins install <what> [<version>] [yes]. <what> is a GitHub owner/repo, a Git or archive "
        + "URL, or a folder; quote it if it contains a space.";

    /// <summary>The token that makes an uninstall delete settings and data too.</summary>
    public const string Purge = "purge";

    // marketplace
    /// <summary>The subcommand that groups the marketplace verbs, and its alias.</summary>
    public const string Marketplace = "marketplace";

    /// <summary>The shorter spelling of <see cref="Marketplace"/>.</summary>
    public const string MarketAlias = "market";

    /// <summary>The keyword that names a marketplace on <c>marketplace add</c>.</summary>
    public const string As = "as";

    /// <summary>The keyword that narrows <c>search</c> to one marketplace.</summary>
    public const string In = "in";

    public const string MarketplaceNone =
        "No marketplaces are added. Add one with 'plugins marketplace add <owner/repo|url|folder>'.";
    public static string MarketplaceHeader(int count) => F("Marketplaces ({0}):", count);
    public static string MarketplaceRow(string name, string source, int count, string policy)
        => F("  {0} - {1} ({2} plugin(s), auto-update {3})", name, source, count, policy);
    public static string MarketplaceRefreshedAt(string when) => F("      refreshed {0}", when);
    public const string MarketplaceNeverRefreshed = "      never refreshed";
    public static string MarketplaceRowError(string reason) => F("      {0}", reason);

    /// <summary>Said when the words after <c>marketplace add</c> are not a source and an optional name.</summary>
    public const string MarketplaceAddUsage =
        "Usage: plugins marketplace add <source> [as <name>]. <source> is a GitHub owner/repo, a Git URL, an "
        + "HTTPS URL ending in mcc-marketplace.toml, or a folder; quote it if it contains a space.";

    /// <summary>Said when the words after <c>search</c> are not a text and an optional marketplace.</summary>
    public const string SearchUsage = "Usage: plugins search <text> [in <marketplace>].";

    public static string SearchNone(string text) => F("Nothing in the added marketplaces matches '{0}'.", text); public static string SearchHeader(int count) => F("Found {0}:", count);
    public static string SearchRow(string id, string marketplace, string version, string description)
        => F("  {0}@{1} {2}{3}", id, marketplace, version, string.IsNullOrWhiteSpace(description) ? "" : $" - {description}");
    public static string SearchInstalled(string version) => F("      installed: {0}", version);
    public static string SearchBlocked(string reason) => F("      unavailable: {0}", reason);
}

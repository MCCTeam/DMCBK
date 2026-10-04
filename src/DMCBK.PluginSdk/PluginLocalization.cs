using System.Globalization;
using Tomlet;
using Tomlet.Models;

namespace DMCBK.PluginSdk;

/// <summary>
/// A plugin's own translated strings.
/// <para>
/// MCC's strings live in a generated resx corpus that a plugin cannot add to, so plugins get a parallel mechanism with the same shape and the same fallback rules.
/// A plugin ships one TOML file per language beside its manifest:
/// </para>
/// <code>
/// plugins/Farmer/
///   plugin.toml
///   Farmer.cs
///   lang/
///     en.toml          key = "text"
///     de.toml
///     pt-BR.toml
///   man/
///     en/farmer.md
///     de/farmer.md
/// </code>
/// <para>
/// TOML rather than resx because a plugin author is already writing TOML for the manifest and the settings file, and because a translator can edit it without an XML editor.
/// Lookup walks the culture chain the way <c>ResourceManager</c> does (<c>pt-BR</c>, then <c>pt</c>, then <c>en</c>), so a partial translation falls back per key rather than per file.
/// </para>
/// </summary>
public interface IPluginLocalization
{
    /// <summary>
    /// The text for a key in the current UI language, falling back through the culture chain to English.
    /// Returns the key itself when nothing has it, which is the same thing MCC's own corpus does: a missing string shows up on screen as its key rather than as blank space.
    /// </summary>
    string Get(string key);

    /// <summary>Resolves a key and formats it with the current culture.</summary>
    string Format(string key, params object?[] args);

    /// <summary>The languages this plugin ships strings for.</summary>
    IReadOnlyList<string> Languages { get; }

    /// <summary>
    /// What this plugin's translation looks like from one language's point of view: the tags it ships, the one that actually answers for <paramref name="culture"/>, and the keys that language is missing against English.
    /// <c>/lang</c> and <c>/plugins list</c> render it; a translator reads it to see what is left to do.
    /// <para>
    /// The default implementation reports the shipped tags and nothing else, so an implementation written against an earlier SDK keeps compiling.
    /// </para>
    /// </summary>
    PluginLocalizationReport Report(CultureInfo culture) => new(Languages, null, []);
}

/// <summary>
/// One plugin's translation coverage for one language, from <see cref="IPluginLocalization.Report"/>.
/// </summary>
/// <param name="Languages">Every language tag the plugin ships a file for, ordered.</param>
/// <param name="Active">
/// The shipped tag that answers for the requested culture, walking the culture chain and then English, or null when the plugin ships nothing that language could reach.
/// </param>
/// <param name="MissingKeys">
/// Keys English has that <paramref name="Active"/> does not, ordered.
/// Empty when the active language is English itself, when it is complete, or when the plugin ships no English table to compare against.
/// </param>
public sealed record PluginLocalizationReport(
    IReadOnlyList<string> Languages, string? Active, IReadOnlyList<string> MissingKeys);

/// <summary>
/// <see cref="IPluginLocalization"/> over a plugin's <c>lang/</c> folder.
/// Files are read once and cached; reloading the plugin builds a new instance, which is how a translator sees an edit.
/// </summary>
public sealed class PluginLocalization : IPluginLocalization
{
    /// <summary>The folder name, relative to the plugin folder, holding the language files.</summary>
    public const string FolderName = "lang";

    private const string FallbackLanguage = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _byLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private CultureInfo _culture;

    /// <summary>Loads every language file in a plugin folder. A folder with none yields an empty table.</summary>
    /// <param name="pluginFolder">The plugin's own folder (the one holding <c>plugin.toml</c>).</param>
    /// <param name="culture">
    /// The language to read in.
    /// This is the host's RESOLVED UI culture, not the operating system's: the whole point of <c>Localization.Language</c> is that it can override the OS, and reading <see cref="CultureInfo.CurrentUICulture"/> here would quietly ignore it on any thread the host has not retargeted.
    /// Null means the ambient UI culture, which is what a caller that has not resolved one would have got anyway.
    /// </param>
    public PluginLocalization(string pluginFolder, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(pluginFolder);
        _culture = culture ?? CultureInfo.CurrentUICulture;
        string root = Path.Combine(pluginFolder, FolderName);
        if (!Directory.Exists(root))
            return;

        foreach (string path in Directory.EnumerateFiles(root, "*.toml"))
        {
            string language = Path.GetFileNameWithoutExtension(path);
            if (language.Length == 0)
                continue;

            try
            {
                _byLanguage[language] = Flatten(new TomlParser().Parse(File.ReadAllText(path)));
            }
            // Tomlet's parse failures are its own exception hierarchy and a malformed language file must not stop a plugin loading, so this catches broadly on purpose.
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A broken language file must not stop the plugin loading.
                // The keys it would have supplied fall through to the next language, and ultimately show as their own names.
            }
        }
    }

    /// <summary>
    /// The language this table reads in.
    /// Settable so <c>/lang &lt;tag&gt;</c> can retarget a plugin that is already loaded, which is what makes the setting take effect without a restart; the files themselves were all read at construction, so switching costs nothing.
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _culture = value;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Languages => [.. _byLanguage.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc/>
    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        for (CultureInfo? c = _culture;
             c is not null && c != CultureInfo.InvariantCulture;
             c = c.Parent)
        {
            if (c.Name.Length > 0
                && _byLanguage.TryGetValue(c.Name, out Dictionary<string, string>? table)
                && table.TryGetValue(key, out string? text))
                return text;
        }

        return _byLanguage.TryGetValue(FallbackLanguage, out Dictionary<string, string>? fallback)
            && fallback.TryGetValue(key, out string? english)
                ? english
                : key;
    }

    /// <inheritdoc/>
    public string Format(string key, params object?[] args)
        => string.Format(_culture, Get(key), args ?? []);

    /// <inheritdoc/>
    public PluginLocalizationReport Report(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        string? active = null;
        for (CultureInfo? c = culture; c is not null && c != CultureInfo.InvariantCulture; c = c.Parent)
        {
            if (c.Name.Length > 0 && _byLanguage.ContainsKey(c.Name))
            {
                active = c.Name;
                break;
            }
        }

        active ??= _byLanguage.ContainsKey(FallbackLanguage) ? FallbackLanguage : null;

        // Missing keys are counted against English, because English is what the fallback chain ends at and therefore the only complete table a plugin is expected to ship.
        // A plugin with no English table has no yardstick, and a run already reading English has nothing to be missing from.
        if (active is null
            || active.Equals(FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            || !_byLanguage.TryGetValue(FallbackLanguage, out Dictionary<string, string>? english))
            return new PluginLocalizationReport(Languages, active, []);

        Dictionary<string, string> table = _byLanguage[active];
        return new PluginLocalizationReport(
            Languages,
            active,
            [.. english.Keys.Where(k => !table.ContainsKey(k)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Flattens nested tables to dotted keys, so <c>[farmer] planted = "..."</c> is reachable as <c>farmer.planted</c>.
    /// Nesting is how a translator keeps a long file organised; dotted keys are how the code reads them, exactly as the resx corpus works.
    /// </summary>
    private static Dictionary<string, string> Flatten(TomlDocument document)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(document, string.Empty, flat);
        return flat;

        static void Walk(TomlTable table, string prefix, Dictionary<string, string> into)
        {
            foreach ((string key, TomlValue value) in table.Entries)
            {
                string path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (value is TomlTable nested)
                    Walk(nested, path, into);
                else
                    into[path] = value.StringValue;
            }
        }
    }
}

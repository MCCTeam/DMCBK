using System.Globalization;
using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging;
using Tomlet;

namespace DMCBK.PluginSdk;

/// <summary>
/// Implement this on a plugin's POCO settings type to hook the validate/normalize step of the settings pipeline.
/// It runs on every load and reload: clamp out-of-range values, swap inverted ranges, normalize strings, or self-disable when misconfigured.
/// Runtime state must NOT live in the settings object; keep it in the plugin instead (the settings object is purely the persisted, user-editable surface).
/// </summary>
public interface IValidatablePluginSettings
{
    /// <summary>Clamps/normalizes/self-disables in place. Called after deserialization and on reload.</summary>
    void Validate();
}

/// <summary>
/// The typed settings surface for one plugin.
/// It owns the plugin's <c>settings.toml</c>: on first run it writes commented defaults, on every load it deserializes then runs the optional <see cref="IValidatablePluginSettings.Validate"/> hook, and <c>reload</c> re-runs the same pipeline.
/// TOML is produced/consumed through the same Tomlet path the core config uses.
/// Comments are declared with Tomlet's <c>[TomlPrecedingComment]</c> / <c>[TomlInlineComment]</c> attributes on the POCO.
/// <para>
/// A comment written as <c>$key$</c> is resolved through the plugin's own <see cref="IPluginLocalization"/> when the file is written, so a plugin's settings are documented in the reader's language exactly the way <c>client.toml</c> is.
/// A comment that is not a placeholder is written through unchanged.
/// </para>
/// </summary>
public sealed class PluginSettings
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly IPluginLocalization _strings;
    private readonly string? _defaultsPath;

    internal PluginSettings(string settingsFilePath, ILogger logger, IPluginLocalization? strings = null)
        : this(settingsFilePath, logger, strings, null) { }

    internal PluginSettings(string settingsFilePath, ILogger logger, IPluginLocalization? strings, string? defaultsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        ArgumentNullException.ThrowIfNull(logger);
        _path = settingsFilePath;
        _defaultsPath = defaultsPath;
        _logger = logger;
        _strings = strings ?? EmptyPluginLocalization.Instance;
    }

    /// <summary>The absolute path of the backing <c>settings.toml</c>.</summary>
    public string FilePath => _path;

    /// <summary>Whether the settings file exists on disk.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>
    /// Loads the settings, running the full pipeline: when the file is missing it materializes defaults, validates, and writes the commented file; when it exists it deserializes and validates.
    /// A parse failure logs and falls back to validated defaults (the plugin keeps running).
    /// </summary>
    public T Load<T>()
        where T : class, new()
    {
        if (!File.Exists(_path))
        {
            T defaults = LoadDefaults<T>();
            Validate(defaults);
            Save(defaults);
            return defaults;
        }

        try
        {
            var document = new TomlParser().Parse(DefaultText());
            Merge(document, new TomlParser().Parse(File.ReadAllText(_path)));
            T value = TomletMain.To<T>(document);
            Validate(value);
            return value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to parse {Path}; using validated defaults.", _path);
            T defaults = LoadDefaults<T>();
            Validate(defaults);
            return defaults;
        }
    }

    private string DefaultText() => _defaultsPath is not null && File.Exists(_defaultsPath)
        ? File.ReadAllText(_defaultsPath) : string.Empty;

    private T LoadDefaults<T>() where T : class, new()
        => _defaultsPath is not null && File.Exists(_defaultsPath) ? TomletMain.To<T>(DefaultText()) : new T();

    private static void Merge(Tomlet.Models.TomlTable defaults, Tomlet.Models.TomlTable user)
    {
        foreach ((string key, Tomlet.Models.TomlValue value) in user.Entries)
        {
            if (value is Tomlet.Models.TomlTable userTable && defaults.Entries.TryGetValue(key, out var prior)
                && prior is Tomlet.Models.TomlTable defaultTable) Merge(defaultTable, userTable);
            else defaults.Entries[key] = value;
        }
    }

    /// <summary>Serializes the settings to the backing file (commented, invariant-culture, deterministic).</summary>
    public void Save<T>(T settings)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        string toml;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            toml = TomletMain.TomlStringFrom(settings);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }

        // The same expansion step client.toml gets, resolved against this plugin's lang/ table instead of MCC's corpus.
        // EmptyPluginLocalization returns the key itself, which is also what the corpus does for a key nothing has, so a plugin with no lang/ folder and a $placeholder$ comment shows the key.
        File.WriteAllText(_path, ConfigCommentExpander.Expand(toml, Resolve));
    }

    /// <summary>
    /// Resolves one placeholder key.
    /// Null when the table answered with the key itself, so the expander falls back the same way the core writer does rather than printing a key that came back dressed as text.
    /// </summary>
    private string? Resolve(string key)
    {
        string text = _strings.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? null : text;
    }

    private static void Validate<T>(T value)
        where T : class
    {
        if (value is IValidatablePluginSettings validatable)
            validatable.Validate();
    }
}

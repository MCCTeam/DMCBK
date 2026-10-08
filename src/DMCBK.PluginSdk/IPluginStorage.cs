using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Tomlet;

namespace DMCBK.PluginSdk;

/// <summary>
/// A plugin's persistence sandbox: a plugin-owned <c>data/</c> directory plus a small string key/value store persisted inside it.
/// It holds log files, roster dumps, and small state under this sandbox instead of CWD-relative paths.
/// Path helpers keep writes inside the sandbox.
/// </summary>
public interface IPluginStorage
{
    /// <summary>The absolute path of the plugin's <c>data/</c> directory (created on demand).</summary>
    string DataDirectory { get; }

    /// <summary>Resolves a relative name to an absolute path inside the sandbox, creating parent folders.</summary>
    string GetPath(string relativeName);

    /// <summary>Reads a persisted key; false when absent.</summary>
    bool TryGet(string key, out string? value);

    /// <summary>Sets a persisted key (call <see cref="Save"/> to flush to disk).</summary>
    void Set(string key, string value);

    /// <summary>Removes a persisted key (call <see cref="Save"/> to flush to disk).</summary>
    bool Remove(string key);

    /// <summary>Flushes the key/value store to <c>data/storage.toml</c>.</summary>
    void Save();
}

/// <summary>The default <see cref="IPluginStorage"/>: a folder sandbox and a TOML-backed key/value store.</summary>
internal sealed class PluginStorage : IPluginStorage
{
    private const string StoreFileName = "storage.toml";

    private readonly string _dataDirectory;
    private readonly ILogger _logger;
    private readonly Lock _saveGate = new();
    private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);

    internal PluginStorage(string dataDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _logger = logger;
        LoadStore();
    }

    /// <inheritdoc/>
    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(_dataDirectory);
            return _dataDirectory;
        }
    }

    /// <inheritdoc/>
    public string GetPath(string relativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeName);
        string combined = Path.GetFullPath(Path.Combine(_dataDirectory, relativeName));
        string root = _dataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal)
            && !string.Equals(combined, _dataDirectory, StringComparison.Ordinal))
            throw new ArgumentException($"'{relativeName}' escapes the plugin data sandbox.", nameof(relativeName));

        string? parent = Path.GetDirectoryName(combined);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        return combined;
    }

    /// <inheritdoc/>
    public bool TryGet(string key, out string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _kv.TryGetValue(key, out value);
    }

    /// <inheritdoc/>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _kv[key] = value;
    }

    /// <inheritdoc/>
    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _kv.TryRemove(key, out _);
    }

    /// <inheritdoc/>
    public void Save()
    {
        lock (_saveGate)
        {
            Directory.CreateDirectory(_dataDirectory);
            var snapshot = new Dictionary<string, string>(_kv, StringComparer.Ordinal);

            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            string toml;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                toml = snapshot.Count == 0 ? string.Empty : TomletMain.TomlStringFrom(snapshot);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }

            // Replace a complete file on the same volume so readers never observe a partial write.
            string temporaryPath = Path.Combine(_dataDirectory, $".{StoreFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, toml);
                string destinationPath = Path.Combine(_dataDirectory, StoreFileName);
                if (File.Exists(destinationPath))
                    File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, destinationPath);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void LoadStore()
    {
        string path = Path.Combine(_dataDirectory, StoreFileName);
        if (!File.Exists(path))
            return;

        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return;

            var loaded = TomletMain.To<Dictionary<string, string>>(text);
            foreach (KeyValuePair<string, string> pair in loaded)
                _kv[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to load plugin storage from {Path}.", path);
        }
    }
}

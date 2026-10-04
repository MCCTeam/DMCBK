using System.Text;
using DMCBK.Core.Configuration;
using Tomlet;
using Tomlet.Models;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Refuses a Beacon state path that would leave the per-script state jail.
/// Carries an actionable message naming the jail; never carries file content.
/// </summary>
public sealed class BeaconStateEscapeException : ArgumentException
{
    /// <summary>Builds the exception with an actionable message.</summary>
    public BeaconStateEscapeException(string message)
        : base(message)
    {
    }

    /// <summary>Builds the exception with an actionable message and a parameter name.</summary>
    public BeaconStateEscapeException(string message, string? paramName)
        : base(message, paramName)
    {
    }
}

/// <summary>
/// Refuses a Beacon state write that would exceed a quota.
/// The message always names the limit.
/// </summary>
public sealed class BeaconQuotaExceededException : InvalidOperationException
{
    /// <summary>Builds the exception with a message naming the exceeded limit.</summary>
    public BeaconQuotaExceededException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Encodes <see cref="BeaconValue"/> to and from Tomlet nodes for the per-script <c>saved</c> files.
/// TOML has no null, so <c>none</c> round-trips as a single-entry marker table; a hand-authored table that happens to equal the marker decodes as <c>none</c> (documented, single-key collision only).
/// Whole doubles that fit exactly become integers; anything TOML cannot express (dates, times) fails closed with <see cref="InvalidDataException"/> naming the key.
/// </summary>
internal static class BeaconValueTomlCodec
{
    /// <summary>The marker table key that encodes <c>none</c>.</summary>
    internal const string NoneMarkerKey = "__beacon_none__";

    private const double MaxExactInteger = 9007199254740992.0;

    /// <summary>Encodes a value to a Tomlet node.</summary>
    internal static TomlValue Encode(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconTextValue t => new TomlString(t.Value),
            BeaconNumberValue n => EncodeNumber(n.Value),
            BeaconYesNoValue b => TomlBoolean.ValueOf(b.Value),
            BeaconListValue l => EncodeList(l),
            BeaconMapValue m => EncodeMap(m),
            BeaconNoneValue => EncodeNone(),
            _ => throw new InvalidOperationException($"Cannot persist a Beacon value of kind '{value.Kind}'."),
        };
    }

    /// <summary>Decodes a Tomlet node; <paramref name="what"/> names the key for errors.</summary>
    internal static BeaconValue Decode(TomlValue node, string what)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        return node switch
        {
            TomlString s => BeaconValue.Text(s.Value),
            TomlLong l => BeaconValue.Number(l.Value),
            TomlDouble d => BeaconValue.Number(d.Value),
            TomlBoolean b => BeaconValue.YesNo(b.Value),
            TomlArray a => DecodeList(a, what),
            TomlTable t => DecodeTable(t, what),
            _ => throw new InvalidDataException(
                $"Beacon saved key '{what}' holds a TOML date or time, which scripts cannot express. "
                + "Edit the file back to text, number, yes/no, list, or map."),
        };
    }

    /// <summary>Deep-copies maps and lists so callers can never alias store internals.</summary>
    internal static BeaconValue Clone(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconListValue l => BeaconValue.List(l.Items.Select(Clone).ToList<BeaconValue>()),
            BeaconMapValue m => BeaconValue.Map(
                m.Entries.ToDictionary(e => e.Key, e => Clone(e.Value), StringComparer.Ordinal)),
            _ => value,
        };
    }

    /// <summary>Quota measure: UTF-8 bytes of the value's display form.</summary>
    internal static long MeasureBytes(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value.ToDisplayString());
    }

    private static TomlValue EncodeNumber(double value)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value)
            && value is > -MaxExactInteger and < MaxExactInteger
            && value == Math.Truncate(value))
            return new TomlLong((long)value);

        return new TomlDouble(value);
    }

    private static TomlArray EncodeList(BeaconListValue list)
    {
        var array = new TomlArray();
        foreach (BeaconValue item in list.Items)
            array.ArrayValues.Add(Encode(item));

        return array;
    }

    private static TomlTable EncodeMap(BeaconMapValue map)
    {
        var table = new TomlTable();
        foreach ((string key, BeaconValue entry) in map.Entries)
            table.PutValue(key, Encode(entry));

        return table;
    }

    private static TomlTable EncodeNone()
    {
        var table = new TomlTable();
        table.PutValue(NoneMarkerKey, TomlBoolean.ValueOf(true));
        return table;
    }

    private static BeaconValue DecodeList(TomlArray array, string what)
    {
        var items = new List<BeaconValue>();
        int index = 0;
        foreach (TomlValue child in array)
        {
            items.Add(Decode(child, $"{what}[{index}]"));
            index++;
        }

        return BeaconValue.List(items);
    }

    private static BeaconValue DecodeTable(TomlTable table, string what)
    {
        var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach ((string key, TomlValue child) in table)
            entries[key] = Decode(child, string.IsNullOrEmpty(what) ? key : $"{what}.{key}");

        if (entries.Count == 1
            && entries.TryGetValue(NoneMarkerKey, out BeaconValue? marker)
            && marker is BeaconYesNoValue yesNo
            && yesNo.Value)
            return BeaconValue.None;

        return BeaconValue.Map(entries);
    }
}

/// <summary>
/// The per-script <c>saved</c> store: TOML files at <c>configurations/beacon/&lt;script&gt;.toml</c>, one file per script, written atomically (tmp plus rename) so a crash mid-write never tears them.
/// </summary>
/// <remarks>
/// <para>Standalone by design.</para>
/// <para><see cref="BeaconEngine"/> owns one instance and bridges <c>saved("key")</c> reads plus <c>save "key" to value</c> writes into it on the load path.</para>
/// <para>This class never touches <c>accounts.toml</c>, proxy credentials, or token caches: script ids are plain names validated by <see cref="ValidateScriptId"/>, every resolved path is proven to stay inside the <c>beacon/</c> jail, and a state file that is (or sits under) a symbolic link fails closed with <see cref="BeaconStateEscapeException"/>.</para>
/// <para>No secrets ever enter snapshots or messages here.</para>
/// <para>
/// Quotas (config-overridable later): at most <see cref="MaxKeys"/> keys per script, at most <see cref="MaxValueBytes"/> bytes per value.
/// A plain <see cref="Load"/> never writes; generation happens only through <see cref="Set"/> plus <see cref="Save"/> (explicit-write-only).
/// </para>
/// </remarks>
public sealed class BeaconSavedState
{
    /// <summary>Maximum distinct saved keys per script.</summary>
    public const int MaxKeys = 1024;

    /// <summary>Maximum bytes per saved value (UTF-8 bytes of its display form).</summary>
    public const long MaxValueBytes = 1024L * 1024L;

    /// <summary>Maximum characters per script id (state file names stay portable).</summary>
    public const int MaxScriptIdLength = 64;

    /// <summary>Maximum characters per saved key.</summary>
    public const int MaxKeyLength = 256;

    private const string TmpMarker = ".tmp.";

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string _beaconDir;
    private readonly Dictionary<string, Dictionary<string, BeaconValue>> _cache = new(StringComparer.Ordinal);

    /// <summary>Builds a store over a configurations folder (the <c>beacon/</c> dir lives inside it).</summary>
    public BeaconSavedState(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        _folder = Path.GetFullPath(configurationsFolder);
        _beaconDir = ConfigurationPaths.BeaconDir(_folder);
    }

    /// <summary>The resolved configurations folder.</summary>
    public string ConfigurationsFolder => _folder;

    /// <summary>The per-script state jail: <c>configurations/beacon/</c>.</summary>
    public string BeaconDirectory => _beaconDir;

    /// <summary>
    /// Fault-injection seam for tests: invoked with the fully-flushed tmp path after the write and before the rename.
    /// A throw leaves the previous file byte-identical (the torn-write proof).
    /// Production code never sets this.
    /// </summary>
    internal Action<string>? BeforeRenameForTests { get; set; }

    /// <summary>
    /// Validates a script id as a plain state-file name.
    /// Traversal shapes (separators, rooted paths, bare dots) fail with <see cref="BeaconStateEscapeException"/>; length and charset failures throw <see cref="ArgumentException"/>.
    /// Pass <paramref name="jailDir"/> so refusal messages name the jail they protect.
    /// </summary>
    public static void ValidateScriptId(string scriptId, string? jailDir = null)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
            throw new ArgumentException("Beacon script id must not be empty.", nameof(scriptId));

        if (scriptId.Length > MaxScriptIdLength)
        {
            throw new ArgumentException(
                $"Beacon script id '{scriptId}' is {scriptId.Length} characters; the limit is {MaxScriptIdLength}.",
                nameof(scriptId));
        }

        if (scriptId is "." or ".."
            || scriptId.Contains('/') || scriptId.Contains('\\')
            || Path.IsPathRooted(scriptId))
        {
            throw new BeaconStateEscapeException(
                $"Refusing Beacon state access for script id '{scriptId}': that is not a plain script name. "
                + $"Script ids are letters, digits, '_', '-', '.', at most {MaxScriptIdLength} characters; "
                + $"state files live only as '<configurations>/beacon/<id>.toml'."
                + (jailDir is null ? string.Empty : $" The state jail is '{jailDir}'."),
                nameof(scriptId));
        }

        foreach (char c in scriptId)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                throw new ArgumentException(
                    $"Beacon script id '{scriptId}' contains '{c}'; use only letters, digits, '_', '-', '.'.",
                    nameof(scriptId));
            }
        }
    }

    /// <summary>
    /// Resolves a script id to its jailed state file, proving the result stays inside <see cref="BeaconDirectory"/> and refusing symbolic-link files.
    /// Pure: creates nothing.
    /// </summary>
    public string ResolveStatePath(string scriptId)
    {
        ValidateScriptId(scriptId, _beaconDir);
        string combined = Path.GetFullPath(Path.Combine(_beaconDir, scriptId + ".toml"));
        string root = _beaconDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            throw new BeaconStateEscapeException(
                $"Refusing Beacon state access for script id '{scriptId}': '{combined}' would leave the "
                + $"per-script state jail '{root}'. State files live only as '<beacon-dir>/<id>.toml'.",
                nameof(scriptId));
        }

        if (File.Exists(combined) && new FileInfo(combined).LinkTarget is not null)
        {
            throw new BeaconStateEscapeException(
                $"Refusing Beacon state access for script id '{scriptId}': '{combined}' is a symbolic link. "
                + $"State files must be regular files inside '{root}'.",
                nameof(scriptId));
        }

        return combined;
    }

    /// <summary>
    /// (Re)loads one script's table from disk, replacing the in-memory copy.
    /// A missing or empty file loads as empty and writes nothing.
    /// A file that does not parse fails with <see cref="InvalidDataException"/> naming the file.
    /// This is the reload primitive behind "tasks reset AND <c>saved</c> survives".
    /// </summary>
    public void Load(string scriptId)
    {
        ValidateScriptId(scriptId, _beaconDir);
        lock (_gate)
        {
            EnsureBeaconDir();
            string path = ResolveStatePath(scriptId);
            Dictionary<string, BeaconValue> table = StoreLocked(scriptId);
            table.Clear();
            if (!File.Exists(path))
                return;

            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return;

            TomlDocument document;
            try
            {
                document = new TomlParser().Parse(text);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Beacon saved-state file '{path}' for script '{scriptId}' does not parse: {ex.Message}", ex);
            }

            foreach ((string key, TomlValue node) in document)
            {
                try
                {
                    table[key] = BeaconValueTomlCodec.Decode(node, key);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException(
                        $"Beacon saved-state file '{path}' for script '{scriptId}' key '{key}': {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>Reads a saved key; false when the script or key is absent.</summary>
    public bool TryGet(string scriptId, string key, out BeaconValue? value)
    {
        ValidateScriptId(scriptId, _beaconDir);
        ValidateKey(key);
        lock (_gate)
        {
            if (_cache.TryGetValue(scriptId, out Dictionary<string, BeaconValue>? table)
                && table.TryGetValue(key, out BeaconValue? found))
            {
                value = BeaconValueTomlCodec.Clone(found);
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>Reads a saved key, or <see cref="BeaconValue.None"/> when absent (the <c>or</c>-default idiom).</summary>
    public BeaconValue GetOrNone(string scriptId, string key)
        => TryGet(scriptId, key, out BeaconValue? value) && value is not null ? value : BeaconValue.None;

    /// <summary>
    /// Stages a saved key in memory (quota-checked).
    /// Call <see cref="Save"/> to flush to disk.
    /// </summary>
    public void Set(string scriptId, string key, BeaconValue value)
    {
        ValidateScriptId(scriptId, _beaconDir);
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        long size = BeaconValueTomlCodec.MeasureBytes(value);
        if (size > MaxValueBytes)
        {
            throw new BeaconQuotaExceededException(
                $"Beacon saved store for script '{scriptId}' refused key '{key}': the value is {size} bytes, "
                + $"over the {MaxValueBytes}-byte per-value limit.");
        }

        lock (_gate)
        {
            Dictionary<string, BeaconValue> table = StoreLocked(scriptId);
            if (!table.ContainsKey(key) && table.Count >= MaxKeys)
            {
                throw new BeaconQuotaExceededException(
                    $"Beacon saved store for script '{scriptId}' refused key '{key}': the script already holds "
                    + $"the {MaxKeys}-key limit.");
            }

            table[key] = BeaconValueTomlCodec.Clone(value);
        }
    }

    /// <summary>Discards a staged key from memory; returns false when absent. Call <see cref="Save"/> to flush.</summary>
    public bool Remove(string scriptId, string key)
    {
        ValidateScriptId(scriptId, _beaconDir);
        ValidateKey(key);
        lock (_gate)
        {
            return _cache.TryGetValue(scriptId, out Dictionary<string, BeaconValue>? table)
                && table.Remove(key);
        }
    }

    /// <summary>
    /// Flushes one script's table to disk atomically: fully write plus flush a tmp sibling, then rename over the target.
    /// A crash before the rename leaves the previous file byte-identical; stale tmp files are swept best-effort afterwards.
    /// </summary>
    public void Save(string scriptId)
    {
        ValidateScriptId(scriptId, _beaconDir);
        lock (_gate)
        {
            EnsureBeaconDir();
            string path = ResolveStatePath(scriptId);
            Dictionary<string, BeaconValue> table = StoreLocked(scriptId);
            CheckQuotasLocked(scriptId, table);

            var document = TomlDocument.CreateEmpty();
            foreach ((string key, BeaconValue value) in table.OrderBy(e => e.Key, StringComparer.Ordinal))
                document.PutValue(key, BeaconValueTomlCodec.Encode(value));

            string text = "# Beacon saved state for script '"
                + scriptId
                + "'. Written atomically (tmp plus rename); a crash mid-write never tears this file. "
                + "Safe to delete to reset the script.\n"
                + document.SerializedValue;

            string tmp = path + TmpMarker + Guid.NewGuid().ToString("N");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                BeforeRenameForTests?.Invoke(tmp);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // Best effort: a leftover tmp is swept on the next save and never read.
                }

                throw;
            }

            SweepTmpFiles();
        }
    }

    /// <summary>Copies one script's staged table (cloned values).</summary>
    public IReadOnlyDictionary<string, BeaconValue> Snapshot(string scriptId)
    {
        ValidateScriptId(scriptId, _beaconDir);
        lock (_gate)
        {
            if (!_cache.TryGetValue(scriptId, out Dictionary<string, BeaconValue>? table))
                return new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

            return table.ToDictionary(e => e.Key, e => BeaconValueTomlCodec.Clone(e.Value), StringComparer.Ordinal);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Beacon saved key must not be empty.", nameof(key));

        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"Beacon saved key is {key.Length} characters; the limit is {MaxKeyLength}.", nameof(key));
        }
    }

    private Dictionary<string, BeaconValue> StoreLocked(string scriptId)
    {
        if (!_cache.TryGetValue(scriptId, out Dictionary<string, BeaconValue>? table) || table is null)
        {
            table = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
            _cache[scriptId] = table;
        }

        return table;
    }

    private static void CheckQuotasLocked(string scriptId, Dictionary<string, BeaconValue> table)
    {
        if (table.Count > MaxKeys)
        {
            throw new BeaconQuotaExceededException(
                $"Beacon saved store for script '{scriptId}' refused to save: the script holds {table.Count} keys, "
                + $"over the {MaxKeys}-key limit.");
        }

        foreach ((string key, BeaconValue value) in table)
        {
            long size = BeaconValueTomlCodec.MeasureBytes(value);
            if (size > MaxValueBytes)
            {
                throw new BeaconQuotaExceededException(
                    $"Beacon saved store for script '{scriptId}' refused to save key '{key}': the value is "
                    + $"{size} bytes, over the {MaxValueBytes}-byte per-value limit.");
            }
        }
    }

    private void EnsureBeaconDir()
    {
        Directory.CreateDirectory(_beaconDir);
        if (new DirectoryInfo(_beaconDir).LinkTarget is not null)
        {
            throw new BeaconStateEscapeException(
                $"Refusing Beacon state access: the state folder '{_beaconDir}' is a symbolic link. "
                + "It must be a regular directory inside the configurations folder.",
                nameof(_beaconDir));
        }
    }

    private void SweepTmpFiles()
    {
        string[] leftovers;
        try
        {
            leftovers = Directory.GetFiles(_beaconDir, "*" + TmpMarker + "*");
        }
        catch
        {
            return;
        }

        foreach (string leftover in leftovers)
        {
            try
            {
                File.Delete(leftover);
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}

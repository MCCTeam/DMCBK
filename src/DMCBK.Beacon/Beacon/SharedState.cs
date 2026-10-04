namespace DMCBK.Core.Beacon;

/// <summary>Whether a share-report entry records a write or a read of a shared key.</summary>
public enum BeaconShareAccess
{
    /// <summary>A script published a shared key.</summary>
    Write,

    /// <summary>A script consumed another script's shared key.</summary>
    Read,
}

/// <summary>
/// One share-report row: which script touched which shared key, how, and when.
/// Queryable via <see cref="BeaconSharedState.GetShareReport()"/> for the linter ("who shares what"); never carries secret material, only key names and value kinds.
/// </summary>
public sealed record BeaconShareEntry(
    string ScriptId,
    string Key,
    BeaconShareAccess Access,
    BeaconValueKind ValueKind,
    DateTimeOffset SharedAtUtc);

/// <summary>
/// The RAM-only cross-script <c>shared</c> store: namespaced dotted keys (<c>shared["shop.price.bread"]</c>) visible to every script in the process, gone on restart.
/// </summary>
/// <remarks>
/// <para>Standalone by design.</para>
/// <para><see cref="BeaconEngine"/> owns one instance per engine and bridges <c>shared</c> reads plus writes into it on the load path.</para>
/// <para><c>lock shared</c> blocks run through <see cref="LockShared(Action)"/>, which takes the same gate as every data operation, so a read-modify-write between patrol and chat handlers cannot interleave (the warn-count race stays exact).</para>
/// <para>The gate is re-entrant, so nested <c>lock shared</c> blocks just work.</para>
/// <para>
/// Quotas (config-overridable later): at most <see cref="MaxKeys"/> distinct keys, at most <see cref="MaxValueBytes"/> bytes per value (UTF-8 bytes of its display form), at most <see cref="MaxKeyLength"/> characters per key.
/// Refusals throw <see cref="BeaconQuotaExceededException"/> naming the limit.
/// Values are deep-copied on the way in and out, so concurrent handlers can never alias each other's maps mid-race.
/// </para>
/// </remarks>
public sealed class BeaconSharedState
{
    /// <summary>Maximum distinct shared keys process-wide.</summary>
    public const int MaxKeys = 1024;

    /// <summary>Maximum bytes per shared value (UTF-8 bytes of its display form).</summary>
    public const long MaxValueBytes = 1024L * 1024L;

    /// <summary>Maximum characters per shared key.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>Maximum retained share-report rows; oldest drop first (the store itself is unaffected).</summary>
    public const int MaxShareReportEntries = 8192;

    private readonly object _gate = new();
    private readonly Dictionary<string, BeaconValue> _values = new(StringComparer.Ordinal);
    private readonly List<BeaconShareEntry> _report = [];

    /// <summary>Distinct shared keys currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _values.Count;
        }
    }

    /// <summary>True when <paramref name="key"/> is currently shared (check before reading).</summary>
    public bool IsSet(string key)
    {
        ValidateKey(key);
        lock (_gate)
            return _values.ContainsKey(key);
    }

    /// <summary>Reads a shared key; false when absent. The returned value is a copy.</summary>
    public bool TryGet(string key, out BeaconValue? value)
    {
        ValidateKey(key);
        lock (_gate)
        {
            if (_values.TryGetValue(key, out BeaconValue? found) && found is not null)
            {
                value = BeaconValueTomlCodec.Clone(found);
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// Reads a shared key as <paramref name="readerScriptId"/>, recording a read row in the share-report on a hit so the linter sees who consumes what others publish.
    /// </summary>
    public bool TryGet(string readerScriptId, string key, out BeaconValue? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerScriptId);
        ValidateKey(key);
        lock (_gate)
        {
            if (_values.TryGetValue(key, out BeaconValue? found) && found is not null)
            {
                value = BeaconValueTomlCodec.Clone(found);
                NoteLocked(readerScriptId, key, BeaconShareAccess.Read, found.Kind);
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>Reads a shared key, or <see cref="BeaconValue.None"/> when absent.</summary>
    public BeaconValue GetOrNone(string key)
        => TryGet(key, out BeaconValue? value) && value is not null ? value : BeaconValue.None;

    /// <summary>
    /// Publishes a shared key as <paramref name="scriptId"/> (quota-checked, copy stored) and records a write row in the share-report.
    /// </summary>
    public void Set(string scriptId, string key, BeaconValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        long size = BeaconValueTomlCodec.MeasureBytes(value);
        if (size > MaxValueBytes)
        {
            throw new BeaconQuotaExceededException(
                $"Beacon shared store refused key '{key}' from script '{scriptId}': the value is {size} bytes, "
                + $"over the {MaxValueBytes}-byte per-value limit.");
        }

        lock (_gate)
        {
            if (!_values.ContainsKey(key) && _values.Count >= MaxKeys)
            {
                throw new BeaconQuotaExceededException(
                    $"Beacon shared store refused key '{key}' from script '{scriptId}': the store already holds "
                    + $"the {MaxKeys}-key limit.");
            }

            _values[key] = BeaconValueTomlCodec.Clone(value);
            NoteLocked(scriptId, key, BeaconShareAccess.Write, value.Kind);
        }
    }

    /// <summary>Withdraws a shared key; returns false when absent. History rows are kept.</summary>
    public bool Remove(string key)
    {
        ValidateKey(key);
        lock (_gate)
            return _values.Remove(key);
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the shared gate, serializing read-modify-write sequences (the <c>lock shared</c> block).
    /// Re-entrant: bodies may read, write, or nest further blocks.
    /// </summary>
    public void LockShared(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_gate)
            body();
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the shared gate and returns its result (the value-returning <c>lock shared</c> form, for tests and engine callers).
    /// </summary>
    public T LockShared<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_gate)
            return body();
    }

    /// <summary>All share-report rows, oldest first: who shared (or read) what.</summary>
    public IReadOnlyList<BeaconShareEntry> GetShareReport()
    {
        lock (_gate)
            return _report.ToList();
    }

    /// <summary>Share-report rows for one script, oldest first.</summary>
    public IReadOnlyList<BeaconShareEntry> GetShareReport(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
            return _report.Where(e => string.Equals(e.ScriptId, scriptId, StringComparison.Ordinal)).ToList();
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Beacon shared key must not be empty.", nameof(key));

        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"Beacon shared key is {key.Length} characters; the limit is {MaxKeyLength}.", nameof(key));
        }

        if (key.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Beacon shared key '{key}' must not contain whitespace; use dots, as in 'shop.price.bread'.",
                nameof(key));
        }

        foreach (string segment in key.Split('.'))
        {
            if (segment.Length == 0)
            {
                throw new ArgumentException(
                    $"Beacon shared key '{key}' has an empty dotted segment; use 'shop.price.bread' shape.",
                    nameof(key));
            }
        }
    }

    private void NoteLocked(string scriptId, string key, BeaconShareAccess access, BeaconValueKind kind)
    {
        if (_report.Count >= MaxShareReportEntries)
            _report.RemoveAt(0);

        _report.Add(new BeaconShareEntry(scriptId, key, access, kind, DateTimeOffset.UtcNow));
    }
}

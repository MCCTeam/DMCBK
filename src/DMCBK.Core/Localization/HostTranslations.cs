using System.Diagnostics.CodeAnalysis;
using Umpk.Data.Lang;
using Umpk.Text;

namespace DMCBK.Core.Localization;

/// <summary>
/// The host's translation chain: operator overrides win, then server resource-pack entries, then the negotiated protocol's own vanilla table, then the newest vanilla table as a last resort so a key only a newer era knows still resolves rather than printing raw.
/// </summary>
/// <remarks>
/// <para>
/// UMPK ships the component flattening logic and, in <see cref="VanillaTranslations"/>, one complete vanilla <c>en_us</c> table per protocol straight from the dataset; it ships no aggregation policy across protocols, because that policy is a host concern.
/// This type is that policy for MCC: four fixed layers over a <see cref="LayeredTranslationSource"/>, with the pack layer fed by <see cref="ResourcePackTranslationLoader"/> and the protocol layer swapped as the session negotiates (or reconnects to) a version.
/// </para>
/// <para>
/// Before <see cref="UseProtocol"/> is ever called, the protocol layer already equals the last layer (both <see cref="VanillaTranslations.Latest"/>), so a client built but not yet connected to a specific protocol resolves exactly like a modern one; nothing needs a null-protocol special case.
/// </para>
/// </remarks>
public sealed class HostTranslations : ITranslationSource
{
    // Layer order: 0 operator overrides, 1 merged resource-pack entries, 2 negotiated protocol vanilla table, 3 newest vanilla table fallback.
    private const int PackLayerIndex = 1;
    private const int ProtocolLayerIndex = 2;

    private readonly LayeredTranslationSource _layers;
    private readonly Lock _packsGate = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _packs = new(StringComparer.Ordinal);
    private volatile int _protocolVersion = -1;

    /// <summary>Builds the chain with optional host locale overrides layered on top of the vanilla tables.</summary>
    /// <param name="overrides">Optional per-key template overrides (a host locale layer); may be null or empty.</param>
    public HostTranslations(IReadOnlyDictionary<string, string>? overrides = null)
    {
        ITranslationSource overrideLayer = overrides is null || overrides.Count == 0
            ? TranslationTable.Empty
            : TranslationTable.FromEntries(overrides);

        _layers = new LayeredTranslationSource(
            overrideLayer, TranslationTable.Empty, VanillaTranslations.Latest, VanillaTranslations.Latest);
    }

    /// <summary>Builds the chain with no host overrides.</summary>
    public static HostTranslations CreateDefault() => new();

    /// <summary>
    /// The protocol whose vanilla table is currently layered in the protocol slot, or null before <see cref="UseProtocol"/> has ever been called.
    /// </summary>
    public int? ProtocolVersion => _protocolVersion < 0 ? null : _protocolVersion;

    /// <summary>
    /// Points the protocol layer at <paramref name="protocolVersion"/>'s own vanilla table.
    /// The client calls this once per session as soon as the version is negotiated and before any chat is rendered; the last layer stays <see cref="VanillaTranslations.Latest"/> regardless, so a key the negotiated protocol's own table does not carry still resolves against the newest table instead of printing the raw key.
    /// </summary>
    public void UseProtocol(int protocolVersion)
    {
        _layers.SetLayer(ProtocolLayerIndex, VanillaTranslations.ForProtocol(protocolVersion));
        _protocolVersion = protocolVersion;
    }

    /// <summary>
    /// Adds or replaces one pack's entries in the pack layer.
    /// Later packs win over earlier ones on clashing keys, matching the retired 1.x stacking where the newest layer was checked first.
    /// Empty entries remove the pack instead of layering nothing.
    /// </summary>
    public void SetPackTranslations(string packId, IReadOnlyDictionary<string, string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(entries);
        lock (_packsGate)
        {
            if (entries.Count == 0)
                _packs.Remove(packId);
            else
                _packs[packId] = entries;

            _layers.SetLayer(PackLayerIndex, MergePacksLocked());
        }
    }

    /// <summary>Removes one pack's entries from the pack layer. Unknown ids are ignored.</summary>
    public void RemovePackTranslations(string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        lock (_packsGate)
        {
            if (_packs.Remove(packId))
                _layers.SetLayer(PackLayerIndex, MergePacksLocked());
        }
    }

    /// <summary>Clears every pack's entries, e.g. on session start or when the server pops all packs.</summary>
    public void ClearPackTranslations()
    {
        lock (_packsGate)
        {
            if (_packs.Count == 0)
                return;

            _packs.Clear();
            _layers.SetLayer(PackLayerIndex, TranslationTable.Empty);
        }
    }

    /// <inheritdoc/>
    public bool TryResolve(string key, [NotNullWhen(true)] out string? template)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _layers.TryResolve(key, out template);
    }

    private TranslationTable MergePacksLocked()
    {
        if (_packs.Count == 0)
            return TranslationTable.Empty;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, string> entries in _packs.Values)
        {
            foreach ((string key, string value) in entries)
                merged[key] = value;
        }

        return TranslationTable.FromEntries(merged);
    }
}

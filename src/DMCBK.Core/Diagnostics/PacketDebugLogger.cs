using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Umpk.Client;
using Umpk.Client.Events;

namespace DMCBK.Core.Diagnostics;

/// <summary>
/// The consumer for <c>logging.packetdebugmessages</c> and <c>logging.packetdebugexclusions</c>.
/// Until this existed, both keys were bound in five places and read by nobody, so turning the setting on did nothing at all: exactly the declared-but-never-implemented shape this codebase keeps finding.
/// <para>
/// It subscribes to UMPK's <see cref="PacketReceived"/> and writes one debug line per clientbound packet carrying the protocol phase, the packet name, the wire id and the payload length.
/// The event once published the decoded packet with <c>WireId</c> hardcoded to <c>-1</c> and no byte count at all, so the line read <c>id=n/a</c> with no size; it carries both for real now and the line does too.
/// The negative-value fallbacks in <see cref="Format"/> are kept rather than deleted: they are the honesty guard that prints <c>id=n/a</c> instead of inventing a wire id, not a workaround for the old gap.
/// </para>
/// </summary>
internal sealed class PacketDebugLogger : IDisposable
{
    private readonly ILogger _logger;
    private readonly string[] _exclusions;
    private IDisposable? _subscription;

    private PacketDebugLogger(ILogger logger, IReadOnlyList<string> exclusions)
    {
        _logger = logger;
        _exclusions = [.. exclusions.Where(static e => !string.IsNullOrWhiteSpace(e))];
    }

    /// <summary>
    /// Attaches a packet-debug logger to a session when the setting is on, else returns null.
    /// Returns null too when the logger would discard every line anyway, so a disabled debug level costs nothing per packet rather than a formatted string per packet.
    /// </summary>
    internal static PacketDebugLogger? TryAttach(
        UmpkClient client, bool enabled, IReadOnlyList<string> exclusions, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(logger);

        if (!enabled || !logger.IsEnabled(LogLevel.Debug))
            return null;

        var instance = new PacketDebugLogger(logger, exclusions);
        instance._subscription = client.Events.Subscribe<PacketReceived>(instance.OnPacket);
        return instance;
    }

    /// <summary>
    /// Renders one packet-debug line.
    /// Separated from the subscription so it is directly testable.
    /// </summary>
    /// <param name="phase">The protocol phase the packet arrived in.</param>
    /// <param name="wireId">The numeric wire id, or a negative value when the feed does not carry one.</param>
    /// <param name="name">The packet name.</param>
    /// <param name="payloadLength">The raw payload length, or a negative value when the feed carries no bytes.</param>
    internal static string Format(string phase, int wireId, string name, int payloadLength)
    {
        var builder = new StringBuilder("packet ");
        builder.Append(phase).Append(' ').Append(name);
        builder.Append(wireId >= 0
            ? string.Format(CultureInfo.InvariantCulture, " id=0x{0:x2}", wireId)
            : " id=n/a");

        if (payloadLength >= 0)
            builder.Append(string.Format(CultureInfo.InvariantCulture, " bytes={0}", payloadLength));

        return builder.ToString();
    }

    /// <summary>
    /// Projects one <see cref="PacketReceived"/> onto its debug line.
    /// Split out from the subscription so the projection itself is testable: it is the piece that decides which fields of the event reach the user, and it read a hardcoded "no payload" before the event carried one.
    /// </summary>
    internal static string FormatReceived(PacketReceived received)
    {
        ArgumentNullException.ThrowIfNull(received);
        return Format(
            received.Phase.ToString(),
            received.WireId,
            received.Packet?.GetType().Name ?? "unknown",
            received.PayloadLength);
    }

    /// <summary>
    /// True when a packet name is excluded.
    /// Matched as a case-insensitive substring so a configured <c>"KeepAlive"</c> covers whatever the era's concrete packet class happens to be called.
    /// </summary>
    internal static bool IsExcluded(string name, IReadOnlyList<string> exclusions)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(exclusions);
        foreach (string exclusion in exclusions)
        {
            if (!string.IsNullOrWhiteSpace(exclusion)
                && name.Contains(exclusion.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(ref _subscription, null)?.Dispose();

    private void OnPacket(PacketReceived received)
    {
        string name = received.Packet?.GetType().Name ?? "unknown";
        if (IsExcluded(name, _exclusions))
            return;

        _logger.LogDebug("{Line}", FormatReceived(received));
    }
}

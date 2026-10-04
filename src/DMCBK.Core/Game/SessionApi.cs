using Umpk.Client.Snapshots;

namespace DMCBK.Core;

/// <summary>
/// The session surface: the negotiated server endpoint and version, the server's own facts (brand and measured tick rate), the latency figures, and the client-settings send.
/// Everything past the endpoint comes from UMPK's session read-model, which tracks the <c>minecraft:brand</c> plugin channel, the game-time broadcast the tick rate is derived from, and the keep-alive exchange.
/// </summary>
public sealed class SessionApi
{
    private readonly GameSession _session;

    internal SessionApi(GameSession session) => _session = session;

    /// <summary>
    /// Reads the session info: the connected endpoint, the negotiated version name and protocol, the server brand, the measured tick rate, and the two latency figures.
    /// Every nullable member means "not known yet" rather than a zero.
    /// </summary>
    public Task<SessionInfoSnapshot> GetInfoAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            SessionSnapshot info = await client.Snapshots.SessionAsync(ct).ConfigureAwait(false);
            return new SessionInfoSnapshot(
                info.Host, info.Port, info.VersionName, info.Protocol, info.ObservedLatencyMs,
                info.Brand, info.TpsEstimate, info.KeepAliveTurnaround, info.KeepAliveInterval);
        });

    /// <summary>
    /// Sends the client-settings (client-information) packet.
    /// The client applies the whole configured client-settings block to UMPK options at build time and sends this once on join; hosts call this again after a settings change.
    /// Passing no options re-sends the options the client was built with.
    /// </summary>
    public Task SendClientSettingsAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Session.SetClientSettingsAsync(null, ct));
}

/// <summary>Session-level facts.</summary>
/// <param name="Host">The connected server host.</param>
/// <param name="Port">The connected server port.</param>
/// <param name="VersionName">The negotiated Minecraft version name.</param>
/// <param name="Protocol">The negotiated protocol number.</param>
/// <param name="ObservedLatencyMs">
/// The SERVER-MEASURED round trip for this client in milliseconds, as published in the tab list, or null before the server reports one.
/// This is the only real latency figure a Java client has: the server times its own keep-alive round trip and sends the smoothed result down.
/// Do not confuse it with <paramref name="KeepAliveTurnaround"/>.
/// </param>
/// <param name="Brand">
/// The server brand (<c>vanilla</c>, <c>Paper</c>, ...) from the <c>minecraft:brand</c> plugin channel (<c>MC|Brand</c> before 1.13), or null until the server announces it.
/// </param>
/// <param name="TpsEstimate">
/// The measured server ticks-per-second, or null when it is NOT KNOWN.
/// Null is never to be shown as zero: a server paused by 1.21.2+ pause-when-empty or held by <c>/tick freeze</c> stops broadcasting game time, so the estimate expires back to null rather than decaying toward zero.
/// The client genuinely cannot tell a paused server from an unreachable one.
/// </param>
/// <param name="KeepAliveTurnaround">
/// How long this client took to answer the last keep-alive, or null before the first one.
/// This is OUR OWN responder-side turnaround, NOT a round trip: a Java client is only ever the responder on the keep-alive exchange and the id is the server's own clock reading, so it cannot measure RTT.
/// Useful for spotting a stalled session loop (the cause of keep-alive timeout disconnects), not for reporting ping.
/// </param>
/// <param name="KeepAliveInterval">
/// The gap between the last two clientbound keep-alives, or null before the second one.
/// A vanilla server aims for 15 seconds, so a materially longer gap means the server is behind on its own network tick.
/// </param>
public sealed record SessionInfoSnapshot(
    string Host,
    int Port,
    string VersionName,
    int Protocol,
    int? ObservedLatencyMs,
    string? Brand,
    double? TpsEstimate,
    TimeSpan? KeepAliveTurnaround,
    TimeSpan? KeepAliveInterval);

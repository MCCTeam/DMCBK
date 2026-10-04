namespace DMCBK.Core;

/// <summary>
/// Raised by <see cref="Client.ServerStatusReceived"/> whenever a server-status ping completes with a usable result: the normal auto-detect ping every connect makes when no version is pinned, and (best-effort) an additional one-shot ping fired purely for the display when the version WAS pinned, so a host's connect-time status panel has something to show either way, matching legacy pinging on every connect for the display.
/// Carries the raw ping result plus the endpoint dialed and the protocol this client will actually speak.
/// </summary>
public sealed class ServerStatusReceivedEventArgs(
    Umpk.Protocol.Java.ServerStatus status, string host, int port, Umpk.Protocol.Java.JavaVersion? resolvedVersion) : EventArgs
{
    /// <summary>The decoded status-ping result (MOTD, reported version, players, favicon, latency).</summary>
    public Umpk.Protocol.Java.ServerStatus Status { get; } = status;

    /// <summary>The host dialed.</summary>
    public string Host { get; } = host;

    /// <summary>The port dialed.</summary>
    public int Port { get; } = port;

    /// <summary>
    /// The version this client will actually speak: the negotiated version on the auto-detect path, or the pinned version on the pinned (display-only ping) path.
    /// Null only when neither was resolvable (a negotiation failure that still returned a status, e.g. an unsupported protocol).
    /// </summary>
    public Umpk.Protocol.Java.JavaVersion? ResolvedVersion { get; } = resolvedVersion;
}

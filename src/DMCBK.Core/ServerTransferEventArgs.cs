namespace DMCBK.Core;

/// <summary>
/// The server asked the client to move to another address (<c>minecraft:transfer</c>, 1.20.5+).
/// <para>
/// This is how a hub sends you to a game server when you click an item in its menu, so it is an instruction to follow rather than a disconnect to report.
/// <see cref="Client"/> follows it by reconnecting; the event is public so a host can say where it is going, as the legacy client did ("Initiating a transfer to: host:port", McClient.cs:426).
/// </para>
/// </summary>
/// <param name="host">The host to move to.</param>
/// <param name="port">The port to move to.</param>
public sealed class ServerTransferEventArgs(string host, int port) : EventArgs
{
    /// <summary>The host the server asked the client to move to.</summary>
    public string Host { get; } = host;

    /// <summary>The port the server asked the client to move to.</summary>
    public int Port { get; } = port;
}

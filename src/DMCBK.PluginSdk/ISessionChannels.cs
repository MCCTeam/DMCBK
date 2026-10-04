using Umpk;
using Umpk.Client.Plugins;

namespace DMCBK.PluginSdk;

/// <summary>
/// The plugin-channel surface of a live session, reached through <see cref="ISessionScope.Channels"/>: register for inbound custom payloads, send outbound ones, read what the server announced, and encode or decode a payload without hand-rolling Minecraft's own primitives.
/// <para>
/// To be registered before the first inbound message of a session arrives, register from <see cref="PluginContext.SessionCreated"/> on <see cref="IPreSessionScope"/> instead.
/// Registrations and subscriptions made here are released at session end and on unload.
/// </para>
/// </summary>
public interface ISessionChannels
{
    /// <summary>
    /// Registers a handler for a play-phase channel.
    /// The first registration for a channel announces it to the server with <c>minecraft:register</c>, the last disposal announces <c>minecraft:unregister</c>.
    /// </summary>
    IDisposable Register(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage);

    /// <summary>
    /// Sends a play-phase custom payload.
    /// There is no "must be registered first" rule: vanilla routes a plugin message whether or not the channel was announced.
    /// </summary>
    /// <exception cref="Umpk.Client.ActionNotSupportedException">This version has no serverbound play custom payload.</exception>
    ValueTask SendAsync(Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// The channels the server announced with <c>minecraft:register</c>, as a snapshot.
    /// Read it to find out what a server speaks before speaking to it.
    /// A pre-1.13 server announcing a legacy, non-namespaced name contributes nothing here, because such a name is not an <see cref="Identifier"/>.
    /// </summary>
    IReadOnlySet<Identifier> ServerAnnounced { get; }

    /// <summary>
    /// Raised when <see cref="ServerAnnounced"/> changes.
    /// Runs on the read loop, so the handler must be cheap; hand real work to <see cref="ISessionScope.Scheduler"/>.
    /// </summary>
    event EventHandler<ServerAnnouncedChannelsChangedEventArgs>? ServerAnnouncedChanged;

    /// <summary>A reader over a payload that already knows this session's block-position layout.</summary>
    PluginPayloadReader Read(ReadOnlyMemory<byte> payload);

    /// <summary>An empty payload writer that already knows this session's block-position layout.</summary>
    PluginPayloadWriter Write();
}

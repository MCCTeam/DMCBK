using Umpk;
using Umpk.Client.Plugins;

namespace DMCBK.PluginSdk;

/// <summary>
/// The default <see cref="ISessionChannels"/>, over one session's <see cref="ClientPluginContext"/> and the client's own <see cref="ClientChannels"/>.
/// <para>
/// <see cref="Register"/> goes through the plugin context, which is what makes a registration end with this plugin's session rather than with the client.
/// The announced-channel event is unsubscribed on the same signal, so a plugin unloaded mid-session stops being called.
/// </para>
/// </summary>
internal sealed class SessionChannels : ISessionChannels
{
    private readonly ClientPluginContext _context;
    private readonly ClientChannels _channels;

    internal SessionChannels(ClientPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _channels = context.Channels;
    }

    /// <inheritdoc/>
    public event EventHandler<ServerAnnouncedChannelsChangedEventArgs>? ServerAnnouncedChanged
    {
        add
        {
            if (value is null)
                return;

            _channels.ServerAnnouncedChanged += value;
            _context.Detached.Register(() => _channels.ServerAnnouncedChanged -= value);
        }

        remove => _channels.ServerAnnouncedChanged -= value;
    }

    /// <inheritdoc/>
    public IReadOnlySet<Identifier> ServerAnnounced => _channels.ServerAnnounced;

    /// <inheritdoc/>
    public IDisposable Register(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
        => _context.RegisterPluginChannel(channel, onMessage);

    /// <inheritdoc/>
    public ValueTask SendAsync(Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => _context.SendPluginMessageAsync(channel, data, ct);

    /// <inheritdoc/>
    public PluginPayloadReader Read(ReadOnlyMemory<byte> payload) => _channels.Read(payload);

    /// <inheritdoc/>
    public PluginPayloadWriter Write() => _channels.Write();
}

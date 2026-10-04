using System.Buffers;
using System.Text;
using Umpk;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using DMCBK.Testing.Server;

namespace DMCBK.Testing;

/// <summary>
/// The live session inside <see cref="PluginTestHost.RunSessionAsync"/>: what the fake server can say to the plugin under test.
/// The client is in play, so anything sent here arrives the way a real server's message would.
/// </summary>
public sealed class PluginTestSession
{
    private readonly PluginTestHost _host;
    private readonly FakeJavaServer _server;

    internal PluginTestSession(PluginTestHost host, FakeJavaServer server)
    {
        _host = host;
        _server = server;
    }

    /// <summary>Announces channels as <c>minecraft:register</c> does, which is how a server offers one.</summary>
    public Task AnnounceChannelsAsync(params Identifier[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return SendPluginMessageAsync(
            Identifier.Minecraft("register"),
            Encoding.UTF8.GetBytes(string.Join('\0', channels.Select(c => c.ToString()))));
    }

    /// <summary>Sends a clientbound play-phase plugin message on one channel.</summary>
    public async Task SendPluginMessageAsync(Identifier channel, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        CancellationToken token = _host.Link(ct);
        int wireId = ClientboundWireId(Identifier.Minecraft("custom_payload"), ProtocolPhase.Play);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteString(channel.ToString());
        writer.WriteBytes(data);
        await _server.SendFrameAsync(wireId, buffer.WrittenSpan.ToArray(), token).ConfigureAwait(false);
    }

    /// <summary>Encodes one clientbound packet for the negotiated version and sends it.</summary>
    public Task SendAsync<TPacket>(TPacket packet, ProtocolPhase phase = ProtocolPhase.Play, CancellationToken ct = default)
        where TPacket : class, IPacket
        => SendAsync(_host, _server, phase, packet, _host.Link(ct));

    /// <summary>
    /// Waits for the next serverbound frame and returns its wire id and a copy of its payload, so a test can assert what the plugin sent rather than only that it did not throw.
    /// </summary>
    public async Task<(int WireId, byte[] Payload)> NextFrameAsync(CancellationToken ct = default)
    {
        InboundFrame frame = await _server.NextFrameAsync(_host.Link(ct)).ConfigureAwait(false);
        return (frame.WireId, frame.CopyPayload());
    }

    internal static async Task SendAsync<TPacket>(
        PluginTestHost host, FakeJavaServer server, ProtocolPhase phase, TPacket packet, CancellationToken ct)
        where TPacket : class, IPacket
    {
        ProtocolDescriptor descriptor = host.Version.Protocol;
        if (!descriptor.TryGetRegistry(phase, PacketFlow.Clientbound, out PhaseRegistry registry))
            throw new InvalidOperationException($"{host.Version.Version.Name} has no clientbound registry for {phase}.");

        foreach ((int wireId, PacketType type) in registry.Packets)
        {
            if (type.Id != packet.Type.Id)
                continue;

            if (!registry.TryGetInbound(wireId, out BoundPacketCodec codec))
                break;

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new PacketWriter(buffer);
            codec.Encode(ref writer, packet, PacketCodecContext.Registryless);
            await server.SendFrameAsync(wireId, buffer.WrittenSpan.ToArray(), ct).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException($"{packet.Type.Id} is not registered clientbound in {phase}.");
    }

    private int ClientboundWireId(Identifier packet, ProtocolPhase phase)
    {
        ProtocolDescriptor descriptor = _host.Version.Protocol;
        if (descriptor.TryGetRegistry(phase, PacketFlow.Clientbound, out PhaseRegistry registry))
        {
            foreach ((int wireId, PacketType type) in registry.Packets)
            {
                if (type.Id == packet)
                    return wireId;
            }
        }

        throw new InvalidOperationException(
            $"{_host.Version.Version.Name} registers no clientbound {packet} in {phase}.");
    }
}

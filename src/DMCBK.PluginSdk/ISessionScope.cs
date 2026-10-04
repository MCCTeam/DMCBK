using DMCBK.Core.Commands;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Client.Plugins;

namespace DMCBK.PluginSdk;

/// <summary>
/// The per-live-session surface, a thin adapter over UMPK's <see cref="ClientPluginContext"/>.
/// A fresh scope is created for every session (initial connect AND each reconnect) for THIS plugin specifically: since <c>PluginHost</c> registers one UMPK extension per MCC plugin (<c>McPluginExtension</c>), UMPK already bridges each plugin into its own, independent <see cref="ClientPluginContext"/> per session, so <see cref="Events"/>, <see cref="Scheduler"/> and <see cref="ObservePackets"/> subscriptions are already billed to, and released with, exactly this plugin, so no extra bookkeeping is needed here.
/// <para>
/// <see cref="Commands"/> is the one member that stays MCC-owned: UMPK's own command scope speaks a different command system (<c>Umpk.Commands</c>), so a plugin's session-scoped internal commands still go through a private <see cref="PluginCommandScope"/> here, released when <see cref="Detached"/> fires.
/// </para>
/// Obtain the live scope from <see cref="PluginContext.SessionStarted"/> or <see cref="PluginContext.Session"/>; never cache a scope across sessions.
/// </summary>
public interface ISessionScope
{
    /// <summary>The live UMPK client for this session (advanced use; prefer <see cref="PluginContext.Game"/>).</summary>
    UmpkClient Client { get; }

    /// <summary>The tracked session state (self, world, entities, inventory, roster, ...).</summary>
    ClientState State { get; }

    /// <summary>
    /// UMPK's own session event bus.
    /// Subscriptions made through it are released automatically when this plugin's session ends, whether that is a real disconnect/reconnect or this plugin being unloaded while the session stays live for everyone else.
    /// </summary>
    ClientEvents Events { get; }

    /// <summary>The thread-safe session action surface (chat, movement, interaction, inventory, session).</summary>
    ClientActions Actions { get; }

    /// <summary>
    /// UMPK's own per-plugin session-loop scheduler: <c>OnTick</c>, <c>Delay</c>, <c>Post</c>, <c>RunOffLoop</c>.
    /// Registrations are released the same way <see cref="Events"/> subscriptions are.
    /// </summary>
    PluginScheduler Scheduler { get; }

    /// <summary>
    /// The plugin's internal-command registration scope for this session: registered commands dispatch real console input and are unregistered at session end (or on plugin unload, whichever comes first).
    /// For a command that should survive reconnects, use <see cref="PluginContext.Commands"/> instead.
    /// </summary>
    IPluginCommandScope Commands { get; }

    /// <summary>
    /// A token that fires when this scope stops being live: the session ended (reconnect, clean disconnect) or THIS plugin was unloaded or reloaded while the session kept running.
    /// Pass it to any loop or await a plugin starts so an unload actually stops the work.
    /// </summary>
    CancellationToken Detached { get; }

    /// <summary>
    /// Tries to acquire the exclusive movement lease for this session.
    /// Returns null when another owner holds it.
    /// Dispose the lease to release; it is also released automatically at session end.
    /// </summary>
    IMovementLease? TryAcquireMovement(string reason);

    /// <summary>
    /// The tag of whoever holds the movement lease right now, or null when it is free.
    /// Read it instead of probing with <see cref="TryAcquireMovement"/> when the question is "is something else steering?": probing takes the lease for the length of the probe, which is enough to make a navigation starting on the same tick fail its own acquire.
    /// </summary>
    string? MovementOwner { get; }

    /// <summary>
    /// This session's plugin channels: register, send, read what the server announced, and the payload reader and writer.
    /// <see cref="RegisterPluginChannel"/> and <see cref="SendPluginMessageAsync"/> are the same two calls reached directly.
    /// </summary>
    ISessionChannels Channels { get; }

    /// <summary>
    /// What the negotiated version lets this session do, packet by packet.
    /// Ask it before an action a version may not carry, instead of catching <see cref="Umpk.Client.ActionNotSupportedException"/>.
    /// </summary>
    ClientActionCapabilities Capabilities { get; }

    /// <summary>Registers a custom plugin-channel handler for this session.</summary>
    PluginChannelRegistration RegisterPluginChannel(Umpk.Identifier channel, Action<ReadOnlyMemory<byte>> onMessage);

    /// <summary>Sends a serverbound custom-payload message on a plugin channel.</summary>
    ValueTask SendPluginMessageAsync(Umpk.Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to the RAW frame feed: every frame this session sends or receives, in frame order, with its real wire id, its phase, its direction and the decrypted, decompressed body bytes.
    /// This is the pre-decode ground truth that the decoded <see cref="Events"/> stream cannot give, and it is what a recorder or a protocol inspector needs.
    /// Forwards to <see cref="ClientPluginContext.ObservePackets"/>, so the frame type is UMPK's own <see cref="Umpk.Protocol.Java.PacketFrame"/>.
    /// Dispose the returned handle to stop; it is also released at session end.
    /// <para>
    /// THREADING.
    /// The handler runs on the connection's read loop (inbound) or inside the caller's send (outbound), NOT on the session loop.
    /// It must be cheap and must not block; hand work to <see cref="Scheduler"/> if it is not trivial.
    /// A throwing handler is logged and swallowed by the client rather than tearing the session down.
    /// </para>
    /// <para>
    /// LIFETIME.
    /// <see cref="Umpk.Protocol.Java.PacketFrame.Payload"/> is only valid inside the call.
    /// Use <see cref="Umpk.Protocol.Java.PacketFrame.CopyPayload"/> to keep the bytes.
    /// The ref-struct frame makes accidentally keeping it a compile error rather than a recycled-buffer read.
    /// </para>
    /// </summary>
    /// <param name="handler">Called once per frame, both directions.</param>
    /// <returns>A handle that unsubscribes on dispose.</returns>
    IDisposable ObservePackets(Umpk.Protocol.Java.PacketFrameHandler handler);
}

/// <summary>
/// The default <see cref="ISessionScope"/>: adapts one UMPK <see cref="ClientPluginContext"/> (already scoped to exactly this plugin's session) and owns the one thing UMPK cannot provide, a real, MCC-command-dispatching <see cref="Commands"/> scope, self-closing when <see cref="Detached"/> fires.
/// </summary>
internal sealed class SessionScope : ISessionScope
{
    private readonly ClientPluginContext _context;
    private readonly PluginCommandScope _commands;
    private readonly SessionChannels _channels;

    internal SessionScope(ClientPluginContext context, ICommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commands);
        _context = context;
        _commands = new PluginCommandScope(commands);
        _channels = new SessionChannels(context);

        // Self-closing: this scope's own Detached token already fires at session end AND when this plugin is unloaded/reloaded mid-session (it is ClientPluginContext.Detached), so registering directly against it is what makes the session-scoped command registrations go away on either path with no help needed from PluginContext.
        _context.Detached.Register(_commands.DisposeAll);
    }

    /// <inheritdoc/>
    public UmpkClient Client => _context.Client;

    /// <inheritdoc/>
    public ClientState State => _context.Client.State;

    /// <inheritdoc/>
    public ClientEvents Events => _context.Events;

    /// <inheritdoc/>
    public ClientActions Actions => _context.Actions;

    /// <inheritdoc/>
    public PluginScheduler Scheduler => _context.Scheduler;

    /// <inheritdoc/>
    public IPluginCommandScope Commands => _commands;

    /// <summary>The same scope, typed, so the host can ask which plugin owns a command that threw.</summary>
    internal PluginCommandScope CommandScope => _commands;

    /// <inheritdoc/>
    public CancellationToken Detached => _context.Detached;

    /// <inheritdoc/>
    public IMovementLease? TryAcquireMovement(string reason) => _context.TryAcquireMovement(reason);

    /// <inheritdoc/>
    public string? MovementOwner => _context.MovementOwner;

    /// <inheritdoc/>
    public ISessionChannels Channels => _channels;

    /// <inheritdoc/>
    public ClientActionCapabilities Capabilities => _context.Capabilities;

    /// <inheritdoc/>
    public PluginChannelRegistration RegisterPluginChannel(Umpk.Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
        => _context.RegisterPluginChannel(channel, onMessage);

    /// <inheritdoc/>
    public ValueTask SendPluginMessageAsync(Umpk.Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => _context.SendPluginMessageAsync(channel, data, ct);

    /// <inheritdoc/>
    public IDisposable ObservePackets(Umpk.Protocol.Java.PacketFrameHandler handler) => _context.ObservePackets(handler);
}

using Umpk;
using Umpk.Client.Plugins;
using Umpk.Protocol.Java;

namespace DMCBK.PluginSdk;

/// <summary>
/// What a plugin may set up on a session that has been built but has not dialled yet, handed to it by <see cref="PluginContext.SessionCreated"/>.
/// This is the only moment early enough to claim a login-phase channel or to be sure of catching the first message on a play channel: login is over before <see cref="PluginContext.SessionStarted"/> fires, and servers announce their channels and start their mod handshakes immediately.
/// <para>
/// Everything registered here belongs to THAT session and is released when it ends, or when the plugin is unloaded, whichever comes first.
/// A fresh scope arrives for every reconnect, so registrations are made again each time rather than carried over.
/// </para>
/// </summary>
public interface IPreSessionScope
{
    /// <summary>The unconnected client this session is about to dial. Subscribe to it, do not drive it.</summary>
    Umpk.Client.UmpkClient Client { get; }

    /// <summary>
    /// Registers a play-phase channel handler before the dial, so the first message the server sends on that channel is delivered.
    /// The first registration for a channel announces it with <c>minecraft:register</c> once play begins.
    /// </summary>
    IDisposable RegisterPlay(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage);

    /// <summary>
    /// Registers a configuration-phase channel handler (1.20.2 and later).
    /// Nothing is announced: vanilla announces nothing during configuration either.
    /// Handlers run inline with the configuration driver, which is what makes it safe to answer from inside one with <see cref="SendConfigurationAsync"/>.
    /// </summary>
    IDisposable RegisterConfiguration(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage);

    /// <summary>
    /// Sends a configuration-phase custom payload.
    /// Meaningful only while the connection is actually in that phase, so the natural caller is a <see cref="RegisterConfiguration"/> handler answering what it was just handed.
    /// </summary>
    /// <exception cref="Umpk.Client.ActionNotSupportedException">This version has no configuration phase.</exception>
    ValueTask SendConfigurationAsync(Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Claims a login-phase <c>custom_query</c> channel, the one a proxy uses for modern forwarding (<c>velocity:player_info</c>) and a mod loader for its handshake (<c>fml:loginwrapper</c>).
    /// The responder answers with the bytes to send, or null for "not understood", which is what every channel nobody claims still gets.
    /// An empty but non-null answer is a real answer with an empty body.
    /// <para>
    /// The responder runs on the login read loop with the server waiting on it, so it must be prompt.
    /// One responder per channel: a second claim on the same channel throws, because a query carries a single transaction id and two answers cannot both be it.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Another responder already claimed the channel.</exception>
    IDisposable RegisterLoginQuery(
        Identifier channel, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> responder);

    /// <summary>
    /// The cookie bag this session answers <c>cookie_request</c> from and stores <c>store_cookie</c> into (1.20.5 and later).
    /// Seed it here, or set its <see cref="CookieStore.Resolver"/> to answer a request the store has no value for, which is how a proxy's forwarding handshake gets past a cookie it expects the client to hold.
    /// </summary>
    CookieStore Cookies { get; }

    /// <summary>Whether this version can carry a serverbound configuration-phase custom payload.</summary>
    bool CanSendConfiguration { get; }

    /// <summary>A reader over a payload that already knows this version's block-position layout.</summary>
    PluginPayloadReader Read(ReadOnlyMemory<byte> payload);

    /// <summary>An empty payload writer that already knows this version's block-position layout.</summary>
    PluginPayloadWriter Write();

    /// <summary>
    /// Subscribes to server dialogs (1.21.6+) before the dial, so a dialog the server shows during configuration is seen.
    /// UMPK publishes the same event for the configuration phase and the play phase; this is the only hook early enough for the former, since <see cref="PluginContext.SessionStarted"/> fires after play begins.
    /// Keep the handler cheap and non-blocking; answer via <see cref="AnswerDialogAsync"/>.
    /// </summary>
    IDisposable SubscribeDialog(
        Action<Umpk.Client.Events.DialogShown> onShown, Action? onCleared = null);

    /// <summary>
    /// Snapshots the dialog the server is currently showing, or null when none is open.
    /// Safe to call during configuration; never throws on versions without dialogs.
    /// </summary>
    PreSessionDialogInfo? GetDialog();

    /// <summary>
    /// Presses a dialog button, submitting input values.
    /// Works in configuration and in play: the response is sent in the phase the connection is actually in.
    /// Prefer a single call carrying all values over separate set + click steps.
    /// </summary>
    /// <param name="buttonIndex">Zero-based index into the shown dialog's button list.</param>
    /// <param name="values">Input values keyed by input key; null submits each input's default.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<Umpk.Client.Actions.DialogClickOutcome> AnswerDialogAsync(
        int buttonIndex,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken ct = default);

    /// <summary>
    /// Submits a custom-click action id directly, for a caller that already knows it (for example a registry-referenced dialog whose body could not be resolved).
    /// </summary>
    Task SubmitDialogActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken ct = default);

    /// <summary>Cancels the shown dialog the way escape does.</summary>
    Task CancelDialogAsync(CancellationToken ct = default);
}

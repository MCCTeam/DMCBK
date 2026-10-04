using Umpk;
using Umpk.Client;
using Umpk.Client.Plugins;
using Umpk.Protocol.Java;

namespace DMCBK.PluginSdk;

/// <summary>
/// The default <see cref="IPreSessionScope"/>: a thin pass to the seams UMPK exposes on an unconnected client (<see cref="UmpkClient.Channels"/>, <see cref="UmpkClient.LoginQueries"/>, <see cref="UmpkClient.Cookies"/>), with every handle it hands out tied to the plugin's own lifetime.
/// <para>
/// The tie matters on one path: a plugin unloaded while its session keeps running.
/// The session's client would otherwise still hold its handler, calling into an assembly the host has unloaded and keeping the plugin's collectible load context alive.
/// Session end needs no help here, because a session's client is built fresh per attempt and goes away with it.
/// </para>
/// </summary>
internal sealed class PreSessionScope : IPreSessionScope
{
    private readonly CancellationToken _untilUnloaded;

    internal PreSessionScope(UmpkClient client, CancellationToken untilUnloaded)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
        _untilUnloaded = untilUnloaded;
    }

    /// <inheritdoc/>
    public UmpkClient Client { get; }

    /// <inheritdoc/>
    public CookieStore Cookies => Client.Cookies;

    /// <inheritdoc/>
    public bool CanSendConfiguration => Client.Channels.CanSendConfiguration;

    /// <inheritdoc/>
    public IDisposable RegisterPlay(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
        => Own(Client.Channels.RegisterPlay(channel, onMessage));

    /// <inheritdoc/>
    public IDisposable RegisterConfiguration(Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
        => Own(Client.Channels.RegisterConfiguration(channel, onMessage));

    /// <inheritdoc/>
    public ValueTask SendConfigurationAsync(Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => Client.Channels.SendConfigurationAsync(channel, data, ct);

    /// <inheritdoc/>
    public IDisposable RegisterLoginQuery(
        Identifier channel, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> responder)
        => Own(Client.LoginQueries.Register(channel, responder));

    /// <inheritdoc/>
    public PluginPayloadReader Read(ReadOnlyMemory<byte> payload) => Client.Channels.Read(payload);

    /// <inheritdoc/>
    public PluginPayloadWriter Write() => Client.Channels.Write();

    /// <inheritdoc/>
    public IDisposable SubscribeDialog(
        Action<Umpk.Client.Events.DialogShown> onShown, Action? onCleared = null)
    {
        ArgumentNullException.ThrowIfNull(onShown);
        IDisposable shown = Client.Events.Subscribe<Umpk.Client.Events.DialogShown>(onShown);
        if (onCleared is null)
            return Own(shown);

        IDisposable cleared = Client.Events.Subscribe<Umpk.Client.Events.DialogCleared>(_ => onCleared());
        return Own(new CompositeDisposable(shown, cleared));
    }

    /// <inheritdoc/>
    public PreSessionDialogInfo? GetDialog()
    {
        if (!Client.State.Dialogs.IsShowing)
            return null;

        return PreSessionDialogInfo.FromUmpk(
            Client.State.Dialogs.Current, Client.State.Dialogs.CurrentRegistryId);
    }

    /// <inheritdoc/>
    public Task<Umpk.Client.Actions.DialogClickOutcome> AnswerDialogAsync(
        int buttonIndex,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken ct = default)
        => Client.Actions.Dialog.ClickAsync(buttonIndex, values, ct);

    /// <inheritdoc/>
    public Task SubmitDialogActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return Client.Actions.Dialog.SubmitActionAsync(Identifier.Parse(actionId), values, ct);
    }

    /// <inheritdoc/>
    public Task CancelDialogAsync(CancellationToken ct = default)
        => Client.Actions.Dialog.CancelAsync(ct);

    private IDisposable Own(IDisposable handle) => new OwnedHandle(handle, _untilUnloaded);

    /// <summary>Two subscriptions released as one.</summary>
    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable _first;
        private readonly IDisposable _second;
        private int _disposed;

        internal CompositeDisposable(IDisposable first, IDisposable second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _first.Dispose();
            _second.Dispose();
        }
    }

    /// <summary>A handle the caller may dispose early, disposed for it when the plugin unloads.</summary>
    private sealed class OwnedHandle : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly CancellationTokenRegistration _unload;
        private int _disposed;

        internal OwnedHandle(IDisposable inner, CancellationToken untilUnloaded)
        {
            _inner = inner;
            _unload = untilUnloaded.Register(Dispose);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _unload.Dispose();
            _inner.Dispose();
        }
    }
}

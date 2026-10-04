using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging;
using Umpk;
using Umpk.Client;
using Umpk.Client.Plugins;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Transport;
using Umpk.Realms;

namespace DMCBK.Core;

/// <summary>
/// One client session over the UMPK engine: resolve the version, build the UMPK client with the mandatory registry/logger/feature wiring (optionally through a forwarding proxy), connect, send and receive chat, auto-reconnect on unexpected disconnects per a policy, and disconnect cleanly.
/// <para>
/// The session lifecycle itself (one attempt to reach play, then a background watch that reconnects per policy) is <see cref="UmpkClientSupervisor"/>'s job, not this class's: this is a thin facade over the supervisor plus the MCC-specific facades (commands, game API, translations, plugin host) that sit beside it.
/// <see cref="DmcbkSessionFactory"/> is the <see cref="IClientSessionFactory"/> that builds and releases the UMPK client for each attempt the supervisor makes.
/// </para>
/// </summary>
public sealed partial class Client : IAsyncDisposable
{
    private readonly IHostInterface _host;
    private readonly ILogger _logger;
    private readonly ChatApi _chat = new();
    private readonly GameSession _gameSession = new();
    private readonly Localization.HostTranslations _translations = new();
    private readonly DmcbkSessionFactory _factory;
    private readonly UmpkClientSupervisor _supervisor;

    private volatile bool _testSessionAttached;
    private ServerEndpoint? _endpoint;
    private int _dialClaimed;
    private int _exitRaised;
    private int _shutdownRequested;
    private readonly TaskCompletionSource _shutdownSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Client(
        ServerEndpoint? endpoint,
        DmcbkAccount account,
        string? tokenStorePath,
        JavaVersion? explicitVersion,
        ClientFeatures features,
        ILoggerFactory loggerFactory,
        IHostInterface host,
        DmcbkConfiguration? configuration,
        IConnectionFactory? proxyFactory,
        bool proxyForPing,
        bool pingForDisplayWhenPinned,
        IReconnectPolicyProvider reconnectProvider)
    {
        _endpoint = endpoint;
        Configuration = configuration;
        UiCulture = Localization.UiCulture.Resolve(configuration?.Localization.Language);
        Features = features.Normalized();
        _host = host;
        LoggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("DMCBK.Core.Client");
        RealmsClientFactory = credential => new RealmsClient(new RealmsClientOptions
        {
            Credential = credential,
            Logger = loggerFactory.CreateLogger("DMCBK.Core.Realms"),
        });

        // Built before the factory because the factory binds it to every session it creates, exactly as it does the chat facade: the container open/close notices come off the live client's event bus.
        var inventory = new InventoryApi(_gameSession, _translations);

        _factory = new DmcbkSessionFactory(
            account, tokenStorePath, explicitVersion, features, loggerFactory, host, configuration,
            proxyFactory, proxyForPing, pingForDisplayWhenPinned, _chat, inventory, _gameSession, _translations);

        _supervisor = new UmpkClientSupervisor(_factory, new ClientSupervisorOptions
        {
            ReconnectPolicy = reconnectProvider,
            LoggerFactory = loggerFactory,
        });

        // Subscribed AFTER the supervisor exists, because following a transfer calls ReconnectAsync, which goes through it.
        _factory.TransferRequested += FollowTransfer;

        Game = new GameApi(
            _chat,
            new WorldApi(_gameSession),
            new EntitiesApi(_gameSession, _translations),
            inventory,
            new MovementApi(_gameSession),
            new PlayerApi(_gameSession, _translations),
            new SessionApi(_gameSession),
            new DialogApi(_gameSession, _translations),
            new MapApi(_gameSession),
            _gameSession);

        Variables = configuration is not null
            ? new VariableStore(configuration.Variables)
            : new VariableStore();
        CommandOutput = host.CommandOutput ?? NullCommandOutput.Instance;
    }

    /// <summary>
    /// The host-provided line sink for background notices (a failed event handler names its script here instead of dying silently).
    /// Commands use their own per-invocation output; this is the only channel background work may write to, and a host without one drops.
    /// </summary>
    public ICommandOutput CommandOutput { get; }

    /// <summary>Raised on every lifecycle transition. Carries the disconnect reason when stopping. Forwards the supervisor's own event.</summary>
    public event EventHandler<ClientStatusChangedEventArgs>? StatusChanged
    {
        add => _supervisor.StatusChanged += value;
        remove => _supervisor.StatusChanged -= value;
    }

    /// <summary>
    /// The host's logger factory (the CLI's <c>ConsoleLoggerFactory</c> in a real run, <c>NullLoggerFactory</c> when the embedder supplied none).
    /// Kept so the parts of the client that are not sessions, the command surface in particular, can report diagnostics through the same pipeline UMPK and the session factory already use, instead of having no way to say anything that is not user-facing text.
    /// </summary>
    internal ILoggerFactory LoggerFactory { get; }

    /// <summary>The current lifecycle status.</summary>
    public ClientStatus Status => _testSessionAttached ? ClientStatus.Playing : _supervisor.Status;

    /// <summary>The reason the last session ended, once it has ended.</summary>
    public DisconnectInfo? LastDisconnect => _supervisor.LastDisconnect;

    /// <summary>The negotiated version once a session has started, else null.</summary>
    public JavaVersion? NegotiatedVersion => _factory.NegotiatedVersion;

    /// <summary>
    /// Raised once a server-status ping produces a result: normally as part of auto-detecting the version, and additionally (best-effort) even when the version was pinned, so a host's connect-time status panel always has something to show, matching legacy pinging on every connect for the display.
    /// May fire more than once per connect attempt (an auto-reconnect re-pings).
    /// Forwards <see cref="DmcbkSessionFactory.StatusReceived"/> directly.
    /// </summary>
    public event EventHandler<ServerStatusReceivedEventArgs>? ServerStatusReceived
    {
        add => _factory.StatusReceived += value;
        remove => _factory.StatusReceived -= value;
    }

    /// <summary>
    /// Raised when the server asks the client to move to another address (<c>minecraft:transfer</c>).
    /// The client FOLLOWS the transfer itself, reconnecting to the new address; this event is for a host that wants to say so.
    /// </summary>
    public event EventHandler<ServerTransferEventArgs>? ServerTransferRequested
    {
        add => _factory.TransferRequested += value;
        remove => _factory.TransferRequested -= value;
    }

    /// <summary>
    /// Raised before each dial, with the <see cref="ConnectPlan"/> for that attempt: the address, the client-settings announce, and a veto.
    /// Fires once per attempt, automatic reconnects included, before the version ping and before the socket.
    /// See <see cref="ConnectPlan"/> for what a handler may do.
    /// </summary>
    public event EventHandler<ConnectPlan>? BeforeConnect
    {
        add => _factory.BeforeConnect += value;
        remove => _factory.BeforeConnect -= value;
    }

    /// <summary>
    /// Raised after <c>reload</c> has read a fresh configuration snapshot and applied it.
    /// The snapshot is on the event, because <see cref="Configuration"/> is the one this client was built from and does not change.
    /// </summary>
    public event EventHandler<ConfigurationReloadedEventArgs>? ConfigurationReloaded;

    /// <summary>
    /// Raised once when the client is shutting down, while the session is still live and before anything is torn down.
    /// See <see cref="BeforeExitEventArgs"/> for the deadline and for deferring work.
    /// </summary>
    public event EventHandler<BeforeExitEventArgs>? BeforeExit;

    /// <summary>
    /// The most recently authenticated online session, retained so Realms resolution can reuse the resolved <see cref="Umpk.Auth.JavaSession"/> (its Microsoft access token proves Realms ownership).
    /// Null when the current or last session was offline.
    /// It is not cleared on disconnect so a follow-up <c>connect</c> to a Realm entry can resolve against the account MCC just logged in as.
    /// </summary>
    public Umpk.Auth.JavaSession? CurrentSession => _factory.CurrentSession;

    /// <summary>
    /// Builds the short-lived <see cref="IRealmsClient"/> the <c>connect</c> command uses to resolve a <see cref="ConfiguredServerKind.Realm"/> entry, given the credential built from the current session.
    /// Defaults to a plain <see cref="RealmsClient"/> over the default HTTP seam and this client's logger factory; hosts or tests may replace it (for example with one that returns a fake <see cref="IRealmsClient"/>, or a real one pointed at a scripted HTTP handler).
    /// </summary>
    public Func<RealmsSessionCredential, IRealmsClient> RealmsClientFactory { get; set; }

    /// <summary>The live-session facade. Its members throw while not in a live session.</summary>
    public GameApi Game { get; }

    /// <summary>The feature composition for this client.</summary>
    public ClientFeatures Features { get; }

    /// <summary>The <c>%var%</c> store shared by <c>set</c> and input/argument expansion.</summary>
    public VariableStore Variables { get; }

    /// <summary>
    /// The live-session handle, for command-side service resolution (the registry suggestion source needs the bound <see cref="UmpkClient"/>'s live registries, read fresh on every query rather than pinned to whatever was live when the source was built).
    /// </summary>
    internal GameSession GameSession => _gameSession;

    /// <summary>The translation source (vanilla en_us plus any host locale overrides); host-renderable.</summary>
    public Umpk.Text.ITranslationSource Translations => _translations;

    /// <summary>
    /// Observes each session's raw wire frames: direction, phase, wire id, the decoded packet when there is one, and the bytes.
    /// Re-attached automatically to every reconnect.
    /// </summary>
    /// <remarks>
    /// Costs nothing when nobody subscribes: UMPK only attaches outbound observation while a handler is registered.
    /// The handler runs on the connection's read loop, not the session loop, so it must be cheap and must not block, and the payload span is only valid for the duration of the call.
    /// </remarks>
    public void ObservePacketFrames(Action<int, Umpk.Protocol.Java.PacketObservation> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _factory.SessionClientCreated += (umpk, protocol) =>
            umpk.PacketFrameObserved += observation => observer(protocol, observation);
    }

    /// <summary>
    /// Observes each session's client before it dials, initial connect and every reconnect alike.
    /// The client is unconnected: subscribe to it, do not drive it.
    /// This is the only hook early enough to see configuration-phase events (server dialogs included) that a play-entry subscription would miss.
    /// </summary>
    /// <remarks>
    /// Handlers run before the connect, so they must be cheap and must not block.
    /// A session's client is built fresh per attempt; the observer runs once per attempt.
    /// </remarks>
    public void ObserveSessionClients(Action<Umpk.Client.UmpkClient> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _factory.SessionClientCreated += (umpk, _) => observer(umpk);
    }

    /// <summary>The currently bound session client, or null between sessions. Internal: the game facades own the typed surface.</summary>
    internal Umpk.Client.UmpkClient? BoundSessionClient => GameSession.Current;

    /// <summary>The configuration snapshot this client was built from, when built from configuration; else null.</summary>
    public DmcbkConfiguration? Configuration { get; }

    /// <summary>
    /// The attached plugin host, once the embedding host has built and attached one (see <see cref="AttachPluginHost"/>).
    /// Null when the client runs without a plugin host.
    /// The <c>plugins</c> / <c>plugin load</c> commands and the config <c>reload</c> consult it through this seam.
    /// </summary>
    public IPluginHost? PluginHost { get; private set; }

    /// <summary>
    /// The supervisor's extensions: components kept activated for as long as they are registered, spanning every reconnect this client makes.
    /// <c>DMCBK.PluginSdk.PluginHost</c> registers one extension per loaded plugin here, so each plugin's session lifecycle (activate once, re-attach per session/reconnect, deactivate once on unload) rides UMPK's own extension host instead of a hand-rolled fan-out.
    /// </summary>
    internal Umpk.Client.ClientExtensionCollection Extensions => _supervisor.Extensions;

    /// <summary>The host UI hooks (notifications, container/book/tab views), or null when the host is text-only.</summary>
    public IHostUi? HostUi => _host.Ui;

    /// <summary>
    /// A per-instance override for UMPK's own <see cref="ClientOptions.ReadIdleTimeout"/> read-idle backstop, applied in <see cref="ApplySessionOptions"/>.
    /// Null (the default) leaves UMPK's own default (30 seconds, vanilla's own rule) in place.
    /// Internal because it is not a user setting; the only reason it is settable at all is that a headless test cannot wait thirty seconds to prove the backstop fires.
    /// </summary>
    internal TimeSpan? LivenessTimeout
    {
        get => _factory.LivenessTimeout;
        set => _factory.LivenessTimeout = value;
    }

    /// <summary>
    /// Attaches the plugin host so the in-core plugin commands can reach it.
    /// Called once by the embedding host after it has discovered and loaded plugins.
    /// Idempotent replacement is allowed (last wins).
    /// </summary>
    public void AttachPluginHost(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        PluginHost = host;
    }

    /// <summary>
    /// The attached plugin market, once the embedding host has built and attached one (see <see cref="AttachPluginMarket"/>).
    /// Null when the client runs without one, and the install verbs then say so rather than pretending.
    /// Separate from <see cref="PluginHost"/> because loading a plugin and fetching one are separate powers: a host may want the first without the second.
    /// </summary>
    public IPluginMarket? PluginMarket { get; private set; }

    /// <summary>
    /// Attaches the plugin market so the install/update/uninstall verbs can reach it.
    /// Called once by the embedding host, which also supplies the market's confirmation callback.
    /// Idempotent (last wins).
    /// </summary>
    public void AttachPluginMarket(IPluginMarket market)
    {
        ArgumentNullException.ThrowIfNull(market);
        PluginMarket = market;
    }

    /// <summary>
    /// Resolves the version, builds the UMPK client, and connects.
    /// Returns once the play session is live and a background supervisor is watching for disconnects (to auto-reconnect per the policy).
    /// Throws a typed exception on version-resolution (<see cref="VersionResolutionException"/>), connect (<see cref="ConnectFailedException"/>), or login (<see cref="LoginRejectedException"/>) failure, or whatever <see cref="DmcbkSessionFactory"/> itself threw (an <see cref="DmcbkClientException"/> or an <see cref="Umpk.Auth.AuthException"/>).
    /// </summary>
    // Following the transfer is the client's job, not the host's: the legacy client did it inside McClient.Transfer (McClient.cs:408-430), and a host that merely logged the event would leave the user sitting on the hub wondering why the menu did nothing.
    // Guarded against overlap the same way legacy guarded it, because a transfer chain can land a second instruction while the first is still unwinding.
    private int _transferInProgress;

    private void FollowTransfer(object? sender, ServerTransferEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _transferInProgress, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconnectAsync(new ServerSelection { Host = e.Host, Port = (ushort)e.Port })
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed transfer leaves the supervisor's own reconnect policy in charge; nothing here can do better than let it retry the address it already has.
            }
            finally
            {
                Interlocked.Exchange(ref _transferInProgress, 0);
            }
        });
    }

    /// <summary>Provides the client runtime operation.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ServerEndpoint endpoint = _endpoint ?? throw new DmcbkNoServerConfiguredException(
            "No server is configured. Set one in servers.toml or client.toml, or connect to one by address.");

        Interlocked.Exchange(ref _dialClaimed, 1);
        return _supervisor.StartAsync(endpoint, ct);
    }

    /// <summary>
    /// Tears down the current session and starts a fresh one, optionally switching to a new server and/or account.
    /// Preserves the status machine (<see cref="ClientStatus.Reconnecting"/> during the switch) and re-arms the auto-reconnect supervisor on success.
    /// The core of the <c>/connect</c> and <c>/reco</c> commands.
    /// </summary>
    public Task ReconnectAsync(
        ServerSelection? newServer = null, AccountSelection? newAccount = null, CancellationToken ct = default)
    {
        _factory.PrepareReconnect(newServer, newAccount);
        ServerEndpoint? target = newServer?.ToEndpoint() ?? _endpoint;

        // A client that has never dialled has no session to replace, and the supervisor refuses a reconnect before its first start.
        // This is what makes connect and reco work from the idle state, where the client was built with no server (or with one it was told not to dial).
        if (Interlocked.Exchange(ref _dialClaimed, 1) == 0)
        {
            ServerEndpoint endpoint = target ?? throw new DmcbkNoServerConfiguredException(
                "No server is configured. Name one to connect to, or set one in servers.toml.");

            _endpoint = endpoint;
            return _supervisor.StartAsync(endpoint, ct);
        }

        return _supervisor.ReconnectAsync(newServer?.ToEndpoint(), ct);
    }

    /// <summary>Disconnects the session cleanly and stops any auto-reconnect.</summary>
    public async Task StopAsync()
    {
        await RaiseBeforeExitAsync().ConfigureAwait(false);
        await _supervisor.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Completes once <see cref="RequestShutdown"/> has been called.
    /// Hosts await this alongside their input loop so an in-process requester (the MCP server's quit tool, a bridge command) can end the run the same way typing <c>quit</c> does.
    /// Signalling only: teardown still flows through the existing <see cref="StopAsync"/>/<see cref="DisposeAsync"/> path, so <c>BeforeExit</c> hooks run.
    /// </summary>
    public Task ShutdownRequested => _shutdownSource.Task;

    /// <summary>
    /// Requests a clean process shutdown: completes <see cref="ShutdownRequested"/> once, no matter how often it is called and from which thread.
    /// Never throws.
    /// Does not disconnect by itself; the host stops the client on its way out exactly as it does for a typed <c>quit</c>.
    /// </summary>
    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            _shutdownSource.TrySetResult();
    }

    /// <inheritdoc />
    private Task? _disposeTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_moduleGate) return new ValueTask(_disposeTask ??= DisposeClientAsync());
    }

    private async Task DisposeClientAsync()
    {
        try { await RaiseBeforeExitAsync().ConfigureAwait(false); }
        finally
        {
            try { await DisposeModulesAsync().ConfigureAwait(false); }
            finally { await _supervisor.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>
    /// How long the deferred work of <see cref="BeforeExit"/> is awaited before shutdown continues without it.
    /// Not a user setting; a test shrinks it rather than waiting out the real one.
    /// </summary>
    internal TimeSpan ExitDeadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Announces the snapshot <c>reload</c> has just applied. See <see cref="ConfigurationReloaded"/>.</summary>
    internal void RaiseConfigurationReloaded(Configuration.DmcbkConfiguration configuration)
        => ConfigurationReloaded?.Invoke(this, new ConfigurationReloadedEventArgs(Configuration, configuration));

    /// <summary>
    /// Raises <see cref="BeforeExit"/> once and waits for what the handlers deferred, up to <see cref="ExitDeadline"/>.
    /// A handler that throws, and work that misses the deadline, are logged and left behind: nothing a plugin does here may stop the client from shutting down.
    /// </summary>
    private async Task RaiseBeforeExitAsync()
    {
        EventHandler<BeforeExitEventArgs>? handlers = BeforeExit;
        if (handlers is null || Interlocked.Exchange(ref _exitRaised, 1) != 0)
            return;

        using var deadline = new CancellationTokenSource(ExitDeadline);
        var args = new BeforeExitEventArgs(deadline.Token);
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<BeforeExitEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A BeforeExit handler threw; the shutdown continues.");
            }
        }

        Task[] deferred = args.Deferred();
        if (deferred.Length == 0)
            return;

        try
        {
            await Task.WhenAll(deferred).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("BeforeExit work did not finish within {Deadline}; abandoning it.", ExitDeadline);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BeforeExit work failed.");
        }
    }

    /// <summary>
    /// True when a failed send failed because the transport itself is gone, as opposed to the send being refused, unsupported, or cancelled.
    /// A write is the one operation that finds out about a broken connection immediately, so this is the difference between "that command did not work" and "there is no connection any more".
    /// </summary>
    /// <remarks>
    /// The live observation this classifies is <c>IOException: Unable to write data to the transport connection: Broken pipe.</c>, which is an <see cref="IOException"/> wrapping a <see cref="System.Net.Sockets.SocketException"/>.
    /// <see cref="ObjectDisposedException"/> is deliberately NOT here: it is what a locally torn-down session throws, and that is a local stop, not a lost connection.
    /// </remarks>
    internal static bool IsTransportFault(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or System.Net.Sockets.SocketException or ConnectionClosedException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a failed send for the user.
    /// When the failure is a transport fault, ends the session with it instead of leaving the client reporting Playing from a stale snapshot.
    /// <para>
    /// A failed write proves the session is disconnected, so the status read after it must reflect the disconnect.
    /// The watchdog still bounds dead sessions the client has not detected yet.
    /// </para>
    /// </summary>
    internal string DescribeSendFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsTransportFault(exception))
            return CommandStrings.Error(exception.Message);

        var info = new DisconnectInfo { Reason = CloseReason.SocketEof, Fault = exception };

        // Routed through the same fault-report seam a real Disconnected event would drive, so the supervisor owns the resulting transition (and the reconnect policy still applies) exactly as it would have.
        _supervisor.ReportSessionFault(info);
        return CommandStrings.ConnectionLost(info.Describe());
    }

    /// <summary>
    /// Whether to install the chat-signing provider, which is what <c>signature.loginwithsecureprofile</c> controls.
    /// Defaults to on when there is no configuration at all (an embedding host that never loaded one), matching the setting's own default.
    /// </summary>
    internal static bool SigningEnabled(DmcbkConfiguration? configuration)
        => configuration?.Chat.Signature.LoginWithSecureProfile ?? true;

    /// <summary>
    /// The UMPK client options every MCC session is built with.
    /// Split out of the builder lambda so the choices below are assertable without standing up a connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AutoSendPosition</c>.
    /// Tell the server where we are.
    /// Without it the server keeps the pre-walk position, so <c>move</c>, ranged <c>dig</c> and pathfinding plugins have no visible effect.
    /// </para>
    /// <para>
    /// The suppressed risk was "an un-corrected falling position gets us kicked".
    /// It is real, but silence does not avoid it, it only hides it.
    /// What actually bounds it: UMPK auto-sends only once Physics is on, the world exists and the player has spawned, so a client with that gate off still sends nothing; a server teleport re-seeds the engine (<c>PhysicsEngineHolder.ResyncPosition</c>), so a position vanilla rejects comes back as a teleport the client adopts rather than insisting on; and vanilla's own limits are per server tick (100 blocks squared past its own delta, <c>ServerGamePacketListenerImpl.handleMovePlayer</c>), which a per-tick cadence never approaches.
    /// The float kick needs 80+ consecutive ticks of unsupported hover, which is a genuinely broken local world and is worth surfacing rather than concealing.
    /// </para>
    /// <para>
    /// UMPK now reports position on tick while a movement lease is held, so a walk no longer arrives as a single jump at the end.
    /// </para>
    /// <para>
    /// <c>ClientInformation</c> is the full client-settings announce from config.
    /// UMPK used to hardcode everything except locale and view distance, so the configured chat visibility, chat colors, main hand and skin parts were modelled but never sent (the unannounced-settings gap).
    /// </para>
    /// <para>
    /// <c>ReadIdleTimeout</c> is left at UMPK's own default (30 seconds, vanilla's own rule) unless <paramref name="readIdleTimeoutOverride"/> is given, which only <see cref="LivenessTimeout"/> ever supplies: production never touches it, and a headless test uses it to shrink the window instead of waiting out the real one.
    /// </para>
    /// </remarks>
    internal static void ApplySessionOptions(
        ClientOptions options, DmcbkConfiguration? configuration, TimeSpan? readIdleTimeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AutoSendPosition = true;
        if (configuration is not null)
        {
            options.ClientInformation = configuration.ClientSettings
                .ToClientInformation(Localization.UiCulture.Resolve(configuration.Localization));
            options.ClientBrand = BrandString(configuration.Connection.Brand);
        }

        if (readIdleTimeoutOverride is { } timeout)
            options.ReadIdleTimeout = timeout;
    }

    /// <summary>
    /// The literal client-brand string for a configured <see cref="BrandKind"/>, or null to announce none.
    /// <para>
    /// <c>Connection.Brand</c> was documented, parsed and validated for the whole of this client's life and consumed by nothing: the setting existed, the enum existed, and no session ever put a brand on the wire.
    /// That is not a neutral default.
    /// A vanilla client announces "vanilla" on <c>minecraft:brand</c> during login, every modded client announces something, and a client that announces NOTHING is therefore distinguishable from all of them by its silence, which is the opposite of what the "vanilla" setting was there to buy.
    /// UMPK carries the announce now (ClientOptions.ClientBrand) and this is where the configured choice reaches it.
    /// </para>
    /// </summary>
    internal static string? BrandString(BrandKind brand) => brand switch
    {
        BrandKind.Vanilla => "vanilla",
        BrandKind.Empty => null,
        _ => "mcc",
    };

    /// <summary>
    /// How long the loop-liveness probe waits before declaring the session loop wedged.
    /// Generous on purpose: it is only ever consulted when the caller might be on the loop, and no healthy loop work item takes anything close to this long.
    /// </summary>
    internal static readonly TimeSpan LoopProbeWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Binds an UNCONNECTED UMPK client and reports the session as live, so the internal command surface can be dispatched end to end without a server.
    /// <para>
    /// This exists because a command test that stops at the "not connected" gate cannot tell a fixed command from a broken one: every branch past that gate is unreachable, so such a test passes just as happily against the defect it is supposed to pin.
    /// With a bound (if idle) session the real branches run, which is what makes a revert check meaningful.
    /// Nothing here touches a socket, and the bound client reports no negotiated session, so every version-gated send correctly answers "cannot send".
    /// </para>
    /// </summary>
    internal void AttachIdleSessionForTesting(UmpkClient umpk)
    {
        ArgumentNullException.ThrowIfNull(umpk);
        _chat.Bind(umpk);
        _gameSession.Bind(umpk);
        _testSessionAttached = true;
    }

    /// <summary>
    /// Blocks on a command's async work and turns the one case that can never finish into a clear error.
    /// <para>
    /// Internal commands are synchronous (Brigadier's execute returns an int), so <c>CommandContext.Run</c> blocks on the async facade.
    /// That is harmless from the console reader thread, but a plugin that dispatches a command INLINE from a session event handler is running inside a session-loop work item, and the facade call it blocks on needs that same loop to advance.
    /// The result was an unbounded hang, observed live as a server timeout.
    /// </para>
    /// <para>
    /// Detection is exact rather than a guess.
    /// <c>ISessionScheduler.IsCurrent</c> alone is not usable: it is an async-local that also reads true inside <c>Task.Run</c> work started from the loop (UMPK's own <c>RunOffLoop</c> does exactly that), which would make legitimate off-loop dispatches throw.
    /// So when <c>IsCurrent</c> says we MIGHT be on the loop, this posts a trivial work item and races it against the command: if we really are blocking the loop, that probe can never run; if we are merely a descendant of a loop item, it runs immediately and the wait proceeds unbounded, so a slow but legitimate <c>/move</c> or <c>/connect</c> is never cut short.
    /// </para>
    /// </summary>
    internal void WaitForCommandWork(Task work) => WaitForWork(work, _factory.Scheduler, LoopProbeWindow);

    /// <summary>
    /// The loop-block guard itself, taken apart from the client so it can be exercised against a real scheduler without a live server.
    /// See <see cref="WaitForCommandWork"/> for the reasoning.
    /// </summary>
    internal static void WaitForWork(Task work, Umpk.Hosting.ISessionScheduler? scheduler, TimeSpan probeWindow)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (scheduler is null || !scheduler.IsCurrent || work.IsCompleted)
        {
            work.GetAwaiter().GetResult();
            return;
        }

        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            scheduler.Post(() => probe.TrySetResult());
        }
        catch (ObjectDisposedException)
        {
            // The session loop is already shutting down; nothing can wedge on it.
            work.GetAwaiter().GetResult();
            return;
        }

        int completed = Task.WaitAny([work, probe.Task], probeWindow);
        if (completed < 0)
            throw new DmcbkSessionLoopBlockedException(CommandStrings.SessionLoopBlocked);

        work.GetAwaiter().GetResult();
    }
}

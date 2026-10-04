using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging;
using Umpk;
using Umpk.Auth;
using Umpk.Auth.Session;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Client.Plugins;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Signing;
using Umpk.Protocol.Java.Transport;

namespace DMCBK.Core;

/// <summary>
/// The <see cref="IClientSessionFactory"/> behind every <see cref="Client"/> session: everything the old hand-rolled state machine's <c>EstablishSessionAsync</c> did MINUS supervision, which <see cref="Umpk.Client.UmpkClientSupervisor"/> now owns instead.
/// One factory instance backs one client's whole lifetime (initial connect and every reconnect), which is why the target server/version/account are mutable fields rather than constructor arguments: <see cref="PrepareReconnect"/> is how <see cref="Client.ReconnectAsync"/> updates them before handing the dial to the supervisor.
/// </summary>
internal sealed class DmcbkSessionFactory : IClientSessionFactory
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    // when the version is pinned, ResolveVersionAsync never pings, so a display-only ping is fired in the background purely for the connect-time status panel.
    // It must never delay the real connect, so it gets its own short, independent timeout rather than sharing PingTimeout.
    private static readonly TimeSpan DisplayPingTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _tokenStorePath;
    private readonly ClientFeatures _features;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IHostInterface _host;
    private readonly DmcbkConfiguration? _configuration;
    private readonly IConnectionFactory? _proxyFactory;
    private readonly bool _proxyForPing;
    private readonly bool _pingForDisplayWhenPinned;
    private readonly ChatApi _chat;
    private readonly InventoryApi _inventory;
    private readonly GameSession _gameSession;
    private readonly Localization.HostTranslations _translations;
    private readonly Umpk.Client.ServerVersionNegotiator _negotiator;
    private readonly object _gate = new();

    private JavaVersion? _pinnedVersion;
    private DmcbkAccount _account;
    private TimeSpan? _readIdleTimeoutOverride;
    private MinecraftAuthFlow? _authFlow;
    private YggdrasilSessionService? _sessionService;
    private Diagnostics.PacketDebugLogger? _packetDebug;
    private IDisposable? _resourcePackSubscription;

    /// <summary>
    /// Raised with each freshly built session client, before it connects.
    /// The host seam a packet capture attaches to; see <see cref="Umpk.Client.UmpkClient.PacketFrameObserved"/> for the feed itself.
    /// </summary>
    internal Action<UmpkClient, int>? SessionClientCreated { get; set; }

    internal DmcbkSessionFactory(
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
        ChatApi chat,
        InventoryApi inventory,
        GameSession gameSession,
        Localization.HostTranslations translations)
    {
        _account = account;
        _tokenStorePath = tokenStorePath;
        _pinnedVersion = explicitVersion;
        _features = features;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("DMCBK.Core.Client");
        _host = host;
        _configuration = configuration;
        _proxyFactory = proxyFactory;
        _proxyForPing = proxyForPing;
        _pingForDisplayWhenPinned = pingForDisplayWhenPinned;
        _chat = chat;
        _inventory = inventory;
        _gameSession = gameSession;
        _translations = translations;
        _negotiator = new Umpk.Client.ServerVersionNegotiator(loggerFactory);
    }

    /// <summary>The negotiated version once a session has been built, else null.</summary>
    internal JavaVersion? NegotiatedVersion { get; private set; }

    /// <summary>Raised whenever a status ping (auto-detect or display-only) produces a result. See <see cref="Client.ServerStatusReceived"/>.</summary>
    internal event EventHandler<ServerStatusReceivedEventArgs>? StatusReceived;

    /// <summary>
    /// Raised when the server asks the client to move to another address (<c>minecraft:transfer</c>, 1.20.5+, in either the configuration or the play phase).
    /// See <see cref="Client.ServerTransferRequested"/>.
    /// </summary>
    internal event EventHandler<ServerTransferEventArgs>? TransferRequested;

    /// <summary>Raised before each dial with the plan for that attempt. See <see cref="Client.BeforeConnect"/>.</summary>
    internal event EventHandler<ConnectPlan>? BeforeConnect;

    /// <summary>The most recently authenticated online session (see <see cref="Client.CurrentSession"/>).</summary>
    internal JavaSession? CurrentSession { get; private set; }

    /// <summary>The scheduler owned by the currently live session, or null between sessions.</summary>
    internal Umpk.Hosting.ISessionScheduler? Scheduler { get; private set; }

    /// <summary>See <see cref="Client.LivenessTimeout"/>.</summary>
    internal TimeSpan? LivenessTimeout
    {
        get
        {
            lock (_gate)
                return _readIdleTimeoutOverride;
        }

        set
        {
            lock (_gate)
                _readIdleTimeoutOverride = value;
        }
    }

    /// <summary>
    /// Updates the dial target before <see cref="Client.ReconnectAsync"/> hands the reconnect to the supervisor.
    /// A named <paramref name="newServer"/> replaces the pinned version outright (even with null, meaning "re-detect"): the new target may speak a different protocol than the old one, so the last negotiated version must not leak across the switch.
    /// A null <paramref name="newServer"/> (a plain <c>reco</c>) leaves both untouched, so <see cref="ResolveVersionAsync"/> reuses whatever is pinned or was last negotiated, exactly like an automatic reconnect does.
    /// </summary>
    internal void PrepareReconnect(ServerSelection? newServer, AccountSelection? newAccount)
    {
        lock (_gate)
        {
            if (newServer is not null)
            {
                _pinnedVersion = newServer.Version;
                NegotiatedVersion = null;
            }

            if (newAccount is not null)
                _account = newAccount.Account;
        }
    }

    /// <inheritdoc />
    public async ValueTask<UmpkClient> CreateAsync(SessionAttempt attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        DmcbkAccount account;
        JavaVersion? pinned;
        lock (_gate)
        {
            account = _account;
            pinned = _pinnedVersion;
        }

        // Fail fast: an online account needs a host-provided auth interaction to drive the interactive login.
        if (account.IsOnline && _host.AuthInteraction is null)
        {
            throw new DmcbkAuthInteractionUnavailableException(
                $"The {account.Kind} account requires an interactive login, but the host provides no auth interaction.");
        }

        // The plan is offered before anything reaches the network, so a redirect moves the version ping and the login as well as the socket, and a veto costs nothing.
        ConnectPlan plan = RaiseBeforeConnect(attempt);
        if (plan.VetoReason is { } vetoed)
            throw new DmcbkConnectVetoedException(vetoed);

        ServerEndpoint endpoint = plan.ToEndpoint();
        if (endpoint != attempt.Endpoint)
            attempt.Redirect(endpoint);

        // Connection.SrvResolve was bound and never consulted; UMPK's builder defaults to a passthrough resolver, so the mode now actually selects.
        // UMPK applies vanilla's own port gate (SRV only when no explicit port was given), so this seam only chooses WHETHER to look up, not WHEN.
        // Fast and Yes used to resolve identically (DnsSrvResolver had no timeout parameter); it now does, so Fast gets a short DNS wait and Yes gets the resolver's own full default, closing the SRV-mode follow-up.
        IServerAddressResolver resolver = _configuration?.Connection.SrvResolve switch
        {
            SrvResolveMode.No => DnsSrvResolver.Passthrough,
            SrvResolveMode.Fast => new DnsSrvResolver(TimeSpan.FromSeconds(1)),
            _ => new DnsSrvResolver(),
        };

        JavaVersion version = await ResolveVersionAsync(endpoint, resolver, pinned, ct).ConfigureAwait(false);
        lock (_gate)
            NegotiatedVersion = version;

        int protocol = version.Version.Protocol;

        // Chat is rendered against the negotiated era's own vanilla table; see the historical note this carried in Client.EstablishSessionAsync before the split (protocols 47..340 disagree with the modern en_us.json on several keys' argument arity and on tile.*/entity.<Name>.name entirely).
        // Set before the connect so nothing the server sends is rendered against the wrong era.
        _translations.UseProtocol(protocol);

        // Resource packs belong to one session: a reconnect (possibly to another server) must not keep rendering with the previous server's pack strings.
        // Pushes re-layer as they arrive.
        _translations.ClearPackTranslations();

        AuthOutcome auth = await AuthenticateAsync(account, attempt, ct).ConfigureAwait(false);
        lock (_gate)
            CurrentSession = auth.Session;

        // Own the session scheduler rather than letting UMPK default one in; see WaitForCommandWork/WaitForWork on Client for why that ownership matters (telling a wedged session loop apart from a slow command).
        var scheduler = new Umpk.Hosting.ChannelSessionScheduler(
            ex => _logger.LogError(ex, "Session loop work threw."));

        var umpkBuilder = new UmpkClientBuilder()
            .UseScheduler(scheduler)
            .UseVersion(version)
            .UseProfile(auth.Profile)
            // H1: without static registries entities silently resolve to minecraft:unknown and non-air item stacks drop under the strict decode policy.
            // Always wire them for the negotiated protocol.
            .UseStaticRegistries(JavaGameData.Registries(protocol))
            .UseAddressResolver(resolver)
            // Block shapes: left unset.
            // UMPK's builder then defaults to the negotiated protocol's own generated shape tables, which carry real slab/stair/fence/wall/pane/carpet geometry per protocol.
            // Only call UseBlockShapes to model a modded server's non-vanilla shapes.
            .UseLoggerFactory(_loggerFactory)
            // Copied field-by-field rather than handing UMPK's builder the _features instance itself: that instance is mutable and owned by this factory for the client's whole lifetime, so sharing it would let a host mutate a live session's composition after Build() ran.
            .ConfigureFeatures(f =>
            {
                f.Terrain = _features.Terrain;
                f.Entities = _features.Entities;
                f.Inventory = _features.Inventory;
                f.Physics = _features.Physics;
                f.Pathfinding = _features.Pathfinding;
            })
            .ConfigureOptions(o =>
            {
                Client.ApplySessionOptions(o, _configuration, LivenessTimeout);
                o.ClientInformation = plan.ClientInformation;
            })
            // Resource packs: accept (the default) so require-resource-pack servers keep the session, decline or prompt per Localization.ResourcePackPolicy. Accepted packs layer their translations into _translations in the background when Localization.LoadResourcePackTranslations is on.
            .ConfigurePolicies(p =>
            {
                p.ResourcePack = ResourcePackPolicyFactory.Build(_configuration, _host, _translations, _logger);
            });

        // Route the whole session through the proxy when one is configured.
        // UMPK uses one connection factory for the session, and the proxy covers the whole session rather than only the login.
        if (_proxyFactory is not null)
            umpkBuilder.UseConnectionFactory(_proxyFactory);

        // Online mode: prove session ownership to the session server before the server enables encryption.
        if (auth.Authenticator is not null && auth.Credentials is not null)
            umpkBuilder.UseAuthenticator(auth.Authenticator, auth.Credentials);

        // Chat signing: online account on a signing-era version fetches player certificates through the auth flow and feeds them into the signing seam.
        // Offline (or a non-signing version) stays unsigned.
        // The gate reads the connection that Build() below produces, so it closes over a local the lambda reads at call time rather than capturing a value that does not exist yet.
        UmpkClient? built = null;
        if (auth.Flow is not null && auth.Session is not null
            && !string.Equals(version.Features.ChatSigning, "none", StringComparison.Ordinal)
            && Client.SigningEnabled(_configuration))
        {
            // Offline-mode servers never negotiate encryption and assign a name-derived uuid, which a Mojang-signed profile key can never validate against; announcing it there turns a successful join into "Invalid signature for profile public key" seconds later.
            // See OnlineModeSigningGate for the vanilla citations and for why the guard belongs here and not in UMPK.
            IChatSigningProvider provider = new AuthFlowCertificateProvider(auth.Flow, auth.Session);
            if (OnlineModeSigningGate.AppliesTo(version.Features.ChatSigning))
                provider = new OnlineModeSigningGate(provider, () => built?.IsConnectionEncrypted ?? false);

            umpkBuilder.UseChatSigning(provider);
        }

        // Unconnected: the supervisor calls ConnectAsync itself, immediately after this returns.
        UmpkClient umpk = umpkBuilder.Build();
        built = umpk;

        // The host's hook, before anything connects, so a diagnostics tap sees the first frame of the session rather than joining partway through.
        // One session per connect AND per reconnect, so a subscriber is handed each new client rather than a stale one.
        SessionClientCreated?.Invoke(umpk, protocol);

        lock (_gate)
        {
            Scheduler = scheduler;
            _authFlow = auth.Flow;
            _sessionService = auth.Authenticator as YggdrasilSessionService;
        }

        // The consumer for logging.packetdebugmessages / logging.packetdebugexclusions. Null when the setting is off or debug logging is filtered out, so a disabled setting costs nothing per packet.
        _packetDebug = Diagnostics.PacketDebugLogger.TryAttach(
            umpk,
            _configuration?.Logging.PacketDebugMessages ?? false,
            _configuration?.Logging.PacketDebugExclusions ?? [],
            _loggerFactory.CreateLogger("DMCBK.Core.Packets"));

        // UMPK decodes resource-pack pops but has no applier for them, so pack layers would survive a pop without this: one subscription clears them as pops arrive, in either phase.
        _resourcePackSubscription = umpk.Events.Subscribe<PacketReceived>(
            received => ResourcePackPolicyFactory.ClearForPop(received.Packet, _translations));

        _chat.Bind(umpk);
        _inventory.Bind(umpk);
        _gameSession.Bind(umpk);

        // A transfer is a server instruction, not a disconnect to be reported.
        // The legacy client acted on it (Protocol18.cs:648 and :3396 both call McClient.Transfer), and without this the new client simply ended the session and stayed put, which is what a hub GUI's "send me to survival" button looked like doing nothing.
        // Subscribed on the session so a transfer chain (hub -> lobby -> game) keeps working; the previous session's subscription dies with its client.
        umpk.Events.Subscribe<ServerTransferRequested>(
            e => TransferRequested?.Invoke(this, new ServerTransferEventArgs(e.Host, e.Port)));

        return umpk;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(UmpkClient client, DisconnectInfo? disconnect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        Diagnostics.PacketDebugLogger? packetDebug;
        IDisposable? resourcePackSubscription;
        MinecraftAuthFlow? authFlow;
        YggdrasilSessionService? sessionService;
        lock (_gate)
        {
            packetDebug = _packetDebug;
            _packetDebug = null;
            resourcePackSubscription = _resourcePackSubscription;
            _resourcePackSubscription = null;
            authFlow = _authFlow;
            _authFlow = null;
            sessionService = _sessionService;
            _sessionService = null;
            Scheduler = null;
        }

        packetDebug?.Dispose();
        resourcePackSubscription?.Dispose();
        _chat.Unbind();
        _inventory.Unbind();
        _gameSession.Unbind();

        await client.DisposeAsync().ConfigureAwait(false);

        // Dispose the auth HTTP resources after the session is torn down: the signing provider used the flow for certificate refresh during the session.
        sessionService?.Dispose();
        authFlow?.Dispose();
    }

    /// <summary>
    /// Builds this attempt's <see cref="ConnectPlan"/> from the configured address and client settings and offers it to every handler.
    /// A throwing handler is logged and skipped, because a plugin cannot be allowed to make the client unable to connect by accident; a handler that means to stop the attempt says so with <see cref="ConnectPlan.Veto"/>.
    /// </summary>
    private ConnectPlan RaiseBeforeConnect(SessionAttempt attempt)
    {
        var plan = new ConnectPlan(
            attempt.Endpoint, ClientInformationFor(_configuration), attempt.Attempt, attempt.PreviousDisconnect);

        foreach (Delegate handler in BeforeConnect?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ConnectPlan>)handler)(this, plan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A BeforeConnect handler threw; the attempt continues without it.");
            }
        }

        return plan;
    }

    /// <summary>The client-settings announce this configuration produces, or UMPK's defaults without one.</summary>
    private static ClientInformationOptions ClientInformationFor(DmcbkConfiguration? configuration)
        => configuration is null
            ? new ClientInformationOptions()
            : configuration.ClientSettings.ToClientInformation(
                Localization.UiCulture.Resolve(configuration.Localization));

    /// <summary>
    /// Resolves the version to speak for this attempt: an explicit pin wins; otherwise the last negotiated version (an automatic retry, or a plain reconnect, both keep speaking the same protocol); otherwise a status ping through UMPK's own negotiator; otherwise the host's version prompt; otherwise a typed failure the supervisor lets through untouched.
    /// </summary>
    private async Task<JavaVersion> ResolveVersionAsync(
        ServerEndpoint endpoint, IServerAddressResolver resolver, JavaVersion? pinned, CancellationToken ct)
    {
        if (pinned is not null)
        {
            // the pinned path never pings for version resolution, so the connect-time status panel would otherwise never have anything to show for an explicitly-versioned connect.
            // Legacy pings on every connect purely for the display, so this OPTIONALLY fires the same ping in the background when a host has asked for it (ClientBuilder.UsePingForDisplayWhenPinned): "pinned means no ping" is an existing, relied-upon invariant elsewhere (a caller that swaps in a non-standard IConnectionFactory that does not support two independent concurrent connections, e.g. a single-pipe test double, would otherwise have this extra ping corrupt its one real connection), so this defaults OFF and only the CLI hosts opt in.
            // When it does run, it must never gate or slow down the real connect, hence fire-and-forget with its own short timeout rather than being awaited here.
            if (_pingForDisplayWhenPinned)
                _ = PingForDisplayAsync(endpoint, resolver, pinned);

            return pinned;
        }

        JavaVersion? negotiated;
        lock (_gate)
            negotiated = NegotiatedVersion;

        if (negotiated is not null)
            return negotiated;

        // Ping with the SAME resolver ConnectAsync will use for the real connect.
        // Route the ping through the proxy when the proxy is active for login.
        IConnectionFactory? pingFactory = _proxyForPing ? _proxyFactory : null;
        var options = new JavaStatusOptions
        {
            Timeout = PingTimeout,
            Logger = _loggerFactory.CreateLogger("DMCBK.Core.VersionNegotiator"),
        };

        VersionNegotiation negotiation = await _negotiator
            .DetectAsync(endpoint, options, ct, resolver, pingFactory)
            .ConfigureAwait(false);

        if (negotiation.Status is { } status)
            RaiseStatusReceived(endpoint, status, negotiation.Version);

        if (negotiation.Succeeded)
            return negotiation.Version!;

        var request = new VersionResolutionRequest(
            endpoint.Host, endpoint.Port, negotiation.Status?.Protocol, DescribeFailure(negotiation));

        IUserPrompt? prompt = _host.Prompt;
        if (prompt is not null)
        {
            JavaVersion? chosen = await prompt.ResolveVersionAsync(request, ct).ConfigureAwait(false);
            if (chosen is not null)
                return chosen;
        }

        throw new VersionResolutionException(
            $"Could not determine the server version for {request.Host}:{request.Port} ({request.Reason}).",
            endpoint.Host, endpoint.Port, request.DetectedProtocol, negotiation.Failure);
    }

    /// <summary>
    /// A display-only status ping for a pinned version.
    /// Never observed by version resolution: any failure (timeout, refused, malformed response) is swallowed, because the only consequence is that no status panel is shown for this connect.
    /// </summary>
    private async Task PingForDisplayAsync(ServerEndpoint endpoint, IServerAddressResolver resolver, JavaVersion pinned)
    {
        try
        {
            using var cts = new CancellationTokenSource(DisplayPingTimeout);
            IConnectionFactory? pingFactory = _proxyForPing ? _proxyFactory : null;
            var options = new JavaStatusOptions
            {
                Timeout = DisplayPingTimeout,
                Logger = _loggerFactory.CreateLogger("DMCBK.Core.VersionNegotiator"),
            };

            VersionNegotiation negotiation = await _negotiator
                .DetectAsync(endpoint, options, cts.Token, resolver, pingFactory)
                .ConfigureAwait(false);

            if (negotiation.Status is { } status)
                RaiseStatusReceived(endpoint, status, pinned);
        }
        catch (OperationCanceledException)
        {
            // Timed out (or the connect that started it is long gone); the panel just does not show.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Display-only status ping failed for a pinned version.");
        }
    }

    private void RaiseStatusReceived(ServerEndpoint endpoint, ServerStatus status, JavaVersion? resolvedVersion)
        => StatusReceived?.Invoke(this, new ServerStatusReceivedEventArgs(status, endpoint.Host, endpoint.Port, resolvedVersion));

    private static string DescribeFailure(VersionNegotiation negotiation) => negotiation.Failure switch
    {
        VersionNegotiationFailure.StatusPingFailed => "status ping failed: " + (negotiation.Fault?.Message ?? "unknown error"),
        VersionNegotiationFailure.NoProtocol => "status JSON had no version.protocol",
        VersionNegotiationFailure.UnsupportedProtocol => "protocol not in the supported catalog",
        _ => "version detection failed",
    };

    private async Task<AuthOutcome> AuthenticateAsync(DmcbkAccount account, SessionAttempt attempt, CancellationToken ct)
    {
        if (!account.IsOnline)
            return new AuthOutcome(OfflineIdentity.ComputeProfile(account.User), null, null, null, null);

        // The interaction-unavailable guard already ran in CreateAsync before any of this.
        IAuthInteraction interaction = _host.AuthInteraction!;

        attempt.ReportAuthenticating();

        var options = new MinecraftAuthOptions
        {
            FlowKind = DmcbkAuthMapping.ToFlowKind(account.Kind),
            YggdrasilBaseUrl = DmcbkAuthMapping.ProviderBaseUrl(account),
            TokenStore = CreateTokenStore(),
            Logger = _loggerFactory.CreateLogger("DMCBK.Core.Auth"),
        };

        var flow = new MinecraftAuthFlow(options);
        try
        {
            JavaSession session = await ResumeOrLoginAsync(flow, account, interaction, ct).ConfigureAwait(false);
            var credentials = new ProfileCredentials(session.Profile, session.AccessToken);
            var authenticator = new YggdrasilSessionService(
                new SessionServiceOptions { BaseUrl = DmcbkAuthMapping.SessionBaseUrl(account) });

            return new AuthOutcome(session.Profile, authenticator, credentials, flow, session);
        }
        catch
        {
            flow.Dispose();
            throw;
        }
    }

    private async Task<JavaSession> ResumeOrLoginAsync(
        MinecraftAuthFlow flow, DmcbkAccount account, IAuthInteraction interaction, CancellationToken ct)
    {
        JavaSession? resumed = await flow.TryResumeAsync(account.User, ct).ConfigureAwait(false);
        if (resumed is not null)
        {
            _logger.LogInformation("Resumed a cached session for {User}.", account.User);
            return resumed;
        }

        // Pass the configured login as the cache hint.
        // The session is otherwise cached only under the resolved Minecraft profile name (the gamertag), which is not what TryResumeAsync above looks up for a Microsoft account, so every start would re-run the interactive sign-in.
        JavaSession session = await flow.LoginAsync(interaction, ct, account.User).ConfigureAwait(false);

        // Always name the profile the sign-in actually resolved to.
        // It is deliberately NOT compared against account.User: for a Microsoft account the profile name is the gamertag and the configured login is an email, so a mismatch is the normal case and refusing on it would break every Microsoft login.
        // Naming it is what makes a substitution visible.
        _logger.LogInformation("Signed in as {Profile}.", session.Profile.Name);

        // The browser flow above never configures BrowserRedirectUri, so it always takes the hosted redirect page: the authorization code is carried from that page to this process by hand, and nothing stops a user pasting a code issued to someone else.
        // That session would then be cached under the configured login and resumed unprompted on every later start, so the substitution has to be called out once, here.
        if (account.Kind == DmcbkAccountKind.MicrosoftBrowser)
        {
            _logger.LogWarning(
                "This sign-in returns its code through the hosted redirect page, so the code is "
                + "carried back by hand and nothing ties it to this session. If '{Profile}' is not "
                + "your account, do not continue: clear the saved session and sign in again.",
                session.Profile.Name);
        }

        return session;
    }

    private ITokenStore CreateTokenStore()
    {
        if (_tokenStorePath is null)
            return new InMemoryTokenStore();

        ILogger logger = _loggerFactory.CreateLogger("DMCBK.Core.Auth.TokenStore");
        return new FileTokenStore(_tokenStorePath, TokenProtectors.CreateDefault(logger), logger);
    }

    /// <summary>The resolved authentication artifacts consumed by the UMPK client builder.</summary>
    private sealed record AuthOutcome(
        GameProfile Profile,
        ISessionAuthenticator? Authenticator,
        ProfileCredentials? Credentials,
        MinecraftAuthFlow? Flow,
        JavaSession? Session);
}

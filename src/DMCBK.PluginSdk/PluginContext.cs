using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DMCBK.PluginSdk;

/// <summary>
/// The services handed to a plugin at <see cref="IPlugin.ActivateAsync"/>.
/// It spans two lifetimes: the cross-session services (settings, storage, logger, translations, chat classifier, cron, messenger, variables, and the full <see cref="Client"/>) live for the whole client run, while <see cref="Session"/> and the <see cref="SessionCreated"/> / <see cref="SessionStarted"/> / <see cref="SessionEnded"/> events expose the per-session (reconnect-aware) UMPK surface.
/// Subscribing to the session events in <c>ActivateAsync</c> is the reconnect-safe pattern; read session state from these events instead of static access.
/// </summary>
public sealed class PluginContext
{
    private readonly object _gate = new();
    private ISessionScope? _session;

    internal PluginContext(
        Client client,
        PluginSettings settings,
        IPluginStorage storage,
        ILogger logger,
        Umpk.Text.ITranslationSource translations,
        IChatClassifier chat,
        Umpk.Hosting.ICronScheduler cron,
        IPluginMessenger messenger,
        IPluginServices services,
        VariableStore variables)
    {
        Client = client;
        Host = HostInfo.FromClient(client);
        Commands = new PluginCommandScope(client.Commands);
        Settings = settings;
        Storage = storage;
        Logger = logger;
        Translations = translations;
        Chat = chat;
        Cron = new GuardedCronScheduler(cron, ex => Fault(PluginFaultSurface.Scheduler, null, ex));
        Messenger = messenger;
        Services = services;
        Variables = variables;
    }

    /// <summary>
    /// Where an exception thrown by this plugin is counted.
    /// Assigned by the host; null in a context nobody is watching, which is what a hand-built context in a test gets.
    /// </summary>
    internal Action<PluginFaultSurface, string?, Exception>? Faults { get; set; }

    /// <summary>
    /// Raised when a new session's client exists but has not dialled yet.
    /// This is the only moment early enough to capture the handshake, login and configuration frames, to claim a login-phase channel, or to be sure of the first message on a play channel; SessionStarted fires after play begins.
    /// The client is unconnected, so what belongs here is subscription (ObservePackets, Events, StatusChanged) and the registrations on <see cref="SessionCreatedEventArgs.Session"/>, not game calls.
    /// </summary>
    public event EventHandler<SessionCreatedEventArgs>? SessionCreated;

    /// <summary>Raised when a session attaches (initial connect and each reconnect). Carries the live scope.</summary>
    public event EventHandler<SessionScopeEventArgs>? SessionStarted;

    /// <summary>Raised when the live session ends (before the next reconnect attaches a fresh scope).</summary>
    public event EventHandler<EventArgs>? SessionEnded;

    /// <summary>
    /// Raised before each connect attempt, automatic reconnects included, with the plan for that attempt: the address, the client-settings announce, and a veto.
    /// Nothing has touched the network yet, so a redirect moves the version ping and the login as well as the socket.
    /// See <see cref="ConnectPlan"/>.
    /// </summary>
    public event EventHandler<ConnectPlan>? BeforeConnect;

    /// <summary>
    /// Raised after <c>reload</c> has applied a fresh configuration snapshot, for a plugin that caches something derived from configuration.
    /// The snapshot is on the event: <see cref="Client.Configuration"/> is the one the client was built from and does not change.
    /// </summary>
    public event EventHandler<ConfigurationReloadedEventArgs>? ConfigurationReloaded;

    /// <summary>
    /// Raised once when the client is shutting down, while the session is still live and before <see cref="IPlugin.DeactivateAsync"/>.
    /// This is where a bridge posts "going offline" and a logger writes its last line.
    /// Work that cannot finish inline goes to <see cref="BeforeExitEventArgs.Defer"/> and is waited for up to the shutdown deadline.
    /// </summary>
    public event EventHandler<BeforeExitEventArgs>? BeforeExit;

    /// <summary>
    /// What the running client is: the SDK api version, the MCC version and the UMPK engine version.
    /// Ask this instead of inferring the client from behaviour when a plugin has to work on more than one.
    /// </summary>
    public HostInfo Host { get; }

    /// <summary>The full core client API (advanced plugins). The high-level game facade is <see cref="Game"/>.</summary>
    public Client Client { get; }

    /// <summary>The high-level live-session game facade (chat, world, entities, inventory, movement, player).</summary>
    public GameApi Game => Client.Game;

    /// <summary>
    /// The plugin's internal-command registration scope for the whole time it is loaded: registered commands dispatch real console input and survive reconnects, and the host unregisters them on unload, disable and reload.
    /// Prefer this over <see cref="ICommandDispatcher.RegisterHostCommand"/>, which never unregisters and therefore keeps the plugin (and its collectible load context) alive after unload.
    /// For a command that only makes sense while connected, use <see cref="ISessionScope.Commands"/>.
    /// </summary>
    public IPluginCommandScope Commands { get; }

    /// <summary>The current live session scope, or throws when no session is attached (guard with <see cref="InSession"/>).</summary>
    public ISessionScope Session
        => CurrentSession ?? throw new InvalidOperationException(
            "No session is attached. Use SessionStarted/SessionEnded or check InSession before touching Session.");

    /// <summary>The current live session scope, or null when no session is attached.</summary>
    public ISessionScope? CurrentSession
    {
        get
        {
            lock (_gate)
                return _session;
        }
    }

    /// <summary>True when a live session is attached.</summary>
    public bool InSession => CurrentSession is not null;

    /// <summary>Typed load/save/validate of this plugin's <c>settings.toml</c>.</summary>
    public PluginSettings Settings { get; }

    /// <summary>The plugin's <c>data/</c> persistence sandbox (paths + a simple key/value store).</summary>
    public IPluginStorage Storage { get; }

    /// <summary>A per-plugin logger routed to core logging.</summary>
    public ILogger Logger { get; }

    /// <summary>The translation source (vanilla en_us plus host locale overrides).</summary>
    public Umpk.Text.ITranslationSource Translations { get; }

    /// <summary>
    /// This plugin's OWN translated strings, read from its <c>lang/&lt;language&gt;.toml</c> files.
    /// <para>
    /// A different axis from <see cref="Translations"/>, which resolves MINECRAFT's translation keys so chat the server sent can be rendered.
    /// Use this one for anything the plugin itself says, so a plugin can be translated the way MCC is.
    /// Never null: a plugin with no <c>lang/</c> folder gets a table that returns each key unchanged, which is what MCC's own corpus does for a missing key.
    /// </para>
    /// </summary>
    public IPluginLocalization Strings { get; internal set; } = EmptyPluginLocalization.Instance;

    /// <summary>The structured chat classifier (kind/sender/body).</summary>
    public IChatClassifier Chat { get; }

    /// <summary>
    /// Wall-clock (cron-style) scheduling, independent of the game tick.
    /// Sourced from UMPK's own per-extension <see cref="Umpk.Client.ClientExtensionContext.Cron"/>: it spans every reconnect and keeps running with no session at all, and the host disposes it automatically when the plugin unloads.
    /// </summary>
    public Umpk.Hosting.ICronScheduler Cron { get; }

    /// <summary>Inter-plugin publish/subscribe and typed request/response.</summary>
    public IPluginMessenger Messenger { get; }

    /// <summary>
    /// Inter-plugin direct calls: register one instance per contract type, look another plugin's up.
    /// The shape <see cref="Messenger"/> fits badly.
    /// Pairs with <c>[requires]</c> and <c>[exports]</c>, which are what make a contract type declared in one plugin usable in another.
    /// Registrations are dropped when the plugin unloads.
    /// </summary>
    public IPluginServices Services { get; }

    /// <summary>The shared <c>%variable%</c> store (also used by set and input expansion).</summary>
    public VariableStore Variables { get; }

    /// <summary>
    /// The Beacon scripting bridge for this plugin: offer extension functions scripts call as <c>extern</c>, declare custom events scripts consume with <c>on</c>, and await exported script functions.
    /// Assigned at activation; registrations queue until the client Beacon engine exists (it starts with the first <c>/scripts</c> run) and flush on first use, and the host withdraws them on unload, disable, and reload.
    /// </summary>
    public IBeaconHost Beacon { get; internal set; } = NullBeaconHost.Instance;

    /// <summary>
    /// Extension-side hook (called by <c>McPluginExtension</c> off UMPK's own <see cref="Umpk.Client.ClientExtensionContext.SessionCreated"/>): raises <see cref="SessionCreated"/> with the freshly built, still-unconnected client for the session about to start.
    /// Does not touch <see cref="CurrentSession"/>; that stays whatever it already was (typically null) until <see cref="AttachSession"/> runs once play begins.
    /// Not raised for a plugin that loads mid-session (UMPK does not replay it; <see cref="SessionStarted"/> still fires for the already-live session).
    /// </summary>
    internal void RaiseSessionCreated(Umpk.Client.UmpkClient client, IPreSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scope);
        Guarded(nameof(SessionCreated), () => SessionCreated?.Invoke(this, new SessionCreatedEventArgs(client, scope)));
    }

    /// <summary>Extension-side hook: offers this plugin the plan for the attempt about to be made.</summary>
    internal void RaiseBeforeConnect(ConnectPlan plan)
        => Guarded(nameof(BeforeConnect), () => BeforeConnect?.Invoke(this, plan));

    /// <summary>Extension-side hook: hands this plugin the configuration snapshot <c>reload</c> just applied.</summary>
    internal void RaiseConfigurationReloaded(ConfigurationReloadedEventArgs e)
        => Guarded(nameof(ConfigurationReloaded), () => ConfigurationReloaded?.Invoke(this, e));

    /// <summary>Extension-side hook: tells this plugin the client is going down while the session is still live.</summary>
    internal void RaiseBeforeExit(BeforeExitEventArgs e)
        => Guarded(nameof(BeforeExit), () => BeforeExit?.Invoke(this, e));

    /// <summary>
    /// Runs one event fan-out and reports an exception instead of letting it out.
    /// A plugin's handler runs on the client's own thread, so an exception that escaped would be the client's problem rather than the plugin's, which is the opposite of the arrangement.
    /// </summary>
    private void Guarded(string eventName, Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fault(PluginFaultSurface.Event, eventName, ex);
        }
    }

    private void Fault(PluginFaultSurface surface, string? detail, Exception ex)
    {
        if (Faults is { } sink)
        {
            sink(surface, detail, ex);
            return;
        }

        Logger.LogWarning(ex, "A plugin handler threw from {Surface}.", detail ?? surface.ToString());
    }

    /// <summary>
    /// Extension-side hook (called by <c>McPluginExtension</c> off UMPK's own <see cref="Umpk.Client.ClientExtensionContext.SessionStarted"/>): stores the live scope and raises <see cref="SessionStarted"/>.
    /// The scope handed in is already fully formed for this plugin (its own <see cref="ISessionScope.Commands"/> included, see <see cref="SessionScope"/>); this method does no wrapping of its own.
    /// </summary>
    internal void AttachSession(ISessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            if (ReferenceEquals(_session, scope))
                return; // Already attached to this scope (guards a load/attach race).

            _session = scope;
        }

        Guarded(nameof(SessionStarted), () => SessionStarted?.Invoke(this, new SessionScopeEventArgs(scope)));
    }

    /// <summary>
    /// Extension-side hook (called by <c>McPluginExtension</c> off UMPK's own <see cref="Umpk.Client.ClientExtensionContext.SessionEnded"/>): clears the current scope and raises <see cref="SessionEnded"/> so the plugin can release what it holds.
    /// Runs on session detach AND on plugin unload (the latter via <see cref="Umpk.Client.ClientExtensionCollection.RemoveAsync"/> ending the live session before deactivating).
    /// The scope's own session-scoped registrations (its <see cref="ISessionScope.Commands"/> above all) are released by the scope itself, off the same underlying <see cref="Umpk.Client.Plugins.ClientPluginContext.Detached"/> token that got it here; this method does not need to close anything on its behalf.
    /// </summary>
    internal void DetachSession()
    {
        ISessionScope? ended;
        lock (_gate)
        {
            ended = _session;
            _session = null;
        }

        if (ended is null)
            return;

        Guarded(nameof(SessionEnded), () => SessionEnded?.Invoke(this, EventArgs.Empty));
    }
}

/// <summary>Carries the live session scope for <see cref="PluginContext.SessionStarted"/>.</summary>
public sealed class SessionScopeEventArgs : EventArgs
{
    internal SessionScopeEventArgs(ISessionScope session) => Session = session;

    /// <summary>The session scope that just attached.</summary>
    public ISessionScope Session { get; }
}

/// <summary>
/// Carries the freshly built, still-unconnected client for <see cref="PluginContext.SessionCreated"/>, and the <see cref="IPreSessionScope"/> that is the supported way to use it.
/// This fires before the client dials, so a subscription taken here (<see cref="Umpk.Client.UmpkClient.ObservePackets"/> above all) is early enough to see the handshake, login and configuration frames that <see cref="PluginContext.SessionStarted"/> is already too late for.
/// The client grants no power <see cref="ISessionScope.Client"/> does not already grant once play begins; it is simply not yet safe to drive.
/// </summary>
public sealed class SessionCreatedEventArgs : EventArgs
{
    internal SessionCreatedEventArgs(Umpk.Client.UmpkClient client, IPreSessionScope session)
    {
        Client = client;
        Session = session;
    }

    /// <summary>The unconnected client this session is about to dial.</summary>
    public Umpk.Client.UmpkClient Client { get; }

    /// <summary>
    /// What can be set up before the dial: play and configuration channels, a login-query responder, the cookie store.
    /// See <see cref="IPreSessionScope"/>.
    /// </summary>
    public IPreSessionScope Session { get; }
}

using DMCBK.Core;
using DMCBK.Core.Commands;
using Microsoft.Extensions.Logging;
using Umpk.Client;

namespace DMCBK.PluginSdk;

/// <summary>
/// The UMPK <see cref="IClientExtension"/> for exactly one loaded MCC plugin.
/// <see cref="PluginHost"/> registers one of these through <see cref="Client.Extensions"/> for every plugin it loads: <see cref="ActivateAsync"/> builds the plugin's <see cref="PluginContext"/> (its <c>Cron</c> now sourced from UMPK's own per-extension <see cref="ClientExtensionContext.Cron"/>, which spans reconnects and keeps running with no session at all) and calls the plugin's own <see cref="IPlugin.ActivateAsync"/>; <see cref="DeactivateAsync"/> runs the plugin's own <see cref="IPlugin.DeactivateAsync"/> under a bounded-wait-then-abandon policy, so a wedged plugin cannot hold <c>unload</c>/<c>reload</c> hostage.
/// <para>
/// Session create/attach/detach is forwarded, not re-implemented: this subscribes to
/// <see cref="ClientExtensionContext.SessionCreated"/>/<see cref="ClientExtensionContext.SessionStarted"/>/
/// <see cref="ClientExtensionContext.SessionEnded"/> and drives <see cref="PluginContext"/>'s own <c>RaiseSessionCreated</c>/<c>AttachSession</c>/<c>DetachSession</c> hooks, which is what raises the plugin-facing <see cref="PluginContext.SessionCreated"/>/<see cref="PluginContext.SessionStarted"/>/ <see cref="PluginContext.SessionEnded"/> events.
/// Because one extension exists per plugin, an unloaded plugin stops reacting at once: UMPK simply stops calling this extension at all.
/// </para>
/// </summary>
internal sealed class McPluginExtension : IClientExtension
{
    private readonly Client _client;
    private readonly IPlugin _instance;
    private readonly PluginSettings _settings;
    private readonly IPluginStorage _storage;
    private readonly ILogger _pluginLogger;
    private readonly ILogger _hostLogger;
    private readonly Umpk.Text.ITranslationSource _translations;
    private readonly IChatClassifier _chat;
    private readonly IPluginMessenger _messenger;
    private readonly IPluginServices _services;
    private readonly IPluginLocalization _localization;
    private readonly VariableStore _variables;
    private readonly TimeSpan _teardownTimeout;

    private PluginContext? _context;
    private EventHandler<DMCBK.Core.ConnectPlan>? _onBeforeConnect;
    private EventHandler<DMCBK.Core.Configuration.ConfigurationReloadedEventArgs>? _onConfigurationReloaded;
    private EventHandler<DMCBK.Core.BeforeExitEventArgs>? _onBeforeExit;

    internal McPluginExtension(
        string id,
        Client client,
        IPlugin instance,
        PluginSettings settings,
        IPluginStorage storage,
        ILogger pluginLogger,
        ILogger hostLogger,
        Umpk.Text.ITranslationSource translations,
        IChatClassifier chat,
        IPluginMessenger messenger,
        IPluginServices services,
        IPluginLocalization localization,
        VariableStore variables,
        TimeSpan teardownTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(pluginLogger);
        ArgumentNullException.ThrowIfNull(hostLogger);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(variables);

        Id = id;
        _client = client;
        _instance = instance;
        _settings = settings;
        _storage = storage;
        _pluginLogger = pluginLogger;
        _hostLogger = hostLogger;
        _translations = translations;
        _chat = chat;
        _messenger = messenger;
        _services = services;
        _localization = localization;
        _variables = variables;
        _teardownTimeout = teardownTimeout;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <summary>The plugin's context, valid once <see cref="ActivateAsync"/> has returned.</summary>
    internal PluginContext Context => _context
        ?? throw new InvalidOperationException($"Plugin '{Id}' has not activated yet.");

    /// <inheritdoc/>
    public async ValueTask ActivateAsync(ClientExtensionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pluginContext = new PluginContext(
            _client, _settings, _storage, _pluginLogger, _translations, _chat, context.Cron, _messenger,
            _services, _variables)
        {
            // Set before ActivateAsync, not by the host after this returns.
            // ActivateAsync is where a plugin reads its strings: the first line it logs, the CmdDesc of every command it registers.
            // Assigned afterwards, all of those resolve through the empty table and come out as their own key names.
            Strings = _localization,
            Beacon = new PluginBeaconHost(Id, _client, _pluginLogger),
        };

        context.SessionCreated += (_, e) => pluginContext.RaiseSessionCreated(
            e.Client, new PreSessionScope(e.Client, context.Deactivated));
        context.SessionStarted += (_, e) => pluginContext.AttachSession(new SessionScope(e.Session, _client.Commands));
        context.SessionEnded += (_, _) => pluginContext.DetachSession();

        // The client's own hooks, bridged for as long as this plugin is loaded.
        // They are unsubscribed in DeactivateAsync: the client outlives the plugin, and a subscription it still held would call into an unloaded assembly and keep its collectible load context alive.
        _onBeforeConnect = (_, plan) => pluginContext.RaiseBeforeConnect(plan);
        _onConfigurationReloaded = (_, e) => pluginContext.RaiseConfigurationReloaded(e);
        _onBeforeExit = (_, e) => pluginContext.RaiseBeforeExit(e);
        _client.BeforeConnect += _onBeforeConnect;
        _client.ConfigurationReloaded += _onConfigurationReloaded;
        _client.BeforeExit += _onBeforeExit;

        _context = pluginContext;
        await _instance.ActivateAsync(pluginContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the plugin's teardown with a bounded wait.
    /// On timeout the token is cancelled and the wait is abandoned rather than extended: the abandoned task's eventual fault is observed so it cannot resurface as an unobserved-exception crash at a later GC.
    /// Mirrors UMPK's own <c>ClientExtensionCollection.DeactivateWithTimeoutAsync</c>, kept here as well (rather than relying on that one alone) so the exact diagnostic wording plugin authors and tests already depend on (<see cref="PluginStrings.TeardownTimedOut"/>) is unaffected by the move to UMPK's extension host.
    /// </summary>
    public async ValueTask DeactivateAsync(CancellationToken ct)
    {
        if (_onBeforeConnect is not null)
        {
            _client.BeforeConnect -= _onBeforeConnect;
            _client.ConfigurationReloaded -= _onConfigurationReloaded;
            _client.BeforeExit -= _onBeforeExit;
            _onBeforeConnect = null;
            _onConfigurationReloaded = null;
            _onBeforeExit = null;
        }

        var cts = new CancellationTokenSource();
        bool abandoned = false;
        Task teardown = Task.CompletedTask;
        try
        {
            teardown = _instance.DeactivateAsync(cts.Token);
            await teardown.WaitAsync(_teardownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            abandoned = true;
            string message = PluginStrings.TeardownTimedOut(Id, (int)_teardownTimeout.TotalSeconds);
            _hostLogger.LogWarning("{Message}", message);

            // Nobody will await the abandoned task, and cancelling it below usually faults it.
            // Observe the fault so it does not resurface as an unobserved-exception crash at the next GC.
            _ = teardown.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _hostLogger.LogDebug(ex, "Cancelling the teardown of plugin '{Id}' threw.", Id);
            }
        }
        catch (Exception ex)
        {
            _hostLogger.LogWarning(ex, "{Message}", PluginStrings.TeardownFailed(Id, ex.Message));
        }
        finally
        {
            // On the abandoned path the plugin still holds the token, and disposing the source would make any later registration on it throw.
            // Let it be collected with the plugin instead.
            if (!abandoned)
                cts.Dispose();
        }
    }
}

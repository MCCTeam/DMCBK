namespace DMCBK.PluginSdk;

/// <summary>
/// The plugin author contract.
/// A plugin assembly (compiled <c>.dll</c>) or single-file <c>.cs</c> entry implements this.
/// The host discovers the implementing type, instantiates it once, then drives the lifecycle:
/// <list type="number">
/// <item><see cref="Configure"/> declares identity/version/settings with NO side effects.</item>
/// <item>The host runs the settings pipeline (load <c>settings.toml</c>, validate, write defaults).</item>
/// <item>
/// <see cref="ActivateAsync"/> runs ONCE per client start (not per reconnect).
/// Subscribe to <see cref="PluginContext.SessionStarted"/> / <see cref="PluginContext.SessionEnded"/> for the per-session (reconnect-aware) work: acquiring the movement lease, per-session event subscriptions, command scope, and scheduler registrations all live on the session scope and are torn down automatically at session end.
/// </item>
/// <item>
/// <see cref="DeactivateAsync"/> runs ONCE when the plugin is unloaded, disabled or reloaded, and is the mirror of <see cref="ActivateAsync"/>: close whatever the plugin itself opened.
/// It has a default no-op implementation, so a plugin that owns nothing does not have to write it.
/// </item>
/// </list>
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Declares the plugin's identity and its optional declared settings type.
    /// Called once, before activation, with no live session and no host services wired yet.
    /// Must be side-effect free (no I/O, no game calls).
    /// </summary>
    void Configure(PluginDescriptor descriptor);

    /// <summary>
    /// Runs once when the client starts, after the settings pipeline has run.
    /// Use it to read settings, wire cross-session services (cron, messenger, storage), and subscribe to <see cref="PluginContext.SessionStarted"/> / <see cref="PluginContext.SessionEnded"/>.
    /// Do not assume a live session here; the first session may not have attached yet.
    /// </summary>
    Task ActivateAsync(PluginContext context);

    /// <summary>
    /// Releases everything the plugin itself opened: network clients, sockets, background loops, timers, file handles.
    /// Runs once, on unload/disable/reload, AFTER the last <see cref="PluginContext.SessionEnded"/> and BEFORE the plugin's collectible load context is unloaded, so the plugin's own code is still loaded while it runs.
    /// <para>
    /// The host does NOT need this for what it owns: session event subscriptions, session command registrations, scheduler registrations, movement leases and plugin channels are torn down at session end, and cron registrations, messenger handles and the storage flush are done by the host on unload.
    /// Implement this only for resources the plugin created itself.
    /// </para>
    /// <para>
    /// The host waits a bounded time (see <c>PluginHost.TeardownTimeout</c>).
    /// A plugin that has not returned by then has <paramref name="ct"/> cancelled, the host logs a warning naming the plugin, and the unload proceeds regardless: teardown can never wedge <c>unload</c> or <c>reload</c>.
    /// Honor <paramref name="ct"/> and do not block.
    /// </para>
    /// <para>
    /// This is a default interface member so that adding it did not break the plugins that shipped before it existed.
    /// Declaring a matching public method on the plugin class overrides the no-op default.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancelled when the host's bounded teardown wait elapses.</param>
    Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;
}

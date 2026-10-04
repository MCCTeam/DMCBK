using DMCBK.Core.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Client;

namespace DMCBK.Marketplace;

/// <summary>Host-supplied timing and connectivity for unattended update checks.</summary>
public sealed record AutomaticUpdateOptions
{
    /// <summary>The clock used for the once-per-day metadata check.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Reports that this host must avoid network access.</summary>
    public Func<bool> Offline { get; init; } = () => false;
    /// <summary>Optional logger when the host offers no notification presentation.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
    /// <summary>The cancellable startup delay.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    /// <summary>Selects the startup delay in seconds, with an exclusive upper bound.</summary>
    public Func<int, int, int> RandomSeconds { get; init; } = Random.Shared.Next;
}

public sealed partial class MarketplaceService
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _automaticGate = new(1, 1);
    private readonly HashSet<(string Id, string Publisher)> _pendingUpdates = [];
    private Task _deferredUpdates = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Host timing, connectivity and diagnostics for automatic checks.</summary>
    public AutomaticUpdateOptions AutomaticUpdates { get; set; } = new();

    /// <inheritdoc/>
    public async Task StartAutoUpdateAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        CancellationToken token = linked.Token;
        if (AutomaticUpdates.Offline()) return;
        MarketplaceBinding[] bindings = (await RegistryAsync(token).ConfigureAwait(false)).Marketplaces
            .Where(binding => binding.AutoUpdate != "off" && !IsLocal(binding.Source)).ToArray();
        if (bindings.Length == 0) return;
        await AutomaticUpdates.Delay(TimeSpan.FromSeconds(AutomaticUpdates.RandomSeconds(5, 61)), token).ConfigureAwait(false);
        await _automaticGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (MarketplaceBinding binding in bindings)
            {
                if (AutomaticUpdates.Offline()) return;
                MarketplaceSnapshot? cached = await _catalogues.ReadCachedAsync(binding, token).ConfigureAwait(false);
                if (cached is not null && AutomaticUpdates.Time.GetUtcNow() - cached.FetchedAt < TimeSpan.FromHours(24)) continue;
                await _catalogues.RefreshAsync(binding, token).ConfigureAwait(false);
                InstallationLock selection = await EmptyInstaller().ReadLockAsync(token).ConfigureAwait(false);
                foreach (LockedPlugin plugin in selection.Plugins.Where(plugin => plugin.MarketplaceId == binding.Id
                             && !plugin.Pinned && plugin.UpdatePolicy != "off"))
                {
                    try
                    {
                        InstallPlan plan = await PlanUpdateAsync(Request(plugin.Id, binding.Id, null), token).ConfigureAwait(false);
                        if (plan.Changes.Count == 0) continue;
                        bool apply = plugin.UpdatePolicy == "automatic"
                            || (plugin.UpdatePolicy == "inherit" && binding.AutoUpdate == "apply");
                        if (!apply) { Notify(Text("operation.update-available", plugin.Id)); continue; }
                        if (SessionActive())
                        {
                            if (_pendingUpdates.Add((plugin.Id, binding.Id))) Notify(Text("operation.update-deferred", plugin.Id));
                        }
                        else
                        {
                            await ApplyAsync(plan, token).ConfigureAwait(false);
                            Notify(Text("operation.installed", plugin.Id, plan.Changes.First(change => change.Id == plugin.Id).Version));
                        }
                    }
                    catch (MarketplaceException exception) { AutomaticUpdates.Logger.LogDebug(exception, "{Message}", Text("operation.failed", exception.Code)); }
                }
            }
        }
        finally { _automaticGate.Release(); }
    }

    private bool SessionActive() => _client.Status is ClientStatus.Connecting or ClientStatus.Playing or ClientStatus.Reconnecting;
    private static bool IsLocal(string source) => Directory.Exists(source) || File.Exists(source)
        || (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.IsFile);
    private void Notify(string message)
    {
        if (_client.HostUi?.TryNotify(new HostNotification(message)) != true)
            AutomaticUpdates.Logger.LogInformation("{Message}", message);
    }

    private void OnAutomaticStatusChanged(object? sender, ClientStatusChangedEventArgs args)
    {
        if (args.Current != ClientStatus.Disconnected || _disposed) return;
        lock (_pendingUpdates) _deferredUpdates = DrainPendingUpdatesAsync(_deferredUpdates);
    }

    private async Task DrainPendingUpdatesAsync(Task predecessor)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
            await _automaticGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (SessionActive() || AutomaticUpdates.Offline()) return;
                foreach ((string id, string publisher) in _pendingUpdates.ToArray())
                {
                    _pendingUpdates.Remove((id, publisher));
                    InstallationLock selection = await EmptyInstaller().ReadLockAsync(_shutdown.Token).ConfigureAwait(false);
                    LockedPlugin? plugin = selection.Plugins.Find(item => item.Id == id && item.MarketplaceId == publisher);
                    if (plugin is null || plugin.Pinned || plugin.UpdatePolicy == "off") continue;
                    MarketplaceBinding? binding = (await RegistryAsync(_shutdown.Token).ConfigureAwait(false)).Marketplaces.Find(item => item.Id == publisher);
                    if (plugin.UpdatePolicy != "automatic" && (plugin.UpdatePolicy != "inherit" || binding?.AutoUpdate != "apply")) continue;
                    InstallPlan plan = await PlanUpdateAsync(Request(id, publisher, null), _shutdown.Token).ConfigureAwait(false);
                    if (plan.Changes.Count > 0) await ApplyAsync(plan, _shutdown.Token).ConfigureAwait(false);
                }
            }
            finally { _automaticGate.Release(); }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) { AutomaticUpdates.Logger.LogWarning(exception, "{Message}", Text("operation.failed", exception.Message)); }
    }

    /// <summary>Cancels and awaits deferred updates before releasing an owned transport.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _deferredUpdates.ConfigureAwait(false);
    }
}

using Umpk.Client;
using Umpk.Client.Events;

namespace DMCBK.Core;

/// <summary>
/// The shared live-session handle the <see cref="GameApi"/> facades read and act through.
/// It owns the bound <see cref="UmpkClient"/> behind a gate (so binding on connect and clearing on disconnect are safe from any thread) and centralizes the two cross-cutting rules the facades must honor:
/// <list type="bullet">
/// <item>
/// Loop affinity: UMPK mutates tracked state on its session loop and documents off-loop reads as eventually-consistent.
/// Snapshot reads that enumerate mutable collections are marshaled onto the loop via <see cref="UmpkClient.InvokeAsync{T}"/> so a host never enumerates a dictionary mid-mutation.
/// </item>
/// <item>
/// Typed exceptions: UMPK's <see cref="FeatureDisabledException"/> is wrapped into the core <see cref="MccFeatureDisabledException"/>, and a missing session surfaces as <see cref="MccNotInSessionException"/>.
/// </item>
/// </list>
/// No statics; one instance per <see cref="Client"/>.
/// </summary>
internal sealed class GameSession
{
    private readonly object _gate = new();
    private UmpkClient? _client;
    private TaskCompletionSource<bool>? _spawned;
    private IDisposable? _spawnSubscription;

    internal void Bind(UmpkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Armed HERE, before the connect, because that is the only race-free point: UmpkClient.ConnectAsync returns as soon as the LOGIN phase finishes and only then starts the receive loop, so the join packet and the initial position teleport are still in flight when the host regains control.
        // A consumer that subscribes after StartAsync can miss the teleport entirely and then wait forever.
        ArmSpawn();
        IDisposable subscription = client.Events.Subscribe<PositionCorrected>(_ => MarkSpawned());

        IDisposable? previous;
        lock (_gate)
        {
            previous = _spawnSubscription;
            _client = client;
            _spawnSubscription = subscription;
        }

        previous?.Dispose();
    }

    /// <summary>
    /// Starts a fresh "has the server placed us yet" signal for a session that is about to connect, resolving any previous one to false.
    /// Separate from <see cref="Bind"/> only so the ordering it guarantees can be exercised without a live UMPK client.
    /// </summary>
    internal void ArmSpawn()
    {
        var spawned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool>? previous;
        lock (_gate)
        {
            previous = _spawned;
            _spawned = spawned;
        }

        previous?.TrySetResult(false);
    }

    internal void Unbind()
    {
        IDisposable? subscription;
        TaskCompletionSource<bool>? spawned;
        lock (_gate)
        {
            subscription = _spawnSubscription;
            spawned = _spawned;
            _client = null;
            _spawnSubscription = null;
            _spawned = null;
        }

        subscription?.Dispose();

        // False, not cancelled: a waiter learns "this session ended without ever placing you" instead of hanging until its own timeout.
        spawned?.TrySetResult(false);
    }

    /// <summary>
    /// Completes with true once the server has placed the player in the world for this session (the initial position teleport), and with false when the session ends first.
    /// Null when no session is bound.
    /// See <see cref="PlayerApi.WaitForSpawnAsync"/> for why a host needs this.
    /// </summary>
    internal Task<bool>? Spawned
    {
        get
        {
            lock (_gate)
                return _spawned?.Task;
        }
    }

    /// <summary>
    /// Records that the server has placed the player.
    /// Called from the position-teleport subscription armed in <see cref="Bind"/>; idempotent, because a session receives many teleports and only the first one is the spawn.
    /// </summary>
    internal void MarkSpawned()
    {
        TaskCompletionSource<bool>? spawned;
        lock (_gate)
            spawned = _spawned;

        spawned?.TrySetResult(true);
    }

    /// <summary>The bound client, or null when not in a live session.</summary>
    internal UmpkClient? Current
    {
        get
        {
            lock (_gate)
                return _client;
        }
    }

    /// <summary>The bound client, or throws <see cref="MccNotInSessionException"/> when not in a session.</summary>
    internal UmpkClient Require()
        => Current ?? throw new MccNotInSessionException(
            "This game facade is unavailable: the client is not in a live session.");

    /// <summary>
    /// Runs a loop-affine read on the session loop and returns its result, wrapping a disabled feature into the typed core exception.
    /// Use for any read that enumerates or copies mutable tracked state.
    /// </summary>
    internal async Task<T> ReadAsync<T>(Func<UmpkClient, T> read, CancellationToken ct)
    {
        UmpkClient client = Require();
        try
        {
            return await client.InvokeAsync(read, ct).ConfigureAwait(false);
        }
        catch (FeatureDisabledException ex)
        {
            throw new MccFeatureDisabledException(ex.Feature, ex);
        }
    }

    /// <summary>Runs an action against the bound client, wrapping a disabled feature into the typed core exception.</summary>
    internal async Task RunAsync(Func<UmpkClient, Task> action)
    {
        UmpkClient client = Require();
        try
        {
            await action(client).ConfigureAwait(false);
        }
        catch (FeatureDisabledException ex)
        {
            throw new MccFeatureDisabledException(ex.Feature, ex);
        }
    }

    /// <summary>Runs a value-returning action against the bound client, wrapping a disabled feature.</summary>
    internal async Task<T> RunAsync<T>(Func<UmpkClient, Task<T>> action)
    {
        UmpkClient client = Require();
        try
        {
            return await action(client).ConfigureAwait(false);
        }
        catch (FeatureDisabledException ex)
        {
            throw new MccFeatureDisabledException(ex.Feature, ex);
        }
    }
}

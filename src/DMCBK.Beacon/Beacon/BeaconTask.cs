namespace DMCBK.Core.Beacon;

/// <summary>
/// Runtime error codes.
/// <c>B4001</c>/<c>B4002</c> are taken by the <c>mcc</c>/<c>server</c> verb failures; these extend the <c>B4xxx</c> runtime-only family for scheduler and movement failures.
/// All are catchable via <c>try/catch err</c> and surface as a map with at least <c>message</c> text, per the error-value shape.
/// </summary>
public static class BeaconTaskErrorCodes
{
    /// <summary>A movement lost the lease to a newer Beacon request, or the lease is held externally.</summary>
    public const string Superseded = "B4003";

    /// <summary>A task was cancelled (<c>cancel task</c>, <c>stop_moving</c>, reconnect, reload, session loss).</summary>
    public const string TaskCancelled = "B4004";

    /// <summary>Movement refused: a required <c>Gameplay.*</c> gate is off.</summary>
    public const string MovementGated = "B4005";

    /// <summary>A <c>wait</c> was refused: 32 sleeps are already pending for this script.</summary>
    public const string TooManySleeps = "B4006";
}

/// <summary>Where a Beacon background task stands.</summary>
public enum BeaconTaskStatus
{
    /// <summary>The body is still running (or waiting on the virtual clock).</summary>
    Running,

    /// <summary>The body returned; <see cref="BeaconTask.Result"/> holds its value.</summary>
    Completed,

    /// <summary>The body raised a catchable <see cref="BeaconRuntimeException"/>; <see cref="BeaconTask.Error"/> holds the catch map.</summary>
    Failed,

    /// <summary>The body was cancelled; <see cref="BeaconTask.Error"/> holds the cancellation map.</summary>
    Cancelled,
}

/// <summary>
/// One Beacon background task: the handle <c>start</c> returns, <c>await</c> joins, <c>tasks()</c> lists, and <c>cancel task</c> stops.
/// The <see cref="Completion"/> task never faults: outcomes land on <see cref="Status"/>/<see cref="Result"/>/<see cref="Error"/>, and <c>await</c> rethrows them through <see cref="BeaconScheduler.AwaitTaskAsync"/>.
/// </summary>
public sealed class BeaconTask
{
    internal BeaconTask(
        long id,
        string name,
        string scriptId,
        CancellationTokenSource cancellation,
        Task<BeaconValue?> completion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(completion);
        Id = id;
        Name = name;
        ScriptId = scriptId;
        Cancellation = cancellation;
        Completion = completion;
        Status = BeaconTaskStatus.Running;
    }

    /// <summary>The numeric id <c>start</c> returned (1-based, sequential per scheduler).</summary>
    public long Id { get; }

    /// <summary>The task name given to <c>start</c> (function or handler label).</summary>
    public string Name { get; }

    /// <summary>The owning script id.</summary>
    public string ScriptId { get; }

    /// <summary>Current status; flips once when the body settles.</summary>
    public BeaconTaskStatus Status { get; internal set; }

    /// <summary>Return value when <see cref="Status"/> is <see cref="BeaconTaskStatus.Completed"/>.</summary>
    public BeaconValue? Result { get; internal set; }

    /// <summary>Catch-map value when <see cref="Status"/> is Failed or Cancelled.</summary>
    public BeaconValue? Error { get; internal set; }

    /// <summary>Completes (never faults) when the body settles.</summary>
    public Task<BeaconValue?> Completion { get; }

    /// <summary>Cancels the body; the status flips when the body observes it.</summary>
    internal CancellationTokenSource Cancellation { get; }

    /// <summary>Failure code when <see cref="Status"/> is Failed or Cancelled.</summary>
    internal string? FailureCode { get; set; }

    /// <summary>Failure message when <see cref="Status"/> is Failed or Cancelled.</summary>
    internal string? FailureMessage { get; set; }
}

/// <summary>The <c>tasks()</c> listing row: identity plus status, no live handles.</summary>
/// <param name="Id">Task id from <c>start</c>.</param>
/// <param name="Name">Task name from <c>start</c>.</param>
/// <param name="ScriptId">Owning script id.</param>
/// <param name="Status">Current status.</param>
/// <param name="Result">Return value when completed (null otherwise, or when the body returned null).</param>
public sealed record BeaconTaskInfo(long Id, string Name, string ScriptId, BeaconTaskStatus Status, BeaconValue? Result = null);

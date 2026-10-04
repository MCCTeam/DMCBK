namespace DMCBK.Core.Commands;

/// <summary>
/// The outcome status of an internal command.
/// Values mirror the legacy MCC status set exactly so the feature-gate contract is preserved: a disabled gameplay feature yields the matching <c>FailNeed*</c> status rather than a generic failure.
/// </summary>
public enum CmdStatus
{
    /// <summary>The command was parsed but never executed (default).</summary>
    NotRun = int.MinValue,

    /// <summary>The command needed a chunk that was not loaded.</summary>
    FailChunkNotLoad = -4,

    /// <summary>The command needed entity handling, which is disabled.</summary>
    FailNeedEntity = -3,

    /// <summary>The command needed inventory handling, which is disabled.</summary>
    FailNeedInventory = -2,

    /// <summary>The command needed terrain/movement handling, which is disabled.</summary>
    FailNeedTerrain = -1,

    /// <summary>The command ran but failed.</summary>
    Fail = 0,

    /// <summary>The command ran successfully.</summary>
    Done = 1,
}

/// <summary>
/// A typed command result: a <see cref="CmdStatus"/> plus an optional human-readable message.
/// The message is already localized text (built through <see cref="CommandStrings"/> / the translation service); the host renders it, the core never touches the console.
/// Replaces the legacy <c>CmdResult</c> and its static <c>currentHandler</c> ambient client.
/// </summary>
public sealed class CmdResult
{
    /// <summary>The result status.</summary>
    public CmdStatus Status { get; private set; } = CmdStatus.NotRun;

    /// <summary>The human-readable, already-localized result message, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>The optional immutable data snapshot associated with this result, independent of its displayed message.</summary>
    public object? Data { get; private set; }

    /// <summary>Attaches the typed result data for hosts that present their own views.</summary>
    public void SetData(object data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    /// <summary>True when the command succeeded.</summary>
    public bool IsSuccess => Status == CmdStatus.Done;

    /// <summary>Sets the status (message taken from the default text for that status) and returns the int code.</summary>
    public int Set(CmdStatus status)
    {
        Status = status;
        Message = DefaultMessage(status);
        return (int)status;
    }

    /// <summary>Sets the status and message and returns the int code.</summary>
    public int Set(CmdStatus status, string? message)
    {
        Status = status;
        Message = message;
        return (int)status;
    }

    /// <summary>Sets a success result with a message and returns the int code.</summary>
    public int Ok(string? message = null) => Set(CmdStatus.Done, message);

    /// <summary>Sets a failure result with a message and returns the int code.</summary>
    public int Fail(string? message = null) => Set(CmdStatus.Fail, message ?? CommandStrings.GeneralFail);

    private static string? DefaultMessage(CmdStatus status) => status switch
    {
        CmdStatus.FailNeedEntity => CommandStrings.NeedEntity,
        CmdStatus.FailNeedInventory => CommandStrings.NeedInventory,
        CmdStatus.FailNeedTerrain => CommandStrings.NeedTerrain,
        CmdStatus.FailChunkNotLoad => CommandStrings.ChunkNotLoaded,
        CmdStatus.Fail => CommandStrings.GeneralFail,
        _ => null,
    };

    /// <inheritdoc/>
    public override string ToString() => Message ?? Status.ToString();
}

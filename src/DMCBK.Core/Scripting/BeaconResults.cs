namespace DMCBK.Core.Beacon;
/// <summary>One dispatch outcome (top-level run or single handler invocation).</summary>
public sealed record BeaconRunResult(
    bool Success,
    BeaconValue? ReturnValue,
    IReadOnlyList<BeaconDiagnostic> Diagnostics,
    IReadOnlyList<string> LocalOutput,
    IReadOnlyList<BeaconPassthroughLog> PassthroughLog,
    IReadOnlyList<string> LocalEcho,
    BeaconDiagnostic? Error,
    bool EventSuppressed = false);

/// <summary>One handler's firing outcome inside a <see cref="BeaconFireResult"/>.</summary>
/// <param name="ScriptId">The owning script (trace attribution).</param>
/// <param name="Block">The registered block that fired or was throttled.</param>
/// <param name="Throttled">True when the throttle window skipped this handler (no invoke).</param>
/// <param name="ThrottleRemaining">Remaining window when throttled; zero otherwise.</param>
/// <param name="Result">The invoke result; null when throttled.</param>
public sealed record BeaconHandlerFire(
    string ScriptId,
    OnBlock Block,
    bool Throttled,
    TimeSpan ThrottleRemaining,
    BeaconRunResult? Result);

/// <summary>One <c>FireEvent</c> dispatch outcome across every registered handler.</summary>
/// <param name="EventName">The fired hook name.</param>
/// <param name="Suppressed">True when a suppressible hook's handler ran <c>stop event</c>.</param>
/// <param name="Handlers">Per-handler outcomes in registration order (script-attributed).</param>
/// <param name="Diagnostics">Aggregated handler warnings (verb nudges); throttles log, not diagnose.</param>
/// <param name="DebugLog">Throttle-skip lines (also sent to the logger at Debug).</param>
/// <param name="SuppressionNotes">No-op notes when <c>stop event</c> fired on a non-suppressible hook (never silent).</param>
public sealed record BeaconFireResult(
    string EventName,
    bool Suppressed,
    IReadOnlyList<BeaconHandlerFire> Handlers,
    IReadOnlyList<BeaconDiagnostic> Diagnostics,
    IReadOnlyList<string> DebugLog,
    IReadOnlyList<string> SuppressionNotes);

/// <summary>One passthrough audit entry: which script, line, verb, and detail.</summary>
/// <remarks>
/// FROZEN for the scheduler, event, and state runtimes: every <c>say</c>/<c>whisper</c>/<c>server</c>/<c>disconnect</c>/<c>mcc</c> call appends exactly one entry, even when refused (leading-slash <c>say</c> sends nothing but still logs).
/// <c>show</c> is local-only and never logs here.
/// Admins auditing a griefing report join on <see cref="ScriptId"/> plus <see cref="Line"/>; the public chat text itself stays unprefixed.
/// </remarks>
public sealed record BeaconPassthroughLog(string ScriptId, int Line, string Verb, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{ScriptId}:{Line} {Verb} {Detail}";
}


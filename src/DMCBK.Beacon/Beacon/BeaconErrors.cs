namespace DMCBK.Core.Beacon;

/// <summary>
/// Abort factories: every budget, jail, and bounds refusal is built here as a catchable <see cref="BeaconRuntimeException"/> with a stable B-code, a beginner-worded message naming handler, line, locals, task stack, and replay seed, plus one paste-ready suggestion.
/// Budget aborts intentionally share the Elm shape (see <see cref="BeaconErrorRenderer"/>): they read like any other script error, not like a crash.
/// </summary>
/// <remarks>
/// Budget aborts are catchable (<c>try/catch err</c> observes <c>err.code</c>/<c>err.line</c>/<c>err.message</c>).
/// This stays safe because fuel is never refunded mid-dispatch: a caught abort re-fires on the next accounting point, so a script cannot catch-and-spin.
/// </remarks>
public static class BeaconErrors
{
    /// <summary>Builds a fuel-exhaustion abort (B4007) with the <c>every</c> suggestion and full context.</summary>
    public static BeaconRuntimeException BudgetExhausted(
        string handler,
        SourceSpan span,
        string localsDump,
        IReadOnlyList<string> callStack,
        int seed,
        long used,
        long limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(localsDump);
        ArgumentNullException.ThrowIfNull(callStack);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.BudgetExhausted,
            $"Beacon fuel exhausted after {used:N0} of {limit:N0} steps in {handler}. "
            + "This loop has no 'wait' inside it, so it never yields. Interval work belongs in an 'every' block. "
            + $"Handler '{handler}' locals: {localsDump}. "
            + $"Task stack (innermost first): {BeaconDiagnostics.FormatCallStack(callStack)}. "
            + $"Seed {seed} replays this dispatch on the virtual clock with the same script.",
            span.Origin,
            "Add 'wait 1 second' inside the loop or move interval work to an 'every' block.");
    }

    /// <summary>Builds a wall-clock abort (B4007): compute without yielding outran the window.</summary>
    public static BeaconRuntimeException WallClockExceeded(
        string handler,
        SourceSpan span,
        string localsDump,
        IReadOnlyList<string> callStack,
        int seed,
        TimeSpan elapsed,
        TimeSpan limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(localsDump);
        ArgumentNullException.ThrowIfNull(callStack);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.BudgetExhausted,
            $"Beacon wall clock exceeded after {elapsed.TotalSeconds:F1}s of compute without yielding "
            + $"(limit {limit.TotalSeconds:F1}s) in {handler}. "
            + $"Handler '{handler}' locals: {localsDump}. "
            + $"Task stack (innermost first): {BeaconDiagnostics.FormatCallStack(callStack)}. "
            + $"Seed {seed} replays this dispatch on the virtual clock with the same script.",
            span.Origin,
            "Split the work across 'every' ticks or background tasks; each 'wait' re-arms the wall-clock window.");
    }

    /// <summary>Builds a recursion-depth abort (B4007): the CLR stack is never allowed to decide.</summary>
    public static BeaconRuntimeException RecursionTooDeep(
        string handler,
        SourceSpan span,
        string functionName,
        int depth,
        int maxDepth,
        int seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.BudgetExhausted,
            $"Beacon recursion past {maxDepth} nested calls in '{functionName}' ({handler}, depth {depth}). "
            + "Recursion this deep would overflow the host stack, so the budget stopped it first. "
            + $"Seed {seed} replays this dispatch on the virtual clock with the same script.",
            span.Origin,
            $"Rewrite '{functionName}' as a repeat/while loop or add an explicit depth guard.");
    }

    /// <summary>Builds an fs-jail refusal (B4009, catchable).</summary>
    public static BeaconRuntimeException FileJailRefused(SourceSpan span, string message, string suggestion)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(suggestion);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.FileJail, message, span.Origin, suggestion);
    }

    /// <summary>Builds a net-gate refusal (B4010, catchable).</summary>
    public static BeaconRuntimeException NetGateRefused(SourceSpan span, string message, string suggestion)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(suggestion);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.NetGate, message, span.Origin, suggestion);
    }

    /// <summary>Builds a read-bounds refusal (B4011, catchable).</summary>
    public static BeaconRuntimeException ReadBoundsRefused(SourceSpan span, string message, string suggestion)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(suggestion);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.ReadBounds, message, span.Origin, suggestion);
    }
}

/// <summary>
/// The single Elm-style error view for lint and runtime alike: <c>file:line:col</c>, source excerpt, caret, expectation, one paste-ready fix, did-you-mean.
/// No <c>span</c> jargon in the default view; pass <c>includeTrace</c> for the machine detail (code plus span) that experts and the trace hooks consume.
/// </summary>
public static class BeaconErrorRenderer
{
    /// <summary>Renders one diagnostic against <paramref name="source"/> (null omits excerpt/caret).</summary>
    public static string Render(BeaconDiagnostic diagnostic, string? source, bool includeTrace = false)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var lines = new List<string> { BeaconDiagnostics.FormatLocation(diagnostic.Span) };
        if (BeaconDiagnostics.TryExcerpt(source, diagnostic.Span, out string excerpt, out string caret))
        {
            lines.Add(excerpt);
            lines.Add(caret);
        }

        lines.Add(diagnostic.Message);
        if (!string.IsNullOrWhiteSpace(diagnostic.Suggestion))
        {
            lines.Add("Try this:");
            lines.Add("  " + diagnostic.Suggestion);
        }

        if (includeTrace)
        {
            SourceSpan origin = diagnostic.Span.Origin;
            lines.Add($"trace: {diagnostic.Code} span={origin.File}:{origin.Line}:{origin.Column}+{origin.Length}");
        }

        return string.Join("\n", lines);
    }
}

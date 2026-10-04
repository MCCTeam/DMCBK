namespace DMCBK.Core.Beacon;
/// <summary>Raised for every Beacon runtime failure (strictness, names, verbs, counts).</summary>
/// <remarks>
/// <para>
/// FROZEN error-value shape for the scheduler, event, and state runtimes: a caught error binds as a map with at least <c>message</c> text (<c>err.message</c>).
/// New keys such as <c>code</c>/<c>line</c> may be added; they will not remove <c>message</c> or change its text-ness.
/// </para>
/// <para>
/// Catchability: <see cref="IsCatchable"/> true means <c>try/catch err</c> handles it (verb failures, strictness, arity, counts).
/// False means it always aborts the dispatch (nested declarations, hole placeholders, fuel exhaustion is a different exception type).
/// </para>
/// </remarks>
public sealed class BeaconRuntimeException : Exception
{
    /// <summary>Builds a runtime failure with a stable code, message, span, and fix.</summary>
    public BeaconRuntimeException(
        string code,
        string message,
        SourceSpan span,
        string? suggestion = null,
        Exception? inner = null,
        bool isCatchable = true)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(span);
        Code = code;
        Span = span;
        Suggestion = suggestion;
        IsCatchable = isCatchable;
    }

    /// <summary>Stable code (<c>B2xxx</c>/<c>B3xxx</c> for lint-shared; <c>B4001</c>/<c>B4002</c> runtime-only).</summary>
    public string Code { get; }

    /// <summary>User-text span (<see cref="SourceSpan.Origin"/>) for the failure.</summary>
    public SourceSpan Span { get; }

    /// <summary>Paste-ready fix, when one exists.</summary>
    public string? Suggestion { get; }

    /// <summary>False aborts the dispatch even inside <c>try</c>.</summary>
    public bool IsCatchable { get; }

    /// <summary>Converts to the catch value: a map with <c>message</c> text plus <c>code</c> and <c>line</c>.</summary>
    public BeaconValue ToErrorValue() => BeaconValue.Map(new Dictionary<string, BeaconValue>
    {
        ["message"] = BeaconValue.Text(Message),
        ["code"] = BeaconValue.Text(Code),
        ["line"] = BeaconValue.Number(Span.Origin.Line),
    });
}


namespace DMCBK.Core.Beacon;

/// <summary>Diagnostic severity for Beacon lint/parse output (headless JSON and in-client agree).</summary>
public enum BeaconSeverity
{
    /// <summary>Refuses to load; exit code 1 in headless lint.</summary>
    Error,
    /// <summary>Loads anyway; reported but non-blocking.</summary>
    Warning,
    /// <summary>Informational note attached to another diagnostic.</summary>
    Note,
}

/// <summary>One lint finding: stable code, severity, message, span, and a paste-ready suggestion.</summary>
public sealed record BeaconDiagnostic(
    string Code,
    BeaconSeverity Severity,
    string Message,
    SourceSpan Span,
    string? Suggestion = null);

/// <summary>Registry entry pairing a stable code with its default severity and description.</summary>
public sealed record BeaconDiagnosticDescriptor(string Code, BeaconSeverity DefaultSeverity, string Description);

/// <summary>
/// Stable diagnostic codes shared by in-client <c>/scripts lint</c> and headless <c>lint</c> so the two can never disagree.
/// Families: <c>B0001</c> parse, <c>B1xxx</c> manifests, <c>B2xxx</c> names/events, <c>B3xxx</c> strictness.
/// New codes extend <see cref="All"/>; codes are never reused or renumbered.
/// </summary>
public static class BeaconDiagnosticCodes
{
    /// <summary>Parse failure (header gate, grammar violations).</summary>
    public const string Parse = "B0001";

    /// <summary>Unclosed <c>/* ... */</c> span: closed at EOF with one warning.</summary>
    public const string UnclosedBlockComment = "B0002";

    /// <summary>Discord-paste normalization applied (smart quotes); names the line, never silent.</summary>
    public const string PasteNormalization = "B0003";

    /// <summary><c># needs:</c> capability mismatch: refuses to load.</summary>
    public const string ManifestNeedsMismatch = "B1001";

    /// <summary><c># wants:</c> provider unavailable: warn-only degradation.</summary>
    public const string ManifestWantsUnavailable = "B1002";

    /// <summary><c># needs:</c>/<c># wants:</c> after the first declaration: ordinary comment, warn once.</summary>
    public const string LateManifest = "B1003";

    /// <summary>Unknown event hook (closed v1 set plus plugin-registered names).</summary>
    public const string UnknownEvent = "B2001";

    /// <summary>Unknown variable or builtin name.</summary>
    public const string UnknownName = "B2002";

    /// <summary>Non-yes/no value in an <c>if</c>/<c>while</c>/<c>when</c> condition.</summary>
    public const string StrictBooleanCondition = "B3001";

    /// <summary>Mixed <c>+</c> operand kinds (text plus number).</summary>
    public const string StrictMixedOperands = "B3002";

    /// <summary><c>=</c> outside the listed operators: suggests <c>set ... to</c> or <c>is</c>.</summary>
    public const string StrictEquals = "B3003";

    /// <summary>Statement line starting with <c>/</c>: suggests <c>server</c> or <c>mcc</c>.</summary>
    public const string StrictLeadingSlash = "B3004";

    /// <summary>Unknown or misplaced time unit after <c>wait</c>/<c>every</c>/<c>cooldown</c>.</summary>
    public const string UnknownUnit = "B3005";

    /// <summary>Chat text carrying characters the server would kick for (newlines, section signs).</summary>
    public const string StrictChatText = "B3006";

    /// <summary>An <c>import</c> path resolves to no readable file inside the importing folder (fail closed).</summary>
    public const string ImportNotFound = "B1004";

    /// <summary>Circular <c>import</c> chain, naming every file in the cycle.</summary>
    public const string ImportCycle = "B1005";

    /// <summary>
    /// An <c>extern</c>/<c>call</c> target cannot be resolved offline (no provider registry yet).
    /// Warns; <c>--strict</c> escalates to an error.
    /// </summary>
    public const string UnresolvedBridge = "B1006";

    /// <summary>A builtin or block kind newer than <c>--target-lib N</c> (the interop surface is lib 2).</summary>
    public const string LibNovelty = "B1007";

    /// <summary>
    /// A forgiven spelling with a mechanical normalization (<c>cancel event</c>, bare <c>end</c>).
    /// Informational only; <c>--fix</c> applies exactly this class and nothing else.
    /// </summary>
    public const string ForgivenForm = "B1008";

    /// <summary>Dispatch exceeded its fuel or wall-clock budget (catchable; carries handler/line/locals/stack/seed).</summary>
    public const string BudgetExhausted = "B4007";

    /// <summary>Chat throttled by the global bucket: the message queued, warns only.</summary>
    public const string ChatThrottled = "B4008";

    /// <summary>File access outside the script data jail was refused (catchable).</summary>
    public const string FileJail = "B4009";

    /// <summary>Network access refused by the allowlist gate (catchable).</summary>
    public const string NetGate = "B4010";

    /// <summary>A read exceeded its bound (paged players, history cap, block range; catchable).</summary>
    public const string ReadBounds = "B4011";

    /// <summary>A world write was refused by the write gate: rate limit, gameplay gate, or disabled feature (catchable).</summary>
    public const string WorldWriteGate = "B4013";

    /// <summary>Every known code; tests pin format (<c>^B\d{4}$</c>) and uniqueness.</summary>
    public static IReadOnlyList<BeaconDiagnosticDescriptor> All { get; } =
    [
        new(Parse, BeaconSeverity.Error, "Beacon source failed to parse."),
        new(UnclosedBlockComment, BeaconSeverity.Warning, "Unclosed block comment; closed at end of file."),
        new(PasteNormalization, BeaconSeverity.Note, "Pasted text was normalized; check the named line."),
        new(ManifestNeedsMismatch, BeaconSeverity.Error, "Script needs a capability no loaded provider offers."),
        new(ManifestWantsUnavailable, BeaconSeverity.Warning, "Script wants a capability no loaded provider offers."),
        new(LateManifest, BeaconSeverity.Warning, "Manifest line after the first declaration is an ordinary comment."),
        new(UnknownEvent, BeaconSeverity.Error, "Unknown event hook."),
        new(UnknownName, BeaconSeverity.Error, "Unknown variable or builtin name."),
        new(StrictBooleanCondition, BeaconSeverity.Error, "Condition must be a yes/no value."),
        new(StrictMixedOperands, BeaconSeverity.Error, "Plus operands must both be text or both be numbers."),
        new(StrictEquals, BeaconSeverity.Error, "Equals sign does not assign; use set ... to or is."),
        new(StrictLeadingSlash, BeaconSeverity.Error, "A statement line must not start with a slash."),
        new(UnknownUnit, BeaconSeverity.Error, "Unknown time unit."),
        new(StrictChatText, BeaconSeverity.Error, "Chat text carries characters the server would kick for."),
        new(ImportNotFound, BeaconSeverity.Error, "An import path resolves to no readable script file."),
        new(ImportCycle, BeaconSeverity.Error, "Circular import chain."),
        new(UnresolvedBridge, BeaconSeverity.Warning, "An extern/call target cannot be resolved offline."),
        new(LibNovelty, BeaconSeverity.Error, "A builtin or block kind is newer than the target library."),
        new(ForgivenForm, BeaconSeverity.Note, "A forgiven spelling with a mechanical normalization."),
        new(BudgetExhausted, BeaconSeverity.Error, "Beacon dispatch exceeded its fuel or wall-clock budget."),
        new(ChatThrottled, BeaconSeverity.Warning, "Script chat was throttled by the global bucket; the message queued."),
        new(FileJail, BeaconSeverity.Error, "Script file access outside its data jail was refused."),
        new(NetGate, BeaconSeverity.Error, "Script network access was refused by the allowlist gate."),
        new(ReadBounds, BeaconSeverity.Error, "Script read exceeded its bound."),
        new(WorldWriteGate, BeaconSeverity.Error, "Script world write was refused by the write gate."),
    ];
}

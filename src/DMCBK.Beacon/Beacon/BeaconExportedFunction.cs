namespace DMCBK.Core.Beacon;
/// <summary>One script export visible to C# and to <c>call</c>: its body plus owning interpreter.</summary>
/// <param name="ScriptId">The owning script id (file name without extension).</param>
/// <param name="Name">The exported function name.</param>
/// <param name="Params">Parameter names.</param>
/// <param name="Body">The function body.</param>
/// <param name="Span">The definition span (user-text origin).</param>
/// <param name="Interpreter">The owning script interpreter (globals live here).</param>
public sealed record BeaconExportedFunction(
    string ScriptId,
    string Name,
    IReadOnlyList<string> Params,
    BeaconBlock Body,
    SourceSpan Span,
    BeaconInterpreter Interpreter);


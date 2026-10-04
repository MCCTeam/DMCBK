namespace DMCBK.Core.Beacon;
/// <summary>
/// One extension-function call: which function, who owns it, the script arguments, where the caller sits, and the cancellation that fires on reconnect and unload.
/// </summary>
/// <param name="Function">The extension function name.</param>
/// <param name="OwnerPluginId">The plugin that registered it (for traces and abort counting).</param>
/// <param name="Args">The script-side arguments (already evaluated).</param>
/// <param name="CallerScriptId">The calling script id.</param>
/// <param name="CallerSpan">The caller line (user-text origin).</param>
/// <param name="Cancellation">Caller cancellation: the dispatch token linked with the event budget cap.</param>
public sealed record BeaconExtensionCall(
    string Function,
    string OwnerPluginId,
    IReadOnlyList<BeaconValue> Args,
    string CallerScriptId,
    SourceSpan CallerSpan,
    CancellationToken Cancellation);

/// <summary>One C# extension function: its metadata plus the scheduler-threaded invoke.</summary>
/// <param name="Name">The script-side name.</param>
/// <param name="OwnerPluginId">The registering plugin id.</param>
/// <param name="Capability">The manifest capability scripts declare to use it.</param>
/// <param name="Description">Help text shown by lint and REPL autocomplete.</param>
/// <param name="ParameterNames">Parameter names for arity checks and help.</param>
/// <param name="Invoke">Runs on the Beacon scheduler, never the session loop.</param>
public sealed record BeaconExtensionFunction(
    string Name,
    string OwnerPluginId,
    string Capability,
    string Description,
    IReadOnlyList<string> ParameterNames,
    Func<BeaconExtensionCall, Task<BeaconValue>> Invoke);

/// <summary>One read-only value export: <c>export set name to expr</c>, snapshotted at load.</summary>
/// <param name="ScriptId">The owning script id (file name without extension).</param>
/// <param name="Name">The exported value name.</param>
/// <param name="Value">The snapshotted value (maps cross read-only by copy-on-read).</param>
/// <param name="Span">The definition span (user-text origin).</param>
public sealed record BeaconExportedValue(
    string ScriptId,
    string Name,
    BeaconValue Value,
    SourceSpan Span);

/// <summary>One plugin variable namespace: a snapshot function returning a read-only map per read.</summary>
/// <param name="Name">The script-side namespace (for example <c>coins</c> in <c>coins.balance</c>).</param>
/// <param name="OwnerPluginId">The registering plugin id.</param>
/// <param name="Capability">The manifest capability scripts declare to read it.</param>
/// <param name="Description">Help text shown by lint and REPL autocomplete.</param>
/// <param name="Snapshot">Returns the current fields; values cross as the six kinds, anything else fails fast.</param>
public sealed record BeaconVariableRegistration(
    string Name,
    string OwnerPluginId,
    string Capability,
    string Description,
    Func<CancellationToken, object?> Snapshot);


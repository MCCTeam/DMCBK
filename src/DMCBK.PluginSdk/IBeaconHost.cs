using DMCBK.Core.Beacon;

namespace DMCBK.PluginSdk;

/// <summary>
/// One call into an extension function: the evaluated script arguments plus the caller line and the cancellation that fires on reconnect and unload.
/// Arguments arrive as the six boundary kinds (text/string, number/double, yes-no/bool, list/read-only list, map/read-only string-keyed dict, none/null); snapshots ride in through arguments, never as cached scopes.
/// </summary>
public sealed class BeaconCallContext
{
    internal BeaconCallContext(
        string function, string callerScriptId, SourceSpan callerSpan,
        IReadOnlyList<BeaconValue> args, CancellationToken cancellation)
    {
        Function = function;
        CallerScriptId = callerScriptId;
        CallerSpan = callerSpan;
        Args = args;
        Cancellation = cancellation;
    }

    /// <summary>The extension function name.</summary>
    public string Function { get; }

    /// <summary>The calling script id.</summary>
    public string CallerScriptId { get; }

    /// <summary>The caller line (user-text origin).</summary>
    public SourceSpan CallerSpan { get; }

    /// <summary>The evaluated script arguments.</summary>
    public IReadOnlyList<BeaconValue> Args { get; }

    /// <summary>
    /// The dispatch cancellation, always capped by the event budget: session-task aborts, reconnect, unload, and driver-propagated aborts flow through it.
    /// Honor it and do not block.
    /// </summary>
    public CancellationToken Cancellation { get; }
    /// <summary>Argument count.</summary>
    public int Count => Args.Count;

    /// <summary>Requires argument <paramref name="index"/> to be text; throws otherwise.</summary>
    public string RequireText(int index)
        => Args[index] is BeaconTextValue text
            ? text.Value
            : throw Arity($"argument {index} of '{Function}' must be text.");

    /// <summary>Requires argument <paramref name="index"/> to be a number; throws otherwise.</summary>
    public double RequireNumber(int index)
        => Args[index] is BeaconNumberValue number
            ? number.Value
            : throw Arity($"argument {index} of '{Function}' must be a number.");

    /// <summary>Requires argument <paramref name="index"/> to be yes/no; throws otherwise.</summary>
    public bool RequireYesNo(int index)
        => Args[index] is BeaconYesNoValue yesNo
            ? yesNo.Value
            : throw Arity($"argument {index} of '{Function}' must be yes/no.");

    private InvalidOperationException Arity(string message) => new(message);
}

/// <summary>Registers one plugin's extension functions.</summary>
public interface IBeaconFunctionRegistry
{
    /// <summary>
    /// Registers <paramref name="function"/> under its name.
    /// A name claimed by a different plugin is a load error naming both; re-registering your own name replaces it.
    /// The returned handle withdraws the registration; the host disposes it for you on unload, disable, and reload.
    /// </summary>
    IDisposable Register(BeaconFunction function);
}

/// <summary>Registers one plugin's variable namespaces.</summary>
public interface IBeaconVariableRegistry
{
    /// <summary>
    /// Registers <paramref name="variable"/> under its namespace.
    /// A name claimed by a different plugin is a load error naming both; re-registering your own name replaces it.
    /// The returned handle withdraws the registration; the host disposes it for you on unload, disable, and reload.
    /// </summary>
    IDisposable Register(BeaconVariable variable);
}

/// <summary>
/// One plugin variable namespace: scripts read it as a read-only map per read (for example <c>coins.balance</c>), with the same six-kind marshaling, capability strings, and unload withdrawal as functions.
/// </summary>
/// <param name="Name">The script-side namespace.</param>
/// <param name="Capability">The manifest capability scripts declare to read it.</param>
/// <param name="Description">Help text shown by lint and REPL autocomplete.</param>
/// <param name="Snapshot">Returns the current fields; runs inline on the dispatch, so it must be fast and never block.</param>
public sealed record BeaconVariable(
    string Name,
    string Capability,
    string Description,
    Func<CancellationToken, object?> Snapshot);

/// <summary>
/// The C# side of the Beacon bridge, reached as <see cref="PluginContext.Beacon"/>.
/// Four extension points: offer functions scripts call as <c>extern</c>, offer read-only variable namespaces scripts read as maps, declare custom events scripts consume with the ordinary <c>on</c> form, and await exported script functions.
/// Extension functions execute on the Beacon scheduler, never the session loop; they must be reentrant and stateless beyond their own storage, and they cannot steer except by returning data to script-side <c>move_*</c> tasks.
/// </summary>
public interface IBeaconHost
{
    /// <summary>Registers extension functions scripts call as <c>extern</c>.</summary>
    IBeaconFunctionRegistry Functions { get; }

    /// <summary>Registers read-only variable namespaces scripts read as maps.</summary>
    IBeaconVariableRegistry Variables { get; }

    /// <summary>
    /// Declares a custom event with its fields.
    /// Scripts consume it with the ordinary <c>on name</c> form.
    /// A name claimed by a different plugin is a load error naming both.
    /// The returned handle withdraws the registration.
    /// </summary>
    IDisposable RegisterEvent(
        string name,
        IReadOnlyList<string> fields,
        string description,
        bool suppressible = false,
        string? capability = null);

    /// <summary>
    /// Fires a plugin-registered event to every matching <c>on</c> block.
    /// Values cross as the six kinds; anything else fails fast.
    /// Honors <paramref name="detached"/> exactly like any session loop must: a fired token skips dispatch, so nothing fires into a dead session.
    /// </summary>
    Task<BeaconFireResult> FireEventAsync(
        string name,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken detached = default);

    /// <summary>
    /// Awaits an exported script function (<c>export function</c>).
    /// The first argument is the script id (file name without extension).
    /// A missing script, a missing export, or an arity mismatch raises <see cref="BeaconCallException"/> naming all three.
    /// </summary>
    Task<object?> CallFunctionAsync(
        string scriptId, string function, IReadOnlyList<object?> args, CancellationToken ct = default);
}

using System.Globalization;
using System.Text.RegularExpressions;
using DMCBK.Core.Commands;

namespace DMCBK.Core.Beacon;

/// <summary>Control-flow return (never caught by <c>try/catch err</c>).</summary>
public sealed class BeaconReturnException : Exception
{
    /// <summary>Builds a return with its value (none for bare <c>return</c>).</summary>
    public BeaconReturnException(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The returned value.</summary>
    public BeaconValue Value { get; }
}

/// <summary>Control-flow break for bare <c>stop</c> (caught by loops; otherwise stops the handler).</summary>
public sealed class BeaconBreakException : Exception
{
}

/// <summary>Control-flow continue for <c>skip</c> (caught by loops; errors outside loops).</summary>
public sealed class BeaconContinueException : Exception
{
}

/// <summary>Control-flow for <c>stop event</c> (suppresses the event and stops the handler).</summary>
public sealed class BeaconStopEventException : Exception
{
}

/// <summary>One function definition with its REPL/lint description.</summary>
public sealed record BeaconFunctionInfo(
    string Name,
    IReadOnlyList<string> Params,
    BeaconBlock Body,
    bool IsExport,
    string? Description,
    SourceSpan Span);

/// <summary>
/// Tree-walk evaluator over desugared-or-raw <see cref="BeaconScript"/> nodes, with call frames, per-script globals, a read-only builtins view, and fuel-seam hooks for recursion.
/// </summary>
/// <remarks>
/// <para>
/// FROZEN evaluation API for the scheduler, event, and state runtimes (scope lookup order, verb result shapes, error value shape).
/// </para>
/// <list type="bullet">
/// <item>
/// Scope lookup order is defined on <see cref="BeaconScope"/> and never reordered here: function locals/params, handler event fields, per-script globals, read-only builtins, bare-shortcut twins (<c>health</c> for <c>me.health</c> etc., no bare <c>hunger</c>).
/// </item>
/// <item>
/// Verb result shapes (see <see cref="BeaconVerbs"/>): <c>say</c>/<c>whisper</c>/<c>server</c>/ <c>show</c> return nothing as statements; <c>mcc</c> returns text; failures raise catchable <see cref="BeaconRuntimeException"/> whose catch value is a map with <c>message</c>.
/// </item>
/// <item>
/// Error value shape: <c>catch err</c> binds a map with at least <c>message</c> text.
/// New keys may be added but <c>message</c> is never removed.
/// </item>
/// <item>
/// Fuel: <see cref="IFuelBudget.Spend"/> on every function call, loop back-edge, wait, and host call.
/// <see cref="BeaconFuelExhaustedException"/> is never catchable via <c>try</c>.
/// </item>
/// <item>
/// <c>wait</c> yields through <see cref="IVirtualClock.Delay"/> with a 100 ms floor enforced here; the full scheduler (32-sleep cap, reconnect cancel) lives in the scheduler runtime.
/// </item>
/// </list>
/// </remarks>
public sealed class BeaconInterpreter
{
    private static readonly HashSet<string> BuiltinFunctions = new(StringComparer.Ordinal)
    {
        "text", "number", "yesno", "len", "lower", "upper", "trim", "trim_start", "trim_end",
        "split", "join",
        "slice", "replace", "replace_first", "index_of", "pad_start", "pad_end", "repeat_str", "escape_regex",
        "sort", "reverse", "unique",
        "keys", "values", "has_key", "match", "match_all", "random", "pick", "chance",
        "min", "max", "clamp", "round", "abs", "log", "arg", "saved", "eat",
        "online_players", "json_parse", "json_stringify",
        "chat_history", "last_from", "count_matching", "tasks", "chat_bucket", "assert",
        "file_read", "file_write", "http_get", "http_post",
        "craft_list", "craft_one",
        "move_goto", "move_follow", "stop_moving", "look_at", "attack", "interact", "use_in_hand",
    };

    private static readonly HashSet<string> BuiltinValues = new(StringComparer.Ordinal)
    {
        "me", "server", "game", "time", "shared", "event", "online_players",
        "beacon", "vars", "settings", "items", "effects", "enchants",
    };

    private static readonly Dictionary<string, (string Ns, string Key)> BareTwins =
        new(StringComparer.Ordinal)
        {
            ["health"] = ("me", "health"),
            ["max_health"] = ("me", "max_health"),
            ["food"] = ("me", "food"),
            ["saturation"] = ("me", "saturation"),
            ["air"] = ("me", "air"),
            ["xp_level"] = ("me", "xp_level"),
            ["online_count"] = ("server", "online_count"),
            ["tps"] = ("server", "tps"),
        };

    private readonly Dictionary<string, BeaconFunctionInfo> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeaconValue> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeaconValue> _shared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, BeaconFunctionInfo>> _importAliases = new(StringComparer.Ordinal);
    private BeaconExternTable? _externs;
    private string? _moduleContext;
    private readonly List<(string ScriptId, int Line, string Name)> _discarded = [];
    private readonly Dictionary<long, TaskSinks> _taskSinks = new();
    private readonly object _taskSinksGate = new();
    private string _currentHandler = "top-level";

    /// <summary>Per-task diagnostic sinks, merged into the awaiting dispatch on <c>await</c>.</summary>
    private sealed class TaskSinks
    {
        public List<BeaconPassthroughLog> Passthrough { get; } = [];
        public List<string> Echo { get; } = [];
        public List<string> Output { get; } = [];
        public List<BeaconDiagnostic> Warnings { get; } = [];
    }

    /// <summary>Spends one accounting step with handler attribution for abort dumps.</summary>
    private void Spend(BeaconScope? scope, SourceSpan span) => Budget.Spend(scope, span);

    /// <summary>Builds an interpreter for one script id over injectable seams.</summary>
    public BeaconInterpreter(
        string scriptId,
        string fileName,
        IBeaconHostServices host,
        IVirtualClock? clock = null,
        ISeededRng? rng = null,
        IFuelBudget? fuel = null,
        TimeSpan? budgetWallClock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(host);
        ScriptId = scriptId;
        FileName = fileName;
        Host = host;
        Clock = clock ?? SystemClock.Shared;
        Rng = rng ?? SeededRng.FromEntropy();
        Fuel = fuel ?? new FuelBudget();
        Budget = new BeaconDispatchBudget(Fuel, Clock, budgetWallClock);
        Globals = new BeaconScope();
    }

    /// <summary>Owning script id (passthrough logs and echo stamps).</summary>
    public string ScriptId { get; }

    /// <summary>File name for spans.</summary>
    public string FileName { get; }

    /// <summary>Host seam (fakes in tests, live adapters in the client host).</summary>
    public IBeaconHostServices Host { get; }

    /// <summary>Scheduler clock seam (virtual in tests).</summary>
    public IVirtualClock Clock { get; }

    /// <summary>Per-dispatch seeded RNG source (fresh instance per dispatch, see <see cref="BeginDispatch"/>).</summary>
    public ISeededRng Rng { get; private set; }

    /// <summary>Step accounting budget.</summary>
    public IFuelBudget Fuel { get; }

    /// <summary>Per-dispatch budget enforcement: fuel plus wall clock plus recursion depth.</summary>
    public BeaconDispatchBudget Budget { get; }

    /// <summary>
    /// Scheduler for <c>start</c>/<c>await</c>/<c>cancel task</c> and capped waits.
    /// Null keeps the legacy refusal (tasks arrive with the scheduler); the engine binds it on the load path.
    /// </summary>
    public BeaconScheduler? Scheduler { get; set; }

    /// <summary>Global chat bucket for <c>say</c>/<c>whisper</c>. Null sends unthrottled (legacy).</summary>
    public BeaconChatBucket? ChatBucket { get; set; }

    /// <summary>Fs jail for <c>file_read</c>/<c>file_write</c>. Null refuses (fail closed).</summary>
    public BeaconFileJail? FileJail { get; set; }

    /// <summary>Net gate for <c>http_get</c>/<c>http_post</c>. Null refuses (fail closed).</summary>
    public BeaconNetGate? NetGate { get; set; }

    /// <summary>
    /// The interop bridge for <c>extern</c> calls, <c>call</c> targets, and custom events.
    /// Null means standalone: every bridge call fails with a catchable missing-provider error.
    /// The engine binds its own bridge on the load path.
    /// </summary>
    public BeaconBridge? Bridge { get; set; }

    /// <summary>
    /// Module resolver for <c>import</c> (file-backed in production, in-memory overlays in tests).
    /// Null means imports fail closed at load with B1004.
    /// </summary>
    public IBeaconModuleResolver? ModuleResolver { get; set; }

    /// <summary>
    /// Shared <c>%var%</c> store behind <c>vars.beacon.*</c>.
    /// Null leaves <c>vars</c> empty and refuses <c>vars.beacon.*</c> writes.
    /// The engine binds the client store on the load path.
    /// </summary>
    public VariableStore? Variables { get; set; }

    /// <summary>
    /// Named arguments of the running <c>command</c> block (<c>arg(name)</c> reads these).
    /// Null outside a command dispatch, where <c>arg</c> yields none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CommandArgs { get; set; }

    /// <summary>
    /// Movement runner for <c>move_goto</c>/<c>move_follow</c>/<c>stop_moving</c> with newest-wins preemption.
    /// Null refuses steering; the engine binds a runner on the load path.
    /// </summary>
    public BeaconMovementRunner? Movement { get; set; }

    /// <summary>
    /// Live gameplay gates for world writes (<c>world.dig</c>/<c>world.place</c>/<c>world.use</c>/<c>world.looking_at</c>).
    /// Defaults to all-on (headless); the live host rebinds from the client configuration on the load path.
    /// </summary>
    public BeaconGameplayGates Gates { get; set; } = BeaconGameplayGates.AllOn;

    /// <summary>
    /// Per-script write gate behind <c>world.dig</c>/<c>world.place</c>/<c>world.use</c> (bucket-style rate limit).
    /// One per interpreter, over the dispatch clock.
    /// </summary>
    public BeaconWorldWriteGate WriteGate => _writeGate ??= new BeaconWorldWriteGate(Clock);
    private BeaconWorldWriteGate? _writeGate;

    /// <summary>
    /// Per-script settings behind the <c>settings</c> map (declared <c># setting</c> defaults overlaid with the settings file).
    /// Empty until the engine binds them on the load path.
    /// </summary>
    public IReadOnlyDictionary<string, BeaconValue> Settings { get; set; }
        = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);

    /// <summary>Read-only value exports published by this source (<c>export set name to expr</c>).</summary>
    public IReadOnlyDictionary<string, (BeaconValue Value, SourceSpan Span)> ValueExports => _valueExports;
    private readonly Dictionary<string, (BeaconValue Value, SourceSpan Span)> _valueExports = new(StringComparer.Ordinal);

    /// <summary>
    /// Trace hook: when set, every executed statement runs inside the returned scope, which records the per-line structured log (<c>run --trace</c>).
    /// Null disables tracing with one null check; evaluation is untouched.
    /// </summary>
    public BeaconTraceSink? Tracer { get; set; }

    /// <summary>Per-script globals root (never shared across scripts).</summary>
    public BeaconScope Globals { get; private set; }

    /// <summary>Registered functions by name.</summary>
    public IReadOnlyDictionary<string, BeaconFunctionInfo> Functions => _functions;

    /// <summary>Discarded-return call sites for the linter (script id, line, name).</summary>
    public IReadOnlyList<(string ScriptId, int Line, string Name)> DiscardedReturns => _discarded;

    /// <summary>Clears globals/functions/descs/discarded/value-exports for a reload; saved/shared survive.</summary>
    public void ResetForNewSource()
    {
        Globals = new BeaconScope();
        _functions.Clear();
        _descriptions.Clear();
        _discarded.Clear();
        _valueExports.Clear();
        _importAliases.Clear();
        _moduleContext = null;
        _externs = null;
    }

    /// <summary>Declared <c>extern</c> names for this source (null before the first load).</summary>
    public BeaconExternTable? Externs => _externs;

    /// <summary>Imported module aliases and their function tables (alias to name to function).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, BeaconFunctionInfo>> ImportAliases
    {
        get
        {
            var view = new Dictionary<string, IReadOnlyDictionary<string, BeaconFunctionInfo>>(StringComparer.Ordinal);
            foreach ((string alias, Dictionary<string, BeaconFunctionInfo> table) in _importAliases)
                view[alias] = table;

            return view;
        }
    }

    /// <summary>
    /// Arms one dispatch: a fresh seeded RNG (deterministic replay from <paramref name="seed"/>), a reset fuel plus wall-clock window, and handler attribution for abort dumps.
    /// Entry points call this first; pass an explicit seed to replay a failed dispatch bit-for-bit, or null to derive from the entropy chain (deterministic whenever the engine seed is fixed).
    /// </summary>
    public void BeginDispatch(string handler, int? seed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        int resolved = seed ?? Rng.Next(int.MaxValue);
        Rng = new SeededRng(resolved);
        Budget.Reset(resolved, handler);
        _currentHandler = handler;
    }

    /// <summary>Seed of the current dispatch (recorded on failure for replay).</summary>
    public int DispatchSeed => Budget.Seed;

    /// <summary>Snapshot of globals for tests and timeout dumps.</summary>
    public IReadOnlyDictionary<string, BeaconValue> GetGlobalsSnapshot() => Globals.Snapshot();

    /// <summary>
    /// Snapshot of the in-memory <c>saved</c> table (cloned values) for engine persistence bridging.
    /// The engine faults disk state in via <see cref="RestoreSaved"/> and flushes back after dispatches.
    /// </summary>
    public IReadOnlyDictionary<string, BeaconValue> SnapshotSaved()
    {
        var copy = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, BeaconValue> entry in _saved)
            copy[entry.Key] = BeaconValueTomlCodec.Clone(entry.Value);

        return copy;
    }

    /// <summary>
    /// Replaces the in-memory <c>saved</c> table with a cloned copy (engine reload path).
    /// </summary>
    public void RestoreSaved(IReadOnlyDictionary<string, BeaconValue> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _saved.Clear();
        foreach (KeyValuePair<string, BeaconValue> entry in snapshot)
            _saved[entry.Key] = BeaconValueTomlCodec.Clone(entry.Value);
    }

    /// <summary>
    /// Snapshot of the local in-memory shared table (cloned values).
    /// </summary>
    public IReadOnlyDictionary<string, BeaconValue> SnapshotShared()
    {
        var copy = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, BeaconValue> entry in _shared)
            copy[entry.Key] = BeaconValueTomlCodec.Clone(entry.Value);

        return copy;
    }

    #region shared helpers (public for tests and runtime wiring)

    /// <summary>Conservative falsiness: only <c>no</c> and <c>none</c> are false.</summary>
    public static bool IsFalsy(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value is BeaconYesNoValue yesNo && !yesNo.Value
            || value is BeaconNoneValue;
    }

    /// <summary>Lowercase kind name for messages.</summary>
    public static string DescribeValueKind(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            BeaconValueKind.Text => "text",
            BeaconValueKind.Number => "number",
            BeaconValueKind.YesNo => "yes/no",
            BeaconValueKind.List => "list",
            BeaconValueKind.Map => "map",
            BeaconValueKind.None => "none",
            _ => "value",
        };
    }

    /// <summary>Converts any value to display text (interpolation, show, echo).</summary>
    public static string ToDisplayText(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconTextValue text => text.Value,
            BeaconNumberValue number => number.Value.ToString("G", CultureInfo.InvariantCulture),
            BeaconYesNoValue yesNo => yesNo.Value ? "yes" : "no",
            BeaconNoneValue => "none",
            BeaconListValue or BeaconMapValue => value.ToDisplayString(),
            _ => value.ToDisplayString(),
        };
    }

    /// <summary>Renders an assignment target trailer back to source-like text for fixes.</summary>
    private static string DescribeTargetParts(BeaconTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var sb = new System.Text.StringBuilder();
        foreach (BeaconTargetPart part in target.Parts)
        {
            switch (part)
            {
                case TargetMember member:
                    sb.Append('.').Append(member.Name);
                    break;
                case TargetIndex:
                    sb.Append("[...]");
                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Renders an expression back to source-like text for paste-ready fixes.</summary>
    public static string DescribeExpr(BeaconExpr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        return expr switch
        {
            IdentExpr ident => ident.Name,
            MemberExpr member => $"{DescribeExpr(member.Target)}.{member.Member}",
            IndexExpr index => $"{DescribeExpr(index.Target)}[{DescribeExpr(index.Index)}]",
            OrExpr or => $"{DescribeExpr(or.Left)} or {DescribeExpr(or.Right)}",
            AndExpr and => $"{DescribeExpr(and.Left)} and {DescribeExpr(and.Right)}",
            NotExpr not => $"not {DescribeExpr(not.Operand)}",
            ParenExpr paren => $"({DescribeExpr(paren.Inner)})",
            CallExpr call => $"{DescribeExpr(call.Target)}(...)",
            TextLiteral => "text",
            NumberLiteral number => number.Raw,
            YesLiteral => "yes",
            NoLiteral => "no",
            NoneLiteral => "none",
            _ => "value",
        };
    }

    /// <summary>Deep equality for <c>is</c>/<c>is not</c> (different kinds are never equal).</summary>
    public static bool EqualsDeep(BeaconValue left, BeaconValue right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Kind != right.Kind)
            return false;

        return (left, right) switch
        {
            (BeaconTextValue a, BeaconTextValue b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),
            (BeaconNumberValue a, BeaconNumberValue b) => a.Value == b.Value,
            (BeaconYesNoValue a, BeaconYesNoValue b) => a.Value == b.Value,
            (BeaconNoneValue, BeaconNoneValue) => true,
            (BeaconListValue a, BeaconListValue b) => a.Items.Count == b.Items.Count
                && a.Items.Zip(b.Items).All(pair => EqualsDeep(pair.First, pair.Second)),
            (BeaconMapValue a, BeaconMapValue b) => a.Entries.Count == b.Entries.Count
                && a.Entries.All(kv => b.Entries.TryGetValue(kv.Key, out BeaconValue? other) && other is not null && EqualsDeep(kv.Value, other)),
            _ => false,
        };
    }

    private static string? DidYouMean(string word, IEnumerable<string> candidates)
        => BeaconTextDistance.SuggestNearest(word, candidates);

    #endregion
    #region top-level + handler entry points

    /// <summary>
    /// Registers functions (capturing <c># desc:</c> lines) and executes bare top-level statements.
    /// <c>on</c>/<c>every</c>/<c>command</c> blocks run through the scheduler and event runtimes and are skipped here.
    /// Never executes when the caller found Lint errors; throws <c>B0001</c> on nested decls.
    /// </summary>
    public async Task<BeaconRunResult> RunTopLevelAsync(
        BeaconScript script, IReadOnlyList<LexedComment>? comments = null, CancellationToken ct = default, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        BeginDispatch("top-level", seed);
        RegisterFunctions(script, comments);
        _externs = new BeaconExternTable(script);
        var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);
        BeaconRunResult? importFailure = await LoadImportsAsync(script, verbs, warnings, ct).ConfigureAwait(false);
        if (importFailure is not null)
        {
            var merged = new List<BeaconDiagnostic>(warnings);
            merged.AddRange(importFailure.Diagnostics);
            return importFailure with { Diagnostics = merged };
        }
        try
        {
            foreach (BeaconTopDecl decl in script.Decls)
            {
                ct.ThrowIfCancellationRequested();
                if (decl is TopStatement top)
                    await ExecuteStatementAsync(top.Statement, Globals, verbs, 0, isInLoop: false, isInHandler: false, ct).ConfigureAwait(false);
                else if (decl is ExportValueDecl exported)
                {
                    BeaconValue value = await EvaluateExprAsync(exported.Value, Globals, verbs, ct).ConfigureAwait(false);
                    Globals.Define(exported.Name, value);
                    _valueExports[exported.Name] = (value, exported.NameSpan.Origin);
                }
            }

            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconReturnException ret)
        {
            return new BeaconRunResult(true, ret.Value, warnings, output, passthrough, echo, null);
        }
        catch (BeaconBreakException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconStopEventException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null, EventSuppressed: true);
        }
        catch (BeaconContinueException ex)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse, BeaconSeverity.Error,
                "I expected 'skip' inside a loop, but found it outside one.",
                new SourceSpan(FileName, 1, 1, 0),
                "Move 'skip' inside repeat/while/for each, or use 'stop' to exit a handler.");
            _ = ex;
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
        catch (BeaconRuntimeException ex)
        {
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, ex.ToDiagnostic());
        }
        catch (BeaconFuelExhaustedException ex)
        {
            // Raw fuel breaches from scheduler-bound paths (which spend the shared seam without interpreter context) still land here; give them the same B4007 Elm shape.
            var diagnostic = FuelDiagnostic("top-level", ex);
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
    }

    /// <summary>Builds the B4007 Elm diagnostic for a raw fuel breach with whatever context survives.</summary>
    private BeaconDiagnostic FuelDiagnostic(string handler, BeaconFuelExhaustedException ex)
    {
        BeaconRuntimeException shaped = BeaconErrors.BudgetExhausted(
            handler,
            new SourceSpan(FileName, 1, 1, 0),
            BeaconDiagnostics.SummarizeScope(Globals),
            Budget.CallStack,
            Budget.Seed,
            ex.Used,
            ex.Limit);
        return shaped.ToDiagnostic();
    }

    private BeaconVerbs NewDispatchVerbs(
        out List<BeaconPassthroughLog> passthrough,
        out List<string> echo,
        out List<string> output,
        out List<BeaconDiagnostic> warnings)
    {
        passthrough = new List<BeaconPassthroughLog>();
        echo = new List<string>();
        output = new List<string>();
        warnings = new List<BeaconDiagnostic>();
        return new BeaconVerbs(Host, ScriptId, passthrough, echo, output, warnings, ChatBucket, Budget.NoteWaitCompleted);
    }

    private static BeaconScope NewFunctionFrame(
        BeaconScope globals, IReadOnlyList<string> paramNames, IReadOnlyList<BeaconValue> args)
    {
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(paramNames);
        ArgumentNullException.ThrowIfNull(args);
        var frame = globals.CreateChild();
        frame.IsFunctionBoundary = true;
        for (int i = 0; i < paramNames.Count; i++)
            frame.Define(paramNames[i], args[i]);

        return frame;
    }

    private static BeaconValue ToContainerValue(BeaconContainerInfo container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["window"] = BeaconValue.Number(container.WindowId),
            ["title"] = container.Title is null ? BeaconValue.None : BeaconValue.Text(container.Title),
            ["kind"] = container.Kind is null ? BeaconValue.None : BeaconValue.Text(container.Kind),
            ["slots"] = BeaconValue.List(container.Slots.Select(BeaconMatchers.ToSlotMap).ToList<BeaconValue>()),
        });
    }

    private static int RequireSlotNumber(BeaconValue value, SourceSpan origin, string message, string suggestion)
    {
        if (value is not BeaconNumberValue slot || slot.Value < 0 || slot.Value != Math.Floor(slot.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                message,
                origin,
                suggestion);
        }

        return (int)slot.Value;
    }

    /// <summary>
    /// Executes one <c>on</c> handler body with an immutable event snapshot.
    /// Event fields shadow globals; <c>event</c> holds the full map.
    /// A false <c>when</c> filter skips quietly.
    /// </summary>
    public async Task<BeaconRunResult> InvokeHandlerAsync(
        OnBlock block, IReadOnlyDictionary<string, BeaconValue> eventFields, CancellationToken ct = default, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(eventFields);
        BeginDispatch($"on {block.EventName}", seed);
        var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);

        var handlerScope = Globals.CreateChild();
        handlerScope.IsEventBoundary = true;
        foreach (KeyValuePair<string, BeaconValue> kv in eventFields)
            handlerScope.Define(kv.Key, kv.Value);

        handlerScope.Define("event", BeaconValue.Map(new Dictionary<string, BeaconValue>(eventFields, StringComparer.Ordinal)));
        var bodyScope = handlerScope.CreateChild();
        try
        {
            if (block.When is not null)
            {
                BeaconValue filter = await EvaluateExprAsync(block.When, bodyScope, verbs, ct).ConfigureAwait(false);
                RequireYesNo(filter, block.When, "when");
                if (filter is BeaconYesNoValue yesNo && !yesNo.Value)
                    return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
            }

            await ExecuteBlockAsync(block.Body, bodyScope, verbs, 0, isInHandler: true, ct).ConfigureAwait(false);
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconReturnException ret)
        {
            return new BeaconRunResult(true, ret.Value, warnings, output, passthrough, echo, null);
        }
        catch (BeaconBreakException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconStopEventException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null, EventSuppressed: true);
        }
        catch (BeaconContinueException)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse, BeaconSeverity.Error,
                "I expected 'skip' inside a loop, but found it outside one.",
                block.Span.Origin,
                "Move 'skip' inside repeat/while/for each.");
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
        catch (BeaconRuntimeException ex)
        {
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, ex.ToDiagnostic());
        }
        catch (BeaconFuelExhaustedException ex)
        {
            var diagnostic = FuelDiagnostic($"on {block.EventName}", ex);
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
    }

    /// <summary>
    /// Executes one <c>every</c> timer body with no event snapshot (globals plus builtins only).
    /// The engine calls this once per due timer from <c>TickEveryAsync</c>; missed intervals coalesce upstream in <see cref="BeaconLifecycle.EveryTick"/>, so this runs at most once per tick per timer.
    /// <c>stop event</c> inside an <c>every</c> body is a no-op success (there is no triggering message to suppress).
    /// </summary>
    public async Task<BeaconRunResult> InvokeEveryAsync(
        EveryBlock block, CancellationToken ct = default, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        BeginDispatch("every", seed);
        var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);

        BeaconScope bodyScope = Globals.CreateChild();
        try
        {
            await ExecuteBlockAsync(block.Body, bodyScope, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconReturnException ret)
        {
            return new BeaconRunResult(true, ret.Value, warnings, output, passthrough, echo, null);
        }
        catch (BeaconBreakException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconStopEventException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null, EventSuppressed: false);
        }
        catch (BeaconContinueException)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse, BeaconSeverity.Error,
                "I expected 'skip' inside a loop, but found it outside one.",
                block.Span.Origin,
                "Move 'skip' inside repeat/while/for each.");
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
        catch (BeaconRuntimeException ex)
        {
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, ex.ToDiagnostic());
        }
        catch (BeaconFuelExhaustedException ex)
        {
            var diagnostic = FuelDiagnostic("every", ex);
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
    }

    /// <summary>
    /// Executes one due one-shot <c>in</c> block body with no event snapshot (globals plus builtins only).
    /// The engine calls this once from <c>TickOnceAsync</c>; a fired one-shot never refires, and pending one-shots cancel on reconnect like pending waits.
    /// </summary>
    public async Task<BeaconRunResult> InvokeOnceAsync(
        OnceBlock block, CancellationToken ct = default, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        BeginDispatch("in", seed);
        var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);

        BeaconScope bodyScope = Globals.CreateChild();
        try
        {
            await ExecuteBlockAsync(block.Body, bodyScope, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconReturnException ret)
        {
            return new BeaconRunResult(true, ret.Value, warnings, output, passthrough, echo, null);
        }
        catch (BeaconBreakException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
        }
        catch (BeaconStopEventException)
        {
            return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null, EventSuppressed: false);
        }
        catch (BeaconContinueException)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse, BeaconSeverity.Error,
                "I expected 'skip' inside a loop, but found it outside one.",
                block.Span.Origin,
                "Move 'skip' inside repeat/while/for each.");
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
        catch (BeaconRuntimeException ex)
        {
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, ex.ToDiagnostic());
        }
        catch (BeaconFuelExhaustedException ex)
        {
            var diagnostic = FuelDiagnostic("in", ex);
            return new BeaconRunResult(false, null, warnings, output, passthrough, echo, diagnostic);
        }
    }

    private void RegisterFunctions(BeaconScript script, IReadOnlyList<LexedComment>? comments)
    {
        Dictionary<int, string> descByLine = ParseDescComments(comments); foreach (BeaconTopDecl decl in script.Decls)
        {
            if (decl is FunctionDef function)
            {
                descByLine.TryGetValue(function.NameSpan.Origin.Line - 1, out string? desc);
                _functions[function.Name] = new BeaconFunctionInfo(
                    function.Name,
                    function.Params.Select(p => p.Name).ToList(),
                    function.Body,
                    function.IsExport,
                    desc,
                    function.Span.Origin);
                if (desc is not null)
                    _descriptions[function.Name] = desc;
            }
        }
    }

    /// <summary>
    /// Loads <c>import</c> modules for a freshly registered source: resolves each import against the importing file, registers the module functions under <c>alias.name</c>, and runs the module top-level <c>set</c> statements into this script globals (so library constants work).
    /// Modules contribute functions and constants only: their <c>on</c>/<c>every</c>/ <c>command</c> blocks never register, and nested imports merge under the same alias in file order.
    /// A resolution, read, parse, or cycle failure returns the failing run result (loading fails closed); null means clean.
    /// </summary>
    private async Task<BeaconRunResult?> LoadImportsAsync(
        BeaconScript script, BeaconVerbs verbs, List<BeaconDiagnostic> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(verbs);
        ArgumentNullException.ThrowIfNull(warnings);
        if (script.Imports.Count == 0)
            return null;

        if (ModuleResolver is null)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.ImportNotFound,
                BeaconSeverity.Error,
                $"I expected a module resolver for 'import', but this interpreter has none attached.",
                script.Imports[0].PathSpan.Origin,
                "Load the script through the engine, which resolves imports against the importing file.");
            return new BeaconRunResult(false, null, [diagnostic], [], [], [], diagnostic);
        }

        var chain = new List<string> { SafeFullPath(FileName) };
        foreach (BeaconImport import in script.Imports)
        {
            ct.ThrowIfCancellationRequested();
            BeaconScript module;
            try
            {
                module = await ModuleResolver.ResolveAsync(FileName, import, chain, ct).ConfigureAwait(false);
            }
            catch (BeaconImportException ex)
            {
                var diagnostic = new BeaconDiagnostic(
                    ex.Code, BeaconSeverity.Error, ex.Message, import.PathSpan.Origin, ex.Suggestion);
                return new BeaconRunResult(false, null, [diagnostic], [], [], [], diagnostic);
            }

            var table = new Dictionary<string, BeaconFunctionInfo>(StringComparer.Ordinal);
            foreach (BeaconTopDecl decl in module.Decls)
            {
                if (decl is FunctionDef function)
                {
                    var info = new BeaconFunctionInfo(
                        function.Name,
                        function.Params.Select(p => p.Name).ToList(),
                        function.Body,
                        function.IsExport,
                        null,
                        function.Span.Origin);
                    _functions[$"{import.Alias}.{function.Name}"] = info;
                    table[function.Name] = info;
                }
            }

            _importAliases[import.Alias] = table;

            foreach (BeaconTopDecl decl in module.Decls)
            {
                if (decl is TopStatement top && top.Statement is SetStmt)
                {
                    Spend(null, top.Span);
                    try
                    {
                        await ExecuteStatementAsync(top.Statement, Globals, verbs, 0, isInLoop: false, isInHandler: false, ct).ConfigureAwait(false);
                    }
                    catch (BeaconRuntimeException ex)
                    {
                        var diagnostic = new BeaconDiagnostic(ex.Code, BeaconSeverity.Error, ex.Message, ex.Span, ex.Suggestion);
                        return new BeaconRunResult(false, null, [diagnostic], [], [], [], diagnostic);
                    }
                }
            }
        }

        return null;
    }

    private static string SafeFullPath(string fileName)
    {
        try
        {
            return Path.GetFullPath(fileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return fileName;
        }
    }

    /// <summary>
    /// Runs an exported function inside the CURRENT dispatch (caller fuel, caller RNG, caller budget): the <c>call "script.func"()</c> path.
    /// Missing-target and arity failures are catchable errors naming script, function, and caller line.
    /// The callee wall window is re-armed on entry (its own dispatch may be long over); fuel stays shared, so the callee still spends the caller fuel.
    /// </summary>
    public async Task<BeaconValue> InvokeExportAsync(
        BeaconExportedFunction export, IReadOnlyList<BeaconValue> args,
        BeaconScope scope, BeaconVerbs verbs, SourceSpan callerSpan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(verbs);
        ArgumentNullException.ThrowIfNull(callerSpan);
        if (args.Count != export.Params.Count)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected {export.Params.Count} argument(s) for '{export.ScriptId}.{export.Name}', but found {args.Count}.",
                callerSpan.Origin,
                $"Write call \"{export.ScriptId}.{export.Name}\"({string.Join(", ", export.Params)}).");
        }

        Spend(scope, callerSpan);
        export.Interpreter.Budget.NoteWaitCompleted();
        using (Budget.TrackCall($"{export.ScriptId}.{export.Name}", callerSpan))
        {
            BeaconScope frame = NewFunctionFrame(export.Interpreter.Globals, export.Params, args);

            try
            {
                await export.Interpreter.ExecuteBlockAsync(export.Body, frame, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                return BeaconValue.None;
            }
            catch (BeaconReturnException ret)
            {
                return ret.Value;
            }
        }
    }

    /// <summary>
    /// Runs an exported function as a FRESH dispatch from the C# side (<c>IBeaconHost.CallFunctionAsync</c>): fresh seed, reset fuel window, own handler name.
    /// </summary>
    public async Task<BeaconValue> InvokeExportFromHostAsync(
        BeaconExportedFunction export, IReadOnlyList<BeaconValue> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(args);
        if (!ReferenceEquals(export.Interpreter, this))
            throw new InvalidOperationException("An export runs on its owning interpreter.");

        if (args.Count != export.Params.Count)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected {export.Params.Count} argument(s) for '{export.ScriptId}.{export.Name}', but found {args.Count}.",
                export.Span.Origin,
                $"Pass {export.Params.Count} argument(s) in script order.");
        }

        BeginDispatch($"export {export.Name}");
        var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);
        using (Budget.TrackCall($"{export.ScriptId}.{export.Name}", export.Span))
        {
            BeaconScope frame = NewFunctionFrame(Globals, export.Params, args);

            try
            {
                await ExecuteBlockAsync(export.Body, frame, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                return BeaconValue.None;
            }
            catch (BeaconReturnException ret)
            {
                return ret.Value;
            }
        }
    }

    /// <summary>
    /// Runs a <c>command</c> block with <paramref name="args"/> bound (fresh dispatch, handler name <c>command /name</c>).
    /// Returns the full run result for the Brigadier wrapper to render.
    /// </summary>
    public async Task<BeaconRunResult> InvokeCommandAsync(
        CommandBlock block, IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(args);
        BeginDispatch($"command {block.Pattern}");
        IReadOnlyDictionary<string, string>? saved = CommandArgs;
        var bound = new Dictionary<string, string>(args, StringComparer.Ordinal);
        CommandArgs = bound;
        try
        {
            var verbs = NewDispatchVerbs(out List<BeaconPassthroughLog> passthrough, out List<string> echo, out List<string> output, out List<BeaconDiagnostic> warnings);
            BeaconScope bodyScope = Globals.CreateChild();
            try
            {
                await ExecuteBlockAsync(block.Body, bodyScope, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                return new BeaconRunResult(true, null, warnings, output, passthrough, echo, null);
            }
            catch (BeaconReturnException ret)
            {
                return new BeaconRunResult(true, ret.Value, warnings, output, passthrough, echo, null);
            }
            catch (BeaconRuntimeException ex)
            {
                return new BeaconRunResult(false, null, warnings, output, passthrough, echo, ex.ToDiagnostic());
            }
            catch (BeaconFuelExhaustedException ex)
            {
                return new BeaconRunResult(false, null, warnings, output, passthrough, echo, FuelDiagnostic($"command {block.Pattern}", ex));
            }
        }
        finally
        {
            CommandArgs = saved;
        }
    }

    private static Dictionary<int, string> ParseDescComments(IReadOnlyList<LexedComment>? comments)
    {
        var result = new Dictionary<int, string>();
        if (comments is null)
            return result;

        foreach (LexedComment comment in comments)
        {
            string text = comment.Text.Trim();
            if (!BeaconCommentDocs.TryStripMarker(text, out string stripped))
                continue;

            if (BeaconCommentDocs.TryGetTag(stripped, "desc:", out string desc))
                result[comment.Span.Line] = desc;
        }

        return result;
    }

    #endregion
    #region statements

    private async Task ExecuteBlockAsync(
        BeaconBlock block, BeaconScope scope, BeaconVerbs verbs, int depth, bool isInHandler, CancellationToken ct)
    {
        foreach (BeaconStatement statement in block.Statements)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteStatementAsync(statement, scope, verbs, depth, isInLoop: false, isInHandler, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteStatementAsync(
        BeaconStatement statement, BeaconScope scope, BeaconVerbs verbs, int depth,
        bool isInLoop, bool isInHandler, CancellationToken ct)
    {
        using IDisposable? traceScope = Tracer?.Invoke(statement, scope, _currentHandler, depth);
        switch (statement)
        {
            case SetStmt set:
                {
                    BeaconValue value = await EvaluateExprAsync(set.Value, scope, verbs, ct).ConfigureAwait(false);
                    await AssignTargetAsync(set.Target, value, scope, verbs, ct).ConfigureAwait(false);
                    break;
                }

            case SayStmt say:
                {
                    Spend(scope, say.Span);
                    BeaconValue message = await EvaluateExprAsync(say.Message, scope, verbs, ct).ConfigureAwait(false);
                    await verbs.SayAsync(message, say.Span, ct).ConfigureAwait(false);
                    break;
                }

            case WhisperStmt whisper:
                {
                    Spend(scope, whisper.Span);
                    BeaconValue player = await EvaluateExprAsync(whisper.Player, scope, verbs, ct).ConfigureAwait(false);
                    BeaconValue message = await EvaluateExprAsync(whisper.Message, scope, verbs, ct).ConfigureAwait(false);
                    await verbs.WhisperAsync(player, message, whisper.Span, ct).ConfigureAwait(false);
                    break;
                }

            case ServerStmt server:
                {
                    Spend(scope, server.Span);
                    BeaconValue command = await EvaluateExprAsync(server.Command, scope, verbs, ct).ConfigureAwait(false);
                    await verbs.ServerAsync(command, server.Span, ct).ConfigureAwait(false);
                    break;
                }

            case DisconnectStmt disconnect:
                {
                    Spend(scope, disconnect.Span);
                    BeaconValue? reason = disconnect.Reason is null
                        ? null
                        : await EvaluateExprAsync(disconnect.Reason, scope, verbs, ct).ConfigureAwait(false);
                    await verbs.DisconnectAsync(reason, disconnect.Span, ct).ConfigureAwait(false);
                    break;
                }

            case ShowStmt show:
                {
                    BeaconValue message = await EvaluateExprAsync(show.Message, scope, verbs, ct).ConfigureAwait(false);
                    verbs.Show(message, show.Span);
                    break;
                }

            case WaitStmt wait:
                {
                    BeaconValue countValue = await EvaluateExprAsync(wait.Count, scope, verbs, ct).ConfigureAwait(false);
                    double count = RequireWaitCount(countValue, wait);
                    TimeSpan requested = UnitToTimeSpan(count, wait.Unit, wait);
                    if (Scheduler is not null)
                        // Scheduler-bound waits floor at the 100 ms quantum, spend one step, enforce the 32-sleep cap (catchable B4006), and abort on session drop.
                        await Scheduler.WaitAsync(requested, ct).ConfigureAwait(false);
                    else
                    {
                        Spend(scope, wait.Span);
                        TimeSpan floored = requested < TimeSpan.FromMilliseconds(100)
                            ? TimeSpan.FromMilliseconds(100)
                            : requested;
                        await Clock.Delay(floored, ct).ConfigureAwait(false);
                    }

                    Budget.NoteWaitCompleted();
                    break;
                }

            case StopStmt stop:
                {
                    if (stop.HasEvent)
                        throw new BeaconStopEventException();

                    throw new BeaconBreakException();
                }

            case SkipStmt skip:
                {
                    if (skip.Kind == BeaconSkipKind.Break)
                        throw new BeaconBreakException();

                    throw new BeaconContinueException();
                }

            case ReturnStmt ret:
                {
                    BeaconValue value = ret.Value is null
                        ? BeaconValue.None
                        : await EvaluateExprAsync(ret.Value, scope, verbs, ct).ConfigureAwait(false);
                    throw new BeaconReturnException(value);
                }

            case SaveStmt save:
                {
                    BeaconValue keyValue = await EvaluateExprAsync(save.Key, scope, verbs, ct).ConfigureAwait(false);
                    BeaconValue value = await EvaluateExprAsync(save.Value, scope, verbs, ct).ConfigureAwait(false);
                    if (keyValue is not BeaconTextValue keyText)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for the save key, but found {DescribeValueKind(keyValue)}.",
                            save.Span.Origin,
                            "Write save \"warns\" to warns.");
                    }

                    _saved[keyText.Value] = value;
                    break;
                }

            case LockStmt lockStmt:
                {
                    // The persisted store serializes read-modify-write; here the body runs directly.
                    await ExecuteBlockAsync(lockStmt.Body, scope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                    break;
                }

            case StartStmt start:
                await ExecuteStartAsync(start, scope, verbs, ct).ConfigureAwait(false);
                break;

            case AwaitStmt awaitStmt:
                await ExecuteAwaitAsync(awaitStmt, scope, verbs, ct).ConfigureAwait(false);
                break;

            case CancelTaskStmt cancel:
                await ExecuteCancelTaskAsync(cancel, scope, verbs, ct).ConfigureAwait(false);
                break;

            case IfStmt ifStmt:
                {
                    bool ran = false;
                    foreach (BeaconIfBranch branch in ifStmt.Branches)
                    {
                        BeaconValue cond = await EvaluateExprAsync(branch.Cond, scope, verbs, ct).ConfigureAwait(false);
                        RequireYesNo(cond, branch.Cond, "if");
                        if (cond is BeaconYesNoValue yesNo && yesNo.Value)
                        {
                            await ExecuteBlockAsync(branch.Body, scope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                            ran = true;
                            break;
                        }
                    }

                    if (!ran && ifStmt.ElseBody is not null)
                        await ExecuteBlockAsync(ifStmt.ElseBody, scope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);

                    break;
                }

            case WhileStmt whileStmt:
                {
                    var bodyScope = scope.CreateChild();
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        BeaconValue cond = await EvaluateExprAsync(whileStmt.Cond, scope, verbs, ct).ConfigureAwait(false);
                        RequireYesNo(cond, whileStmt.Cond, "while");
                        if (cond is BeaconYesNoValue yesNo && !yesNo.Value)
                            break;

                        Spend(scope, whileStmt.Span);
                        try
                        {
                            await ExecuteBlockAsync(whileStmt.Body, bodyScope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                        }
                        catch (BeaconBreakException)
                        {
                            break;
                        }
                        catch (BeaconContinueException)
                        {
                            continue;
                        }
                    }

                    break;
                }

            case RepeatStmt repeat:
                {
                    BeaconValue countValue = await EvaluateExprAsync(repeat.Count, scope, verbs, ct).ConfigureAwait(false);
                    long times = RequireRepeatCount(countValue, repeat);
                    var bodyScope = scope.CreateChild();
                    for (long i = 0; i < times; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Spend(scope, repeat.Span);
                        try
                        {
                            await ExecuteBlockAsync(repeat.Body, bodyScope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                        }
                        catch (BeaconBreakException)
                        {
                            break;
                        }
                        catch (BeaconContinueException)
                        {
                            continue;
                        }
                    }

                    break;
                }

            case ForStmt forStmt:
                {
                    BeaconValue iterable = await EvaluateExprAsync(forStmt.Iterable, scope, verbs, ct).ConfigureAwait(false);
                    List<BeaconValue> items = RequireIterable(iterable, forStmt);
                    var bodyScope = scope.CreateChild();
                    foreach (BeaconValue item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        Spend(scope, forStmt.Span);
                        BeaconScope iteration = bodyScope.CreateChild();
                        iteration.Define(forStmt.Var, item);
                        try
                        {
                            await ExecuteBlockAsync(forStmt.Body, iteration, verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                        }
                        catch (BeaconBreakException)
                        {
                            break;
                        }
                        catch (BeaconContinueException)
                        {
                            continue;
                        }
                    }

                    break;
                }

            case TryStmt tryStmt:
                {
                    try
                    {
                        try
                        {
                            await ExecuteBlockAsync(tryStmt.Body, scope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                        }
                        catch (BeaconRuntimeException ex) when (ex.IsCatchable)
                        {
                            var catchScope = scope.CreateChild();
                            catchScope.Define(tryStmt.CatchVar, ex.ToErrorValue());
                            await ExecuteBlockAsync(tryStmt.CatchBody, catchScope, verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (tryStmt.FinallyBody is not null)
                            await ExecuteBlockAsync(tryStmt.FinallyBody, scope.CreateChild(), verbs, depth + 1, isInHandler, ct).ConfigureAwait(false);
                    }

                    break;
                }

            case ExprStmt exprStmt:
                {
                    BeaconValue value = await EvaluateExprAsync(exprStmt.Expr, scope, verbs, ct).ConfigureAwait(false);
                    if (exprStmt.Expr is CallExpr call && call.Target is IdentExpr ident)
                        _discarded.Add((ScriptId, exprStmt.Span.Origin.Line, ident.Name));

                    _ = value;
                    break;
                }

            case NestedDeclStmt nested:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.Parse,
                    "I expected a statement, but found a block nested inside another block. Blocks on/every/function/command live at the top level only.",
                    nested.Span.Origin,
                    "Move this block to the top level of the file.",
                    isCatchable: false);

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.Parse,
                    $"I expected a runnable statement, but found '{statement.GetType().Name}'.",
                    statement.Span.Origin,
                    "Fix lint errors first, then run again.",
                    isCatchable: false);
        }

        _ = isInLoop;
    }

    private static BeaconRuntimeException SchedulerMissing(SourceSpan span, string what, string suggestion) => new(
        BeaconDiagnosticCodes.UnknownName,
        $"I expected a scheduler for '{what}', but this interpreter has none attached. The script parsed, it just cannot run background tasks here.",
        span.Origin,
        suggestion);

    private async Task ExecuteStartAsync(StartStmt start, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        BeaconScheduler? scheduler = Scheduler;
        if (scheduler is null)
            throw SchedulerMissing(start.Span, "start", "Remove 'start patrol()' until the scheduler lands.");

        (string Name, IReadOnlyList<BeaconValue> Args) target =
            await ResolveTaskTargetAsync(start.Task, scope, verbs, ct).ConfigureAwait(false);
        if (!_functions.TryGetValue(target.Name, out BeaconFunctionInfo? function) || function is null)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Unknown task '{target.Name}'.",
                start.Span.Origin,
                SuggestFix(target.Name, scope));
        }

        if (target.Args.Count != function.Params.Count)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Task '{target.Name}' expects {function.Params.Count} argument(s) but got {target.Args.Count}.",
                start.Span.Origin,
                $"Write start {target.Name}({string.Join(", ", function.Params)}).");
        }

        var sinks = new TaskSinks();
        BeaconTask task = scheduler.StartTask(
            target.Name, taskCtx => RunTaskBodyAsync(function, target.Args, sinks, taskCtx, ct));
        lock (_taskSinksGate)
            _taskSinks[task.Id] = sinks;
    }

    private async Task<BeaconValue?> RunTaskBodyAsync(
        BeaconFunctionInfo function,
        IReadOnlyList<BeaconValue> args,
        TaskSinks sinks,
        BeaconTaskContext taskCtx,
        CancellationToken dispatchCt)
    {
        using (Budget.EnterTask(function.Name))
        {
            BeaconScope frame = NewFunctionFrame(Globals, function.Params, args);

            var taskVerbs = new BeaconVerbs(
                Host, ScriptId, sinks.Passthrough, sinks.Echo, sinks.Output, sinks.Warnings, ChatBucket, Budget.NoteWaitCompleted);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(taskCtx.CancellationToken, dispatchCt);
            try
            {
                await ExecuteBlockAsync(function.Body, frame, taskVerbs, 0, isInHandler: false, linked.Token).ConfigureAwait(false);
                return BeaconValue.None;
            }
            catch (BeaconReturnException ret)
            {
                return ret.Value;
            }
        }
    }

    private async Task ExecuteAwaitAsync(AwaitStmt awaitStmt, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        BeaconScheduler? scheduler = Scheduler;
        if (scheduler is null)
            throw SchedulerMissing(awaitStmt.Span, "await", "Remove 'await id' until the scheduler lands.");

        BeaconValue idValue = await EvaluateExprAsync(awaitStmt.Task, scope, verbs, ct).ConfigureAwait(false);
        long taskId = RequireTaskId(idValue, awaitStmt.Span);
        BeaconValue? result;
        try
        {
            result = await scheduler.AwaitTaskAsync(taskId, ct).ConfigureAwait(false);
        }
        finally
        {
            // Joining is yielding: the join may outlast the wall window.
            Budget.NoteWaitCompleted();
        }

        _ = result;

        // The task settled: merge its diagnostic sinks into this dispatch for audit continuity.
        TaskSinks? sinks;
        lock (_taskSinksGate)
        {
            _taskSinks.TryGetValue(taskId, out sinks);
            _taskSinks.Remove(taskId);
        }

        if (sinks is not null)
            verbs.AbsorbTaskSinks(sinks.Passthrough, sinks.Echo, sinks.Output, sinks.Warnings);
    }

    private async Task ExecuteCancelTaskAsync(CancelTaskStmt cancel, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        BeaconScheduler? scheduler = Scheduler;
        if (scheduler is null)
            throw SchedulerMissing(cancel.Span, "cancel task", "Remove 'cancel task id' until the scheduler lands.");

        BeaconValue idValue = await EvaluateExprAsync(cancel.Task, scope, verbs, ct).ConfigureAwait(false);
        long taskId = RequireTaskId(idValue, cancel.Span);
        if (!scheduler.CancelTask(taskId, $"cancel task by {ScriptId}"))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected a running task id for 'cancel task {taskId}', but no running task has that id. "
                + "Task ids come from 'start' and vanish once their task settles.",
                cancel.Span.Origin,
                "List live tasks with tasks() first.");
        }
    }

    private async Task<(string Name, IReadOnlyList<BeaconValue> Args)> ResolveTaskTargetAsync(
        BeaconExpr task, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        switch (task)
        {
            case CallExpr call when call.Target is IdentExpr ident:
                {
                    var args = new List<BeaconValue>(call.Args.Count);
                    foreach (BeaconExpr arg in call.Args)
                        args.Add(await EvaluateExprAsync(arg, scope, verbs, ct).ConfigureAwait(false));

                    return (ident.Name, args);
                }

            case IdentExpr ident:
                return (ident.Name, []);

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    "I expected a task call after 'start', but found something else.",
                    task.Span.Origin,
                    "Write start patrol().");
        }
    }

    private static long RequireTaskId(BeaconValue value, SourceSpan span)
    {
        if (value is BeaconNumberValue number
            && !double.IsNaN(number.Value) && !double.IsInfinity(number.Value)
            && number.Value == Math.Floor(number.Value) && number.Value >= 0)
            return (long)number.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a task id number, but found {DescribeValueKind(value)}.",
            span.Origin,
            "Write await id where id came from tasks()[0].id.");
    }

    private async Task AssignTargetAsync(
        BeaconTarget target, BeaconValue value, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(verbs);
        // Shared is a writable stub here (cross-script quotas and locks live in the shared store).
        if (string.Equals(target.Base, "shared", StringComparison.Ordinal))
        {
            BeaconValue sharedCurrent = BeaconValue.Map(new Dictionary<string, BeaconValue>(_shared, StringComparer.Ordinal));
            if (target.Parts.Count == 0)
            {
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    "I expected shared[\"key\"] for shared writes, but found a bare 'set shared'.",
                    target.Span.Origin,
                    "Write shared[\"shop.price\"] is set or set shared[\"k\"] to v.");
            }

            BeaconValue updatedShared = await AssignPartsAsync(sharedCurrent, target.Parts, 0, value, target.Span, scope, verbs, ct).ConfigureAwait(false);
            _shared.Clear();
            if (updatedShared is BeaconMapValue sharedMap)
            {
                foreach (KeyValuePair<string, BeaconValue> kv in sharedMap.Entries)
                    _shared[kv.Key] = kv.Value;
            }

            return;
        }

        // event, server, me, time and similar reserved names are read-only views.
        // Bare or member writes fail with a clear message instead of an unknown-name error or a snapshot mutation.
        if (target.Base is "event" or "server" or "me" or "time" or "online_players"
            or "beacon" or "items" or "effects" or "enchants" or "game" or "settings")
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected a variable for 'set {target.Base}', but '{target.Base}' is a read-only view.",
                target.Span.Origin,
                target.Base == "event"
                    ? "Event snapshots are immutable; copy a field first (set m to event.message)."
                    : $"Read {target.Base} but write another name (builtins like me, server cannot be overwritten).");
        }

        // Plugin variable namespaces are read-only maps per read: writing them (bare or member) refuses readably instead of shadowing the provider or mutating a snapshot.
        if (!scope.TryLookup(target.Base, out _) && Bridge is not null
            && Bridge.TryGetVariable(target.Base, out BeaconVariableRegistration? owned) && owned is not null)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected a variable for 'set {target.Base}', but '{target.Base}' is a read-only plugin variable from '{owned.OwnerPluginId}'.",
                target.Span.Origin,
                $"Read {target.Base} but write another name (plugin variables cannot be overwritten).");
        }

        // The vars.beacon.* bridge: only vars.beacon.<name> writes through to the shared VariableStore (as beacon_<name>); a bare `set vars` or any other segment refuses.
        if (string.Equals(target.Base, "vars", StringComparison.Ordinal))
        {
            if (target.Parts.Count == 2
                && target.Parts[0] is TargetMember first
                && string.Equals(first.Name, "beacon", StringComparison.Ordinal)
                && target.Parts[1] is TargetMember second
                && second.Name.Length > 0)
            {
                if (!BeaconVarsBridge.TryWrite(Variables, second.Name, value, out string? wireError))
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"I expected a wired variable store for 'set vars.beacon.{second.Name}', but {wireError}.",
                        target.Span.Origin,
                        "Load the script through the engine, which binds the client variable store.");
                }

                return;
            }

            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected 'set vars.beacon.<name> to ...', but found 'set {target.Base}{DescribeTargetParts(target)}'. Only vars.beacon.* bridges to the shared store.",
                target.Span.Origin,
                "Write set vars.beacon.coins to 5 (shared as %beacon_coins%).");
        }

        if (IsBuiltinName(target.Base) && !scope.TryLookup(target.Base, out _))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected a variable for 'set {target.Base}', but '{target.Base}' is a read-only builtin.",
                target.Span.Origin,
                "Pick another name (builtins like text, me, server cannot be overwritten).");
        }

        // Bare `set x to ...`: new names define locally; existing names update the owner, except across barriers (function frames shadow globals; event field names shadow).
        if (target.Parts.Count == 0)
        {
            if (!scope.TryLookupWithOwner(target.Base, out BeaconValue? existing, out BeaconScope? existingOwner) || existing is null || existingOwner is null)
            {
                scope.Define(target.Base, value);
                return;
            }

            if (existingOwner.IsEventBoundary)
            {
                scope.Define(target.Base, value);
                return;
            }

            BeaconScope? frame = FindFunctionFrame(scope);
            if (frame is not null && !IsInside(existingOwner, frame))
            {
                frame.Define(target.Base, value);
                return;
            }

            existingOwner.Define(target.Base, value);
            return;
        }

        if (!scope.TryLookupWithOwner(target.Base, out BeaconValue? current, out BeaconScope? owner) || current is null || owner is null)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Unknown name '{target.Base}'.",
                target.BaseSpan.Origin,
                SuggestFix(target.Base, scope));
        }

        BeaconValue updated = await AssignPartsAsync(current, target.Parts, 0, value, target.Span, scope, verbs, ct).ConfigureAwait(false);
        owner.Define(target.Base, updated);
    }

    private static BeaconScope? FindFunctionFrame(BeaconScope scope)
    {
        for (BeaconScope? current = scope; current is not null; current = current.Parent)
        {
            if (current.IsFunctionBoundary)
                return current;
        }

        return null;
    }

    private static bool IsInside(BeaconScope candidate, BeaconScope ancestor)
    {
        for (BeaconScope? current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private async Task<BeaconValue> AssignPartsAsync(
        BeaconValue current, IReadOnlyList<BeaconTargetPart> parts, int index, BeaconValue value,
        SourceSpan span, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        BeaconTargetPart part = parts[index];
        bool isLast = index == parts.Count - 1;
        switch (part)
        {
            case TargetMember member:
                {
                    if (current is not BeaconMapValue map)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a map for '.{member.Name}', but found {DescribeValueKind(current)}.",
                            member.Span.Origin,
                            "Only maps have named fields; check the value with 'is set' first.");
                    }

                    var copy = new Dictionary<string, BeaconValue>(map.Entries, StringComparer.Ordinal);
                    if (isLast)
                        copy[member.Name] = value;
                    else
                    {
                        copy.TryGetValue(member.Name, out BeaconValue? next);
                        next ??= BeaconValue.None;
                        copy[member.Name] = await AssignPartsAsync(next, parts, index + 1, value, span, scope, verbs, ct).ConfigureAwait(false);
                    }

                    return BeaconValue.Map(copy);
                }

            case TargetIndex targetIndex:
                {
                    BeaconValue indexValue = await EvaluateExprAsync(targetIndex.Index, scope, verbs, ct).ConfigureAwait(false);
                    if (current is BeaconMapValue map)
                    {
                        string key = IndexToMapKey(indexValue, targetIndex.Span);
                        var copy = new Dictionary<string, BeaconValue>(map.Entries, StringComparer.Ordinal);
                        if (isLast)
                            copy[key] = value;
                        else
                        {
                            copy.TryGetValue(key, out BeaconValue? next);
                            next ??= BeaconValue.None;
                            copy[key] = await AssignPartsAsync(next, parts, index + 1, value, span, scope, verbs, ct).ConfigureAwait(false);
                        }

                        return BeaconValue.Map(copy);
                    }

                    if (current is BeaconListValue list)
                    {
                        long listIndex = RequireListIndex(indexValue, targetIndex.Span);
                        if (listIndex < 0 || listIndex >= list.Items.Count)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                $"I expected an index from 0 to {list.Items.Count - 1}, but found {listIndex}.",
                                targetIndex.Span.Origin,
                                "Use an index in range (lists are 0-based).");
                        }

                        var copy = list.Items.ToList();
                        if (isLast)
                            copy[(int)listIndex] = value;
                        else
                            copy[(int)listIndex] = await AssignPartsAsync(copy[(int)listIndex], parts, index + 1, value, span, scope, verbs, ct).ConfigureAwait(false);

                        return BeaconValue.List(copy);
                    }

                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        $"I expected a list or map for '[...]', but found {DescribeValueKind(current)}.",
                        targetIndex.Span.Origin,
                        "Only lists and maps support indexing.");
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.Parse,
                    "I expected a member or index target.",
                    span.Origin,
                    "Write set quiz.running to yes or set warns[player] to 1.",
                    isCatchable: false);
        }
    }

    #endregion
    #region expressions

    private async Task<BeaconValue> EvaluateExprAsync(
        BeaconExpr expr, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        switch (expr)
        {
            case OrExpr or:
                {
                    BeaconValue left = await EvaluateExprAsync(or.Left, scope, verbs, ct).ConfigureAwait(false);
                    if (!IsFalsy(left))
                        return left;

                    return await EvaluateExprAsync(or.Right, scope, verbs, ct).ConfigureAwait(false);
                }

            case AndExpr and:
                {
                    BeaconValue left = await EvaluateExprAsync(and.Left, scope, verbs, ct).ConfigureAwait(false);
                    if (IsFalsy(left))
                        return left;

                    return await EvaluateExprAsync(and.Right, scope, verbs, ct).ConfigureAwait(false);
                }

            case NotExpr not:
                {
                    BeaconValue operand = await EvaluateExprAsync(not.Operand, scope, verbs, ct).ConfigureAwait(false);
                    if (operand is not BeaconYesNoValue yesNo)
                        throw StrictBooleanForNot(operand, not);

                    return BeaconValue.YesNo(!yesNo.Value);
                }

            case ComparisonExpr cmp:
                return await EvaluateComparisonAsync(cmp, scope, verbs, ct).ConfigureAwait(false);

            case AddExpr add:
                {
                    BeaconValue left = await EvaluateExprAsync(add.Left, scope, verbs, ct).ConfigureAwait(false);
                    BeaconValue right = await EvaluateExprAsync(add.Right, scope, verbs, ct).ConfigureAwait(false);
                    if (add.Op == "+")
                    {
                        if (left is BeaconNumberValue ln && right is BeaconNumberValue rn)
                            return BeaconValue.Number(ln.Value + rn.Value);

                        if (left is BeaconTextValue lt && right is BeaconTextValue rt)
                            return BeaconValue.Text(lt.Value + rt.Value);

                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected both sides of '+' to be the same kind (both text or both numbers), but found {DescribeValueKind(left)} and {DescribeValueKind(right)}.",
                            add.Span.Origin,
                            "Write \"health: {health}\" for interpolation or use text() to convert, e.g. text(health).");
                    }
                    else
                    {
                        if (left is BeaconNumberValue ln2 && right is BeaconNumberValue rn2)
                            return BeaconValue.Number(ln2.Value - rn2.Value);

                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected numbers on both sides of '-', but found {DescribeValueKind(left)} and {DescribeValueKind(right)}.",
                            add.Span.Origin,
                            "Write a - b with numbers only (use text() to render first).");
                    }
                }

            case MulExpr mul:
                {
                    BeaconValue left = await EvaluateExprAsync(mul.Left, scope, verbs, ct).ConfigureAwait(false);
                    BeaconValue right = await EvaluateExprAsync(mul.Right, scope, verbs, ct).ConfigureAwait(false);
                    if (left is not BeaconNumberValue ln || right is not BeaconNumberValue rn)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected numbers on both sides of '{mul.Op}', but found {DescribeValueKind(left)} and {DescribeValueKind(right)}.",
                            mul.Span.Origin,
                            $"Write a {mul.Op} b with numbers only.");
                    }

                    return mul.Op switch
                    {
                        "*" => BeaconValue.Number(ln.Value * rn.Value),
                        "/" => rn.Value == 0
                            ? throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected a non-zero divisor, but found 0.",
                                mul.Span.Origin,
                                "Guard the divisor with 'if d is not 0' or 'is not set' first.")
                            : BeaconValue.Number(ln.Value / rn.Value),
                        "%" => rn.Value == 0
                            ? throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected a non-zero divisor, but found 0.",
                                mul.Span.Origin,
                                "Guard the divisor first.")
                            : BeaconValue.Number(ln.Value % rn.Value),
                        _ => throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.Parse, $"Unknown operator '{mul.Op}'.", mul.Span.Origin, "Use *, /, or %.",
                            isCatchable: false),
                    };
                }

            case NegateExpr neg:
                {
                    BeaconValue operand = await EvaluateExprAsync(neg.Operand, scope, verbs, ct).ConfigureAwait(false);
                    if (operand is not BeaconNumberValue number)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a number after '-', but found {DescribeValueKind(operand)}.",
                            neg.Span.Origin,
                            "Write -40 or -x with a number.");
                    }

                    return BeaconValue.Number(-number.Value);
                }

            case MemberExpr member:
                {
                    BeaconValue target = await EvaluateExprAsync(member.Target, scope, verbs, ct).ConfigureAwait(false);
                    if (target is not BeaconMapValue map)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a map for '.{member.Member}', but found {DescribeValueKind(target)}.",
                            member.MemberSpan.Origin,
                            "Only maps have named fields; check the value with 'is set' first.");
                    }

                    if (map.Entries.TryGetValue(member.Member, out BeaconValue? found) && found is not null)
                        return found;

                    if (member.Target is IdentExpr table
                        && table.Name is "items" or "effects" or "enchantments" or "enchants")
                        throw UnknownTableKey(table.Name, member.Member, member.MemberSpan);

                    return BeaconValue.None;
                }

            case IndexExpr index:
                {
                    BeaconValue target = await EvaluateExprAsync(index.Target, scope, verbs, ct).ConfigureAwait(false);
                    BeaconValue key = await EvaluateExprAsync(index.Index, scope, verbs, ct).ConfigureAwait(false);
                    return IndexLookup(target, key, index.Span);
                }

            case CallExpr call:
                return await EvaluateCallAsync(call, scope, verbs, ct).ConfigureAwait(false);

            case NumberLiteral number:
                return BeaconValue.Number(number.Value);

            case TextLiteral text:
                {
                    var builder = new System.Text.StringBuilder();
                    foreach (BeaconTextNode part in text.Parts)
                    {
                        switch (part)
                        {
                            case TextChunk chunk:
                                builder.Append(chunk.Value);
                                break;
                            case TextHole hole:
                                {
                                    BeaconValue holeValue = await EvaluateExprAsync(hole.Expr, scope, verbs, ct).ConfigureAwait(false);
                                    builder.Append(ToDisplayText(holeValue));
                                    break;
                                }
                        }
                    }

                    return BeaconValue.Text(builder.ToString());
                }

            case RegexLiteral regex:
                return BeaconValue.Text(regex.Pattern);

            case YesLiteral:
                return BeaconValue.YesNo(true);

            case NoLiteral:
                return BeaconValue.YesNo(false);

            case NoneLiteral:
                return BeaconValue.None;

            case CallPrimExpr prim:
                return await EvaluateCallPrimAsync(prim, scope, verbs, ct).ConfigureAwait(false);

            case IdentExpr ident:
                return ResolveIdent(ident.Name, ident.Span, scope);

            case ListLiteral list:
                {
                    var items = new List<BeaconValue>(list.Items.Count);
                    foreach (BeaconExpr item in list.Items)
                        items.Add(await EvaluateExprAsync(item, scope, verbs, ct).ConfigureAwait(false));

                    return BeaconValue.List(items);
                }

            case MapLiteral map:
                {
                    var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
                    foreach (BeaconMapEntry entry in map.Entries)
                        entries[entry.Key] = await EvaluateExprAsync(entry.Value, scope, verbs, ct).ConfigureAwait(false);

                    return BeaconValue.Map(entries);
                }

            case ParenExpr paren:
                return await EvaluateExprAsync(paren.Inner, scope, verbs, ct).ConfigureAwait(false);

            case MccExpr mcc:
                {
                    Spend(scope, mcc.Span);
                    BeaconValue argument = await EvaluateExprAsync(mcc.Argument, scope, verbs, ct).ConfigureAwait(false);
                    return await verbs.MccAsync(argument, mcc.Span, ct).ConfigureAwait(false);
                }

            case ErrorExpr error:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.Parse,
                    "I expected a value, but this hole never parsed. Fix lint errors first, then run again.",
                    error.Span.Origin,
                    "Run lint and fix the B0001 findings.",
                    isCatchable: false);

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.Parse,
                    $"I expected a runnable expression, but found '{expr.GetType().Name}'.",
                    expr.Span.Origin,
                    "Fix lint errors first, then run again.",
                    isCatchable: false);
        }
    }

    private async Task<BeaconValue> EvaluateComparisonAsync(
        ComparisonExpr cmp, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        BeaconValue left = await EvaluateExprAsync(cmp.Left, scope, verbs, ct).ConfigureAwait(false);
        switch (cmp.Op)
        {
            case BeaconComparisonOp.IsEmpty:
                return BeaconValue.YesNo(IsEmptyValue(left, cmp));
            case BeaconComparisonOp.IsNotEmpty:
                return BeaconValue.YesNo(!IsEmptyValue(left, cmp));
            case BeaconComparisonOp.IsSet:
                return BeaconValue.YesNo(left.Kind != BeaconValueKind.None);
            case BeaconComparisonOp.IsNotSet:
                return BeaconValue.YesNo(left.Kind == BeaconValueKind.None);
            default:
                break;
        }

        if (cmp.Right is null)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.Parse, "I expected a right-hand value for this comparison.",
                cmp.Span.Origin, "Write x is \"y\" (or x is set / x is empty).",
                isCatchable: false);
        }

        BeaconValue right = await EvaluateExprAsync(cmp.Right, scope, verbs, ct).ConfigureAwait(false);
        return cmp.Op switch
        {
            BeaconComparisonOp.Is or BeaconComparisonOp.Equal => BeaconValue.YesNo(EqualsDeep(left, right)),
            BeaconComparisonOp.IsNot or BeaconComparisonOp.NotEqual => BeaconValue.YesNo(!EqualsDeep(left, right)),
            BeaconComparisonOp.Less => CompareNumbers(left, right, cmp, static (a, b) => a < b),
            BeaconComparisonOp.Greater => CompareNumbers(left, right, cmp, static (a, b) => a > b),
            BeaconComparisonOp.LessEqual => CompareNumbers(left, right, cmp, static (a, b) => a <= b),
            BeaconComparisonOp.GreaterEqual => CompareNumbers(left, right, cmp, static (a, b) => a >= b),
            BeaconComparisonOp.Contains => EvaluateContains(left, right, cmp.Span),
            BeaconComparisonOp.Matches => EvaluateMatches(left, right, cmp.Span),
            BeaconComparisonOp.StartsWith => EvaluateStartsEnds(left, right, cmp.Span, starts: true),
            BeaconComparisonOp.EndsWith => EvaluateStartsEnds(left, right, cmp.Span, starts: false),
            _ => throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.Parse, $"Unknown comparison '{cmp.Op}'.", cmp.Span.Origin,
                "Use is, <, >, contains, matches, starts with, ends with, is empty, is set.",
                isCatchable: false),
        };
    }

    private static BeaconValue CompareNumbers(BeaconValue left, BeaconValue right, ComparisonExpr cmp, Func<double, double, bool> test)
    {
        if (left is not BeaconNumberValue ln || right is not BeaconNumberValue rn)
        {
            string symbol = cmp.Op switch
            {
                BeaconComparisonOp.Less => "<",
                BeaconComparisonOp.Greater => ">",
                BeaconComparisonOp.LessEqual => "<=",
                BeaconComparisonOp.GreaterEqual => ">=",
                _ => "comparison",
            };
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected numbers on both sides of '{symbol}', but found {DescribeValueKind(left)} and {DescribeValueKind(right)}.",
                cmp.Span.Origin,
                $"Write x {symbol} 1 with numbers (use number(text) to convert).");
        }

        return BeaconValue.YesNo(test(ln.Value, rn.Value));
    }

    private static BeaconValue EvaluateContains(BeaconValue left, BeaconValue right, SourceSpan span)
    {
        if (left is BeaconTextValue lt && right is BeaconTextValue rt)
            return BeaconValue.YesNo(lt.Value.Contains(rt.Value, StringComparison.Ordinal));

        if (left is BeaconListValue list)
            return BeaconValue.YesNo(list.Items.Any(item => EqualsDeep(item, right)));

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected text or a list for 'contains', but found {DescribeValueKind(left)}.",
            span.Origin,
            "Write message contains \"!help\" for text or list membership.");
    }

    private static BeaconValue EvaluateMatches(BeaconValue left, BeaconValue right, SourceSpan span)
    {
        if (left is not BeaconTextValue lt)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected text on the left of 'matches', but found {DescribeValueKind(left)}.",
                span.Origin,
                "Write text matches /pattern/.");
        }

        if (right is not BeaconTextValue rt)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a /pattern/ on the right of 'matches', but found {DescribeValueKind(right)}.",
                span.Origin,
                "Write text matches /pattern/.");
        }

        try
        {
            bool hit = Regex.IsMatch(lt.Value, rt.Value, RegexOptions.None, TimeSpan.FromSeconds(1));
            return BeaconValue.YesNo(hit);
        }
        catch (ArgumentException ex)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a valid pattern for 'matches', but \"{rt.Value}\" does not compile: {ex.Message}",
                span.Origin,
                "Write text matches /a+/ (escape a literal slash as \\/).",
                ex);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"The 'matches' pattern timed out on this text.",
                span.Origin,
                "Simplify the pattern and try again.",
                ex);
        }
    }

    private static BeaconValue EvaluateStartsEnds(BeaconValue left, BeaconValue right, SourceSpan span, bool starts)
    {
        if (left is not BeaconTextValue lt || right is not BeaconTextValue rt)
        {
            string word = starts ? "starts with" : "ends with";
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected text on both sides of '{word}', but found {DescribeValueKind(left)} and {DescribeValueKind(right)}.",
                span.Origin,
                starts ? "Write message starts with \"!sell \"." : "Write name ends with \".txt\".");
        }

        bool hit = starts
            ? lt.Value.StartsWith(rt.Value, StringComparison.Ordinal)
            : lt.Value.EndsWith(rt.Value, StringComparison.Ordinal);
        return BeaconValue.YesNo(hit);
    }

    private static bool IsEmptyValue(BeaconValue value, BeaconExpr? context)
    {
        switch (value)
        {
            case BeaconTextValue text:
                return text.Value.Length == 0;
            case BeaconListValue list:
                return list.Items.Count == 0;
            case BeaconMapValue map:
                return map.Entries.Count == 0;
            case BeaconNoneValue:
                return true;
            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.StrictMixedOperands,
                    $"I expected text, a list, or a map for emptiness, but found {DescribeValueKind(value)}.",
                    (context?.Span ?? new SourceSpan("unknown.mcc", 1, 1, 0)).Origin,
                    "Use 'is set' for presence, or compare numbers with > 0.");
        }
    }

    private static BeaconRuntimeException StrictBooleanForNot(BeaconValue operand, NotExpr not)
    {
        string hint = operand.Kind switch
        {
            BeaconValueKind.Number => $"Did you mean `not ({DescribeExpr(not.Operand)} > 0)`?",
            BeaconValueKind.Text or BeaconValueKind.List or BeaconValueKind.Map =>
                $"Did you mean `not ({DescribeExpr(not.Operand)} is not empty)`?",
            BeaconValueKind.None => $"Did you mean `{DescribeExpr(not.Operand)} is not set`?",
            _ => "Conditions need yes/no: use a comparison (is, <, >, contains, matches), and/or/not, or a call.",
        };
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictBooleanCondition,
            $"I expected yes/no after 'not', but found {DescribeValueKind(operand)}. {hint}",
            not.Span.Origin,
            "Write not tired where tired is yes/no, or compare first.");
    }

    private void RequireYesNo(BeaconValue value, BeaconExpr expr, string where)
    {
        if (value is BeaconYesNoValue)
            return;

        // or/and return operands, never yes/no: name the trap explicitly.
        if (expr is OrExpr orExpr)
        {
            string inner = DescribeExpr(orExpr);
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictBooleanCondition,
                $"I expected yes/no after '{where}', but found {DescribeValueKind(value)} from `{inner}`. " +
                $"`or` returns an operand value, never yes/no. Did you mean `{where} ({inner}) > 2`? Or `{where} ({inner}) is not empty` for lists and text.",
                expr.Span.Origin,
                $"Write {where} ({inner}) > 2 or {where} ({inner}) is not empty.");
        }

        if (expr is AndExpr andExpr)
        {
            string inner = DescribeExpr(andExpr);
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictBooleanCondition,
                $"I expected yes/no after '{where}', but found {DescribeValueKind(value)} from `{inner}`. " +
                $"`and` returns an operand value, never yes/no.",
                expr.Span.Origin,
                $"Write {where} ({inner}) > 0 or compare first.");
        }

        string hint = (expr, value) switch
        {
            (IdentExpr ident, BeaconNumberValue) => $"Did you mean `{where} {ident.Name} > 0`?",
            (MemberExpr, BeaconNumberValue) => $"Did you mean `{where} {DescribeExpr(expr)} > 0`?",
            (IndexExpr, BeaconNumberValue) => $"Did you mean `{where} {DescribeExpr(expr)} > 0`?",
            (IdentExpr ident, BeaconTextValue or BeaconListValue or BeaconMapValue) =>
                $"Did you mean `{where} {ident.Name} is not empty` for lists and text.",
            (_, BeaconTextValue or BeaconListValue or BeaconMapValue) =>
                $"Did you mean `{where} {DescribeExpr(expr)} is not empty`?",
            (_, BeaconNoneValue) => $"Did you mean `{where} {DescribeExpr(expr)} is set`?",
            (_, BeaconNumberValue) => $"Did you mean `{where} {DescribeExpr(expr)} > 0`?",
            _ => "Conditions need yes/no: use a comparison (is, <, >, contains, matches), and/or/not, or a call.",
        };
        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictBooleanCondition,
            $"I expected a yes/no condition after '{where}', but found {DescribeValueKind(value)}. {hint}",
            expr.Span.Origin,
            where == "when"
                ? "Write when message contains \"!help\" or when health < 6."
                : $"Write {where} x > 0 or {where} x is set.");
    }

    private static double RequireWaitCount(BeaconValue value, WaitStmt wait)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative number for 'wait', but found {DescribeValueKind(value)}.",
                wait.Span.Origin,
                "Write wait 2 seconds.");
        }

        return number.Value;
    }

    private static long RequireRepeatCount(BeaconValue value, RepeatStmt repeat)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative number for 'repeat', but found {DescribeValueKind(value)}.",
                repeat.Span.Origin,
                "Write repeat 3 times.");
        }

        return (long)Math.Floor(number.Value);
    }

    private static List<BeaconValue> RequireIterable(BeaconValue value, ForStmt forStmt)
    {
        if (value is BeaconListValue list)
            return list.Items.ToList();

        if (value is BeaconMapValue map)
            return map.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => BeaconValue.Text(k)).ToList<BeaconValue>();

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a list or map after 'in', but found {DescribeValueKind(value)}.",
            forStmt.Span.Origin,
            $"Write for each {forStmt.Var} in online_players.");
    }

    private static TimeSpan UnitToTimeSpan(double count, string unit, WaitStmt wait)
    {
        return unit switch
        {
            "millisecond" => TimeSpan.FromMilliseconds(count),
            "second" => TimeSpan.FromSeconds(count),
            "minute" => TimeSpan.FromMinutes(count),
            _ => throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownUnit,
                $"Unit '{wait.RawUnit}' is not allowed after 'wait'. Use one of: millisecond, second, minute.",
                wait.UnitSpan.Origin,
                "Write 'wait 2 seconds'."),
        };
    }

    private static BeaconValue IndexLookup(BeaconValue target, BeaconValue index, SourceSpan span)
    {
        if (target is BeaconMapValue map)
        {
            string key = IndexToMapKey(index, span);
            return map.Entries.TryGetValue(key, out BeaconValue? found) && found is not null
                ? found
                : BeaconValue.None;
        }

        if (target is BeaconListValue list)
        {
            long listIndex = RequireListIndex(index, span);
            if (listIndex < 0 || listIndex >= list.Items.Count)
                return BeaconValue.None;

            return list.Items[(int)listIndex];
        }

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a list or map for '[...]', but found {DescribeValueKind(target)}.",
            span.Origin,
            "Only lists and maps support indexing (check with 'is set' first).");
    }

    private static string IndexToMapKey(BeaconValue index, SourceSpan span)
    {
        if (index is BeaconTextValue text)
            return text.Value;

        if (index is BeaconNumberValue number)
            return number.Value.ToString("G", CultureInfo.InvariantCulture);

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected text for a map key, but found {DescribeValueKind(index)}.",
            span.Origin,
            "Write warns[player] where player is text (use text(value) to convert).");
    }

    private static long RequireListIndex(BeaconValue index, SourceSpan span)
    {
        if (index is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a number for a list index, but found {DescribeValueKind(index)}.",
                span.Origin,
                "Write items[0] (lists are 0-based).");
        }

        if (number.Value != Math.Floor(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a whole number for a list index, but found {number.Value.ToString("G", CultureInfo.InvariantCulture)}.",
                span.Origin,
                "Write items[0] with a whole number.");
        }

        return (long)number.Value;
    }

    #endregion
    #region names, builtins, calls

    /// <summary>Unknown game-table key with a neighbor suggestion (never a silent none).</summary>
    private BeaconRuntimeException UnknownTableKey(string table, string key, SourceSpan span)
    {
        IReadOnlySet<string> candidates = table switch
        {
            "items" => BeaconGameTables.ItemIds(Host.GameProtocol),
            "effects" => BeaconGameTables.EffectIds(Host.GameProtocol),
            _ => BeaconGameTables.EnchantIds(Host.GameProtocol),
        };
        var shorts = new HashSet<string>(
            candidates.Select(BeaconMatchers.ShortName), StringComparer.Ordinal);
        string? neighbor = BeaconGameTables.Suggest(key, shorts);
        return new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            neighbor is null
                ? $"Unknown {table} key '{key}'. The tables track the session version; check the id."
                : $"Unknown {table} key '{key}'. Did you mean '{neighbor}'?",
            span.Origin,
            neighbor is null
                ? $"Use a full type id like {table}.totem_of_undying."
                : $"Write {table}.{neighbor}.");
    }

    private static bool IsBuiltinName(string name)
        => BuiltinFunctions.Contains(name) || BuiltinValues.Contains(name);

    private string SuggestFix(string name, BeaconScope scope)
    {
        var candidates = scope.VisibleNames()
            .Concat(BuiltinFunctions)
            .Concat(BuiltinValues)
            .Concat(BareTwins.Keys)
            .Concat(_externs?.Names ?? [])
            .Concat(_importAliases.Keys)
            .Concat(Bridge?.VariableNames ?? [])
            .Concat(["food", "player", "message", "me", "server", "event"])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        string? best = DidYouMean(name, candidates);
        return best is null
            ? $"Check the spelling of '{name}'."
            : $"Did you mean '{best}'?";
    }

    private BeaconValue ResolveIdent(string name, SourceSpan span, BeaconScope scope)
    {
        SourceSpan origin = span.Origin;
        if (string.Equals(name, "hunger", StringComparison.OrdinalIgnoreCase))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                "There is no bare `hunger`; food is `food` everywhere.",
                origin,
                "Write food (or me.food for the live value).");
        }

        if (scope.TryLookup(name, out BeaconValue? found) && found is not null)
            return found;

        if (BuiltinValues.Contains(name))
            return ResolveBuiltinValue(name);

        if (BuiltinFunctions.Contains(name))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected arguments for '{name}', but found the bare name. '{name}' is a function.",
                origin,
                $"Write {name}(...) (see the builtin reference).");
        }

        if (_externs?.Declares(name) == true || Bridge?.TryGetFunction(name, out _) == true)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"I expected arguments for '{name}', but found the bare name. '{name}' is an extension function.",
                origin,
                $"Write {name}(...) with its declared arguments.");
        }

        if (BareTwins.TryGetValue(name, out (string Ns, string Key) twin))
        {
            BeaconValue ns = ResolveBuiltinValue(twin.Ns);
            if (ns is BeaconMapValue map && map.Entries.TryGetValue(twin.Key, out BeaconValue? live) && live is not null)
                return live;

            return BeaconValue.None;
        }

        if (Bridge is not null && Bridge.TryGetVariable(name, out BeaconVariableRegistration? variable) && variable is not null)
            return ReadPluginVariable(variable, span);

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            $"Unknown name '{name}'.",
            origin,
            SuggestFix(name, scope));
    }

    /// <summary>
    /// Reads one plugin variable namespace: invokes the snapshot inline (snapshots must be fast and never block; dispatches never run on the session loop) and marshals the six kinds.
    /// Anything else is a catchable error naming the plugin, never a silent none.
    /// </summary>
    private BeaconValue ReadPluginVariable(BeaconVariableRegistration variable, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(span);
        SourceSpan origin = span.Origin;
        object? raw;
        try
        {
            raw = variable.Snapshot(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Variable '{variable.Name}' from plugin '{variable.OwnerPluginId}' failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }

        BeaconValue crossed;
        try
        {
            crossed = BeaconMarshal.ToBeacon(raw);
        }
        catch (InvalidOperationException ex)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Variable '{variable.Name}' from plugin '{variable.OwnerPluginId}' returned a value that cannot cross: {ex.Message}",
                origin,
                "Plugin variables return a string-keyed map of text, numbers, booleans, lists, maps, or null.",
                ex);
        }

        if (crossed is not BeaconMapValue map)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected a map for variable '{variable.Name}' from plugin '{variable.OwnerPluginId}', but found {DescribeValueKind(crossed)}.",
                origin,
                "Plugin variables return a read-only string-keyed map per read.");
        }

        return map;
    }

    private BeaconValue ResolveBuiltinValue(string name)
    {
        if (string.Equals(name, "me", StringComparison.Ordinal))
            return BuildMeMap();

        if (string.Equals(name, "server", StringComparison.Ordinal))
            return BuildServerMap();

        if (string.Equals(name, "game", StringComparison.Ordinal))
            return BuildGameMap();

        if (string.Equals(name, "settings", StringComparison.Ordinal))
            return BeaconValue.Map(new Dictionary<string, BeaconValue>(Settings, StringComparer.Ordinal));

        if (string.Equals(name, "time", StringComparison.Ordinal))
            return BuildTimeMap();

        if (string.Equals(name, "shared", StringComparison.Ordinal))
            return BeaconValue.Map(new Dictionary<string, BeaconValue>(_shared, StringComparer.Ordinal));

        if (string.Equals(name, "beacon", StringComparison.Ordinal))
        {
            return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["lib"] = BeaconValue.Number(BeaconLint.CurrentLibVersion),
            });
        }

        if (string.Equals(name, "vars", StringComparison.Ordinal))
            return BeaconVarsBridge.Snapshot(Variables);

        if (string.Equals(name, "items", StringComparison.Ordinal))
        {
            return BeaconValue.Map(BeaconGameTables.ItemIds(Host.GameProtocol)
                .ToDictionary(id => BeaconMatchers.ShortName(id), id => (BeaconValue)BeaconValue.Text(id), StringComparer.Ordinal));
        }

        if (string.Equals(name, "effects", StringComparison.Ordinal))
        {
            return BeaconValue.Map(BeaconGameTables.EffectIds(Host.GameProtocol)
                .ToDictionary(id => BeaconMatchers.ShortName(id), id => (BeaconValue)BeaconValue.Text(id), StringComparer.Ordinal));
        }

        if (string.Equals(name, "enchants", StringComparison.Ordinal))
        {
            return BeaconValue.Map(BeaconGameTables.EnchantIds(Host.GameProtocol)
                .ToDictionary(id => BeaconMatchers.ShortName(id), id => (BeaconValue)BeaconValue.Text(id), StringComparer.Ordinal));
        }

        if (string.Equals(name, "online_players", StringComparison.Ordinal))
        {
            return BeaconValue.List(Host.OnlinePlayers(BeaconReadBounds.OnlinePlayersDefaultPage)
                .Select(p => BeaconValue.Text(p)).ToList<BeaconValue>());
        }

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            $"Unknown name '{name}'.",
            new SourceSpan(FileName, 1, 1, 0),
            $"Check the spelling of '{name}'.");
    }

    private BeaconValue BuildMeMap()
    {
        BeaconVitals? vitals = Host.SelfVitals;
        BeaconPosition? pos = Host.SelfPosition;
        var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["name"] = Host.SelfName is string self ? BeaconValue.Text(self) : BeaconValue.None,
            ["uuid"] = BeaconValue.None,
            ["health"] = vitals?.Health is { } health ? BeaconValue.Number(health) : BeaconValue.None,
            ["max_health"] = vitals?.MaxHealth is { } maxHealth ? BeaconValue.Number(maxHealth) : BeaconValue.None,
            ["food"] = vitals?.Food is { } food ? BeaconValue.Number(food) : BeaconValue.None,
            ["saturation"] = vitals?.Saturation is { } saturation ? BeaconValue.Number(saturation) : BeaconValue.None,
            ["air"] = vitals?.Air is { } air ? BeaconValue.Number(air) : BeaconValue.None,
            ["xp_level"] = vitals?.XpLevel is { } xp ? BeaconValue.Number(xp) : BeaconValue.None,
            ["armor"] = vitals?.Armor is { } armor ? BeaconValue.Number(armor) : BeaconValue.None,
            ["gamemode"] = Host.Gamemode is string mode ? BeaconValue.Text(mode) : BeaconValue.None,
            ["yaw"] = pos?.Yaw is { } yaw ? BeaconValue.Number(yaw) : BeaconValue.None,
            ["pitch"] = pos?.Pitch is { } pitch ? BeaconValue.Number(pitch) : BeaconValue.None,
            ["ping"] = Host.Ping is { } ping ? BeaconValue.Number(ping) : BeaconValue.None,
            ["is_sneaking"] = Host.IsSneaking is { } sneaking ? BeaconValue.YesNo(sneaking) : BeaconValue.None,
            ["pos"] = pos is null
                ? BeaconValue.None
                : BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["x"] = BeaconValue.Number(pos.X),
                    ["y"] = BeaconValue.Number(pos.Y),
                    ["z"] = BeaconValue.Number(pos.Z),
                }),
            ["effects"] = BeaconValue.List(Host.PlayerEffects
                .Select(e => BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["name"] = BeaconValue.Text(e.Name),
                    ["level"] = BeaconValue.Number(e.Level),
                    ["seconds_left"] = BeaconValue.Number(e.SecondsLeft),
                })).ToList<BeaconValue>()),
        };
        return BeaconValue.Map(entries);
    }

    private BeaconValue BuildServerMap()
    {
        int count;
        try
        {
            count = Host.OnlinePlayerCount;
        }
        catch (Exception)
        {
            count = 0;
        }

        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["online_count"] = BeaconValue.Number(count),
            ["tps"] = Host.ServerTps is double tps ? BeaconValue.Number(tps) : BeaconValue.None,
            ["mspt"] = Host.ServerMspt is double mspt ? BeaconValue.Number(mspt) : BeaconValue.None,
            ["protocol"] = Host.GameProtocol is { } protocol ? BeaconValue.Number(protocol) : BeaconValue.None,
            ["ip"] = Host.ServerInfo?.Ip is string ip ? BeaconValue.Text(ip) : BeaconValue.None,
            ["port"] = Host.ServerInfo?.Port is { } port ? BeaconValue.Number(port) : BeaconValue.None,
            ["version_name"] = Host.ServerInfo?.VersionName is string version ? BeaconValue.Text(version) : BeaconValue.None,
            ["max_players"] = Host.ServerInfo?.MaxPlayers is { } max ? BeaconValue.Number(max) : BeaconValue.None,
            ["motd"] = Host.ServerInfo?.Motd is string motd ? BeaconValue.Text(motd) : BeaconValue.None,
            ["day_time"] = Host.ServerInfo?.DayTime is { } dayTime ? BeaconValue.Number(dayTime) : BeaconValue.None,
            ["day"] = Host.ServerInfo?.Day is { } day ? BeaconValue.Number(day) : BeaconValue.None,
            ["weather"] = Host.ServerInfo?.Weather is string weather ? BeaconValue.Text(weather) : BeaconValue.None,
            ["difficulty"] = Host.ServerInfo?.Difficulty is string difficulty ? BeaconValue.Text(difficulty) : BeaconValue.None,
            ["scoreboard"] = BuildScoreboardMap(),
            ["bossbars"] = BuildBossbarsList(),
        });
    }

    private BeaconValue BuildScoreboardMap()
    {
        BeaconScoreboard board;
        try
        {
            board = Host.Scoreboard;
        }
        catch (Exception)
        {
            return BeaconValue.None;
        }

        var objectives = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (BeaconObjectiveInfo objective in board.Objectives)
        {
            objectives[objective.Name] = BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["display"] = BeaconValue.Text(objective.Display),
                ["scores"] = BeaconValue.Map(objective.Scores
                    .ToDictionary(kv => kv.Key, kv => (BeaconValue)BeaconValue.Number(kv.Value), StringComparer.Ordinal)),
            });
        }

        var teams = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (BeaconTeamInfo team in board.Teams)
        {
            teams[team.Name] = BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["display"] = BeaconValue.Text(team.Display),
                ["members"] = BeaconValue.List(team.Members.Select(BeaconValue.Text).ToList<BeaconValue>()),
            });
        }

        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["objectives"] = BeaconValue.Map(objectives),
            ["teams"] = BeaconValue.Map(teams),
        });
    }

    /// <summary>
    /// Reads one scoreboard score without the map-navigation chain: <c>server.score("kills")</c> answers the whole scores map for one objective, and <c>server.score("kills", "Alice")</c> answers one entry's number.
    /// Either answers <c>none</c> when the objective (or entry) is unknown or no session is attached, so scripts test the answer with <c>is set</c> instead of catching.
    /// </summary>
    private BeaconValue CallServerScore(IReadOnlyList<BeaconValue> args, SourceSpan origin)
    {
        if (args.Count is not (1 or 2))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Function 'server.score' expects 1 or 2 argument(s) but got {args.Count}.",
                origin,
                "Write server.score(\"kills\", \"Alice\").");
        }

        if (args[0] is not BeaconTextValue objective)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected text for 'server.score', but found {DescribeValueKind(args[0])}.",
                origin,
                "Write server.score(\"kills\", \"Alice\").");
        }

        string? entry = null;
        if (args.Count == 2)
        {
            if (args[1] is not BeaconTextValue entryValue)
            {
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.StrictMixedOperands,
                    $"I expected text for 'server.score', but found {DescribeValueKind(args[1])}.",
                    origin,
                    "Write server.score(\"kills\", \"Alice\").");
            }

            entry = entryValue.Value;
        }

        BeaconScoreboard board;
        try
        {
            board = Host.Scoreboard;
        }
        catch (Exception)
        {
            return BeaconValue.None;
        }

        BeaconObjectiveInfo? found = null;
        foreach (BeaconObjectiveInfo candidate in board.Objectives)
        {
            if (string.Equals(candidate.Name, objective.Value, StringComparison.Ordinal))
            {
                found = candidate;
                break;
            }
        }

        if (found is null)
            return BeaconValue.None;

        if (entry is null)
        {
            return BeaconValue.Map(found.Scores
                .ToDictionary(kv => kv.Key, kv => (BeaconValue)BeaconValue.Number(kv.Value), StringComparer.Ordinal));
        }

        return found.Scores.TryGetValue(entry, out int value)
            ? BeaconValue.Number(value)
            : BeaconValue.None;
    }

    private BeaconValue BuildBossbarsList()
    {
        IReadOnlyList<BeaconBossBarInfo> bars;
        try
        {
            bars = Host.BossBars;
        }
        catch (Exception)
        {
            return BeaconValue.List([]);
        }

        return BeaconValue.List(bars
            .Select(b => BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
            {
                ["title"] = BeaconValue.Text(b.Title),
                ["progress"] = BeaconValue.Number(b.Progress),
                ["color"] = BeaconValue.Text(b.Color),
            })).ToList<BeaconValue>());
    }

    private BeaconValue BuildGameMap()
    {
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["protocol"] = Host.GameProtocol is { } protocol ? BeaconValue.Number(protocol) : BeaconValue.None,
            ["protocols"] = BeaconValue.List(BeaconGameTables.Protocols()
                .Select(p => (BeaconValue)BeaconValue.Number(p)).ToList()),
        });
    }

    private BeaconValue BuildTimeMap()
    {
        DateTimeOffset now = Clock.UtcNow;
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["hour"] = BeaconValue.Number(now.Hour),
            ["minute"] = BeaconValue.Number(now.Minute),
            ["stamp"] = BeaconValue.Number(now.ToUnixTimeSeconds()),
            ["now"] = BeaconValue.Text(now.ToString("HH:mm", CultureInfo.InvariantCulture)),
            ["date"] = BeaconValue.Text(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["today"] = BeaconValue.Text(now.ToString("dddd", CultureInfo.InvariantCulture)),
        });
    }

    private async Task<BeaconValue> EvaluateCallPrimAsync(
        CallPrimExpr prim, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        var argValues = new List<BeaconValue>(prim.Args.Count);
        foreach (BeaconExpr arg in prim.Args)
            argValues.Add(await EvaluateExprAsync(arg, scope, verbs, ct).ConfigureAwait(false));

        string target = prim.Target;
        int dot = target.IndexOf('.');
        if (dot <= 0 || dot == target.Length - 1)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected call \"script.function\"(), but found call \"{target}\"().",
                prim.TargetSpan.Origin,
                "Write call \"shopkeeper.daily_report\"() with the owning script file name first.");
        }

        string scriptId = target[..dot];
        string function = target[(dot + 1)..];
        if (Bridge is not null && Bridge.TryGetValueExport(scriptId, function, out BeaconExportedValue? valueExport) && valueExport is not null)
        {
            if (argValues.Count != 0)
            {
                throw new BeaconRuntimeException(
                    BeaconBridgeErrorCodes.BridgeCall,
                    $"call \"{target}\"() failed: script '{scriptId}' exports '{function}' as a value, not a function. Values take no arguments.",
                    prim.TargetSpan.Origin,
                    $"Write call \"{target}\"() with no arguments, or set x to call \"{target}\"().");
            }

            return BeaconScheduler.CopyBeaconValue(valueExport.Value);
        }

        if (Bridge is null || !Bridge.TryGetExport(scriptId, function, out BeaconExportedFunction? export) || export is null)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"call \"{target}\"() failed: script '{scriptId}' exports no function '{function}'. " +
                $"Mark it visible with 'export function {function}()' in {scriptId}.mcc.",
                prim.TargetSpan.Origin,
                $"Add 'export function {function}()' to {scriptId}.mcc, or guard the call with try/catch err.");
        }

        return await InvokeExportAsync(export, argValues, scope, verbs, prim.Span, ct).ConfigureAwait(false);
    }

    private async Task<BeaconValue> EvaluateCallAsync(
        CallExpr call, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        var argValues = new List<BeaconValue>(call.Args.Count);
        foreach (BeaconExpr arg in call.Args)
            argValues.Add(await EvaluateExprAsync(arg, scope, verbs, ct).ConfigureAwait(false));

        if (call.Target is IdentExpr ident)
            return await CallNamedAsync(ident.Name, argValues, call.Span, scope, verbs, ct).ConfigureAwait(false);

        if (call.Target is MemberExpr member && member.Target is IdentExpr ns)
            return await CallNamespacedAsync(ns.Name, member.Member, argValues, call.Span, scope, verbs, ct).ConfigureAwait(false);

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            "Only named functions can be called (no first-class function values).",
            call.Span.Origin,
            "Write greet(x) where greet is a function defined with 'function greet(...)'.");
    }

    private async Task<BeaconValue> CallNamedAsync(
        string name, IReadOnlyList<BeaconValue> args, SourceSpan span,
        BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        SourceSpan origin = span.Origin;
        if (string.Equals(name, "mcc", StringComparison.Ordinal))
        {
            if (args.Count != 1)
            {
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Function 'mcc' expects 1 argument but got {args.Count}.",
                    origin,
                    "Write mcc \"/list\" or mcc(\"/list\").");
            }

            Spend(scope, span);
            return await verbs.MccAsync(args[0], span, ct).ConfigureAwait(false);
        }

        if (_functions.TryGetValue(name, out BeaconFunctionInfo? function) && function is not null)
        {
            if (args.Count != function.Params.Count)
            {
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Function '{name}' expects {function.Params.Count} argument(s) but got {args.Count}.",
                    origin,
                    $"Write {name}({string.Join(", ", function.Params)}).");
            }

            Spend(scope, span);
            using (Budget.TrackCall(name, span))
            {
                BeaconScope frame = NewFunctionFrame(Globals, function.Params, args);

                // Fresh verbs sharing the same dispatch sinks (passthrough/echo/output flow to the caller).
                var callVerbs = verbs;
                try
                {
                    await ExecuteBlockAsync(function.Body, frame, callVerbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                    return BeaconValue.None;
                }
                catch (BeaconReturnException ret)
                {
                    return ret.Value;
                }
            }
        }

        if (_moduleContext is not null
            && _importAliases.TryGetValue(_moduleContext, out Dictionary<string, BeaconFunctionInfo>? moduleTable)
            && moduleTable is not null
            && moduleTable.TryGetValue(name, out BeaconFunctionInfo? moduleFunction)
            && moduleFunction is not null)
            return await CallModuleSelfAsync(name, moduleFunction, args, span, scope, verbs, ct).ConfigureAwait(false);

        if (BuiltinFunctions.Contains(name))
        {
            Spend(scope, span);
            return await CallBuiltinAsync(name, args, origin, scope, ct).ConfigureAwait(false);
        }

        if (_externs?.Declares(name) == true || Bridge?.TryGetFunction(name, out _) == true)
            return await CallExternAsync(name, args, origin, scope, ct).ConfigureAwait(false);

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            $"Unknown name '{name}'.",
            origin,
            SuggestFix(name, scope));
    }

    /// <summary>
    /// Runs a bare self-call inside an imported module body: the module sees its own functions without the alias prefix, while the importing script still must qualify them.
    /// Own script functions and builtins win over the module table (checked before this runs).
    /// </summary>
    private async Task<BeaconValue> CallModuleSelfAsync(
        string name, BeaconFunctionInfo function, IReadOnlyList<BeaconValue> args,
        SourceSpan span, BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        if (args.Count != function.Params.Count)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Function '{_moduleContext}.{name}' expects {function.Params.Count} argument(s) but got {args.Count}.",
                span.Origin,
                $"Write {_moduleContext}.{name}({string.Join(", ", function.Params)}).");
        }

        Spend(scope, span);
        using (Budget.TrackCall($"{_moduleContext}.{name}", span))
        {
            BeaconScope frame = NewFunctionFrame(Globals, function.Params, args);

            try
            {
                await ExecuteBlockAsync(function.Body, frame, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                return BeaconValue.None;
            }
            catch (BeaconReturnException ret)
            {
                return ret.Value;
            }
        }
    }

    /// <summary>
    /// Calls a C# extension function through the bridge.
    /// The script must declare the name with <c>extern</c>; the call runs on the caller fuel with caller cancellation, on the Beacon scheduler (never the session loop, because dispatches never run there).
    /// A missing provider, an arity mismatch, or a plugin failure is a catchable error naming the plugin, the function, and the caller line.
    /// </summary>
    private async Task<BeaconValue> CallExternAsync(
        string name, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        if (_externs?.Declares(name) != true)
        {
            string plugin = Bridge is not null && Bridge.TryGetFunction(name, out BeaconExtensionFunction? found) && found is not null
                ? found.OwnerPluginId
                : "unknown";
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Unknown name '{name}'. It is offered by plugin '{plugin}', but this script never declares it.",
                origin,
                $"Write extern {name} from \"{plugin}\" above the first declaration.");
        }

        string declaredPlugin = _externs.PluginFor(name) ?? "unknown";
        if (Bridge is null || !Bridge.TryGetFunction(name, out BeaconExtensionFunction? function) || function is null)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected plugin '{declaredPlugin}' to offer '{name}', but no loaded plugin does. " +
                $"Install the '{declaredPlugin}' plugin, or drop the extern until it exists.",
                origin,
                $"Install the '{declaredPlugin}' plugin, or guard the call with try/catch err.");
        }

        if (!string.Equals(function.OwnerPluginId, declaredPlugin, StringComparison.Ordinal))
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected plugin '{declaredPlugin}' to offer '{name}', but it comes from plugin '{function.OwnerPluginId}'. " +
                $"Fix the extern line or install '{declaredPlugin}'.",
                origin,
                $"Write extern {name} from \"{function.OwnerPluginId}\".");
        }

        if (args.Count != function.ParameterNames.Count)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"I expected {function.ParameterNames.Count} argument(s) for extern '{name}' from plugin '{function.OwnerPluginId}', but found {args.Count}.",
                origin,
                $"Write {name}({string.Join(", ", function.ParameterNames)}).");
        }

        Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
        using (Budget.TrackCall($"extern {name}", new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length)))
        {
            var call = new BeaconExtensionCall(
                name, function.OwnerPluginId, args, ScriptId,
                new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length), ct);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(BeaconBudgetLimits.MaxWallClock);
                var callWithBudget = call with { Cancellation = budget.Token };
                return await function.Invoke(callWithBudget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new BeaconRuntimeException(
                    BeaconBridgeErrorCodes.BridgeCall,
                    $"Extern '{name}' from plugin '{function.OwnerPluginId}' ran past the event budget and was aborted (counted against '{function.OwnerPluginId}').",
                    origin,
                    "Keep extension functions short, or move slow work into the plugin's own storage.");
            }
            catch (BeaconRuntimeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new BeaconRuntimeException(
                    BeaconBridgeErrorCodes.BridgeCall,
                    $"Extern '{name}' from plugin '{function.OwnerPluginId}' failed: {ex.Message}",
                    origin,
                    "Wrap it in try/catch err and read err.message.",
                    ex);
            }
        }
    }

    private async Task<BeaconValue> CallNamespacedAsync(
        string ns, string member, IReadOnlyList<BeaconValue> args, SourceSpan span,
        BeaconScope scope, BeaconVerbs verbs, CancellationToken ct)
    {
        SourceSpan origin = span.Origin;
        if (_importAliases.TryGetValue(ns, out Dictionary<string, BeaconFunctionInfo>? table)
            && table is not null
            && table.TryGetValue(member, out BeaconFunctionInfo? imported)
            && imported is not null)
        {
            if (args.Count != imported.Params.Count)
            {
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Function '{ns}.{member}' expects {imported.Params.Count} argument(s) but got {args.Count}.",
                    origin,
                    $"Write {ns}.{member}({string.Join(", ", imported.Params)}).");
            }

            Spend(scope, span);
            using (Budget.TrackCall($"{ns}.{member}", span))
            {
                BeaconScope frame = NewFunctionFrame(Globals, imported.Params, args);

                string? outerModule = _moduleContext;
                _moduleContext = ns;
                try
                {
                    await ExecuteBlockAsync(imported.Body, frame, verbs, 0, isInHandler: false, ct).ConfigureAwait(false);
                    return BeaconValue.None;
                }
                catch (BeaconReturnException ret)
                {
                    return ret.Value;
                }
                finally
                {
                    _moduleContext = outerModule;
                }
            }
        }

        if (string.Equals(ns, "inv", StringComparison.Ordinal))
            return await CallInvAsync(member, args, origin, scope, ct).ConfigureAwait(false);

        if (string.Equals(ns, "entities", StringComparison.Ordinal))
            return await CallEntitiesAsync(member, args, origin, scope, ct).ConfigureAwait(false);

        if (string.Equals(ns, "trade", StringComparison.Ordinal))
            return await CallTradeAsync(member, args, origin, scope, ct).ConfigureAwait(false);

        if (string.Equals(ns, "enchant", StringComparison.Ordinal))
            return await CallEnchantAsync(member, args, origin, scope, ct).ConfigureAwait(false);

        if (string.Equals(ns, "dialog", StringComparison.Ordinal))
            return await CallDialogAsync(member, args, origin, scope, ct).ConfigureAwait(false);

        if (string.Equals(ns, "server", StringComparison.Ordinal))
        {
            if (string.Equals(member, "scoreboard", StringComparison.Ordinal))
            {
                RequireArity($"server.{member}", args, 0, origin);
                return BuildScoreboardMap();
            }

            if (string.Equals(member, "bossbars", StringComparison.Ordinal))
            {
                RequireArity($"server.{member}", args, 0, origin);
                return BuildBossbarsList();
            }

            if (string.Equals(member, "score", StringComparison.Ordinal))
                return CallServerScore(args, origin);
        }

        if (string.Equals(ns, "world", StringComparison.Ordinal))
        {
            if (string.Equals(member, "block_at", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.block_at' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.block_at(100, 64, -30).");
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                BeaconBlockInfo? block;
                try
                {
                    block = Host.GetBlock(coords.X, coords.Y, coords.Z);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        $"world.block_at({coords.X}, {coords.Y}, {coords.Z}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                if (block is null)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        $"I expected a loaded block at ({coords.X}, {coords.Y}, {coords.Z}), but the host knows nothing there. "
                        + "Block reads are one at a time and only inside the loaded range.",
                        origin,
                        "Read a block near spawn or within render distance, inside try/catch err.");
                }

                return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["name"] = BeaconValue.Text(block.Name),
                    ["id"] = BeaconValue.Number(block.Id),
                });
            }

            if (string.Equals(member, "light_at", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.light_at' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.light_at(100, 64, -30).");
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                int? light;
                try
                {
                    light = Host.GetLight(coords.X, coords.Y, coords.Z);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        $"world.light_at({coords.X}, {coords.Y}, {coords.Z}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return light is { } level ? BeaconValue.Number(level) : BeaconValue.None;
            }

            if (string.Equals(member, "biome_at", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.biome_at' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.biome_at(100, 64, -30).");
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                string? biome;
                try
                {
                    biome = Host.GetBiome(coords.X, coords.Y, coords.Z);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        $"world.biome_at({coords.X}, {coords.Y}, {coords.Z}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return biome is not null ? BeaconValue.Text(biome) : BeaconValue.None;
            }

            if (string.Equals(member, "sign_text", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        CommandStrings.ScriptsWorldArity("world.sign_text", args.Count),
                        origin,
                        CommandStrings.ScriptsWorldSignTextUsage);
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                string? text;
                try
                {
                    text = Host.GetSignText(coords.X, coords.Y, coords.Z);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        CommandStrings.ScriptsWorldSignTextFailed(coords.X, coords.Y, coords.Z, ex.Message),
                        origin,
                        CommandStrings.ScriptsWorldCatchSuggestion,
                        ex);
                }

                return text is null ? BeaconValue.None : BeaconValue.Text(text);
            }

            if (string.Equals(member, "find_blocks", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.find_blocks' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.find_blocks(\"chest\", 16, 10).");
                }

                if (args[0] is not BeaconTextValue needle || string.IsNullOrWhiteSpace(needle.Value))
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected a block id for 'world.find_blocks', but found something else.",
                        origin,
                        "Write world.find_blocks(\"chest\", 16, 10).");
                }

                (int Radius, int MaxResults) bounds = BeaconReadBounds.ClampFindBlocks(args[1], args[2], origin);
                IReadOnlyList<BeaconBlockPos> found;
                try
                {
                    Spend(scope, span);
                    found = await Host.FindBlocksAsync(needle.Value, bounds.Radius, bounds.MaxResults, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        $"world.find_blocks(\"{needle.Value}\", {bounds.Radius}, {bounds.MaxResults}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return BeaconValue.List(found
                    .Take(bounds.MaxResults)
                    .Select(p => BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                    {
                        ["x"] = BeaconValue.Number(p.X),
                        ["y"] = BeaconValue.Number(p.Y),
                        ["z"] = BeaconValue.Number(p.Z),
                    })).ToList<BeaconValue>());
            }

            if (string.Equals(member, "find_signs", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        CommandStrings.ScriptsWorldArity("world.find_signs", args.Count),
                        origin,
                        CommandStrings.ScriptsWorldFindSignsUsage);
                }

                if (args[0] is not BeaconTextValue needle || string.IsNullOrWhiteSpace(needle.Value))
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        CommandStrings.ScriptsWorldFindSignsNeedle,
                        origin,
                        CommandStrings.ScriptsWorldFindSignsUsage);
                }

                (int Radius, int MaxResults) bounds = BeaconReadBounds.ClampFindSigns(args[1], args[2], origin);
                IReadOnlyList<BeaconSignInfo> found;
                try
                {
                    Spend(scope, span);
                    found = await Host.FindSignsAsync(needle.Value, bounds.Radius, bounds.MaxResults, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.ReadBounds,
                        CommandStrings.ScriptsWorldFindSignsFailed(needle.Value, bounds.Radius, bounds.MaxResults, ex.Message),
                        origin,
                        CommandStrings.ScriptsWorldCatchSuggestion,
                        ex);
                }

                return BeaconValue.List(found
                    .Take(bounds.MaxResults)
                    .Select(p => BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                    {
                        ["x"] = BeaconValue.Number(p.X),
                        ["y"] = BeaconValue.Number(p.Y),
                        ["z"] = BeaconValue.Number(p.Z),
                        ["text"] = BeaconValue.Text(p.Text),
                    })).ToList<BeaconValue>());
            }

            if (string.Equals(member, "dig", StringComparison.Ordinal))
            {
                if (args.Count is not 3 and not 4)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.dig' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.dig(100, 63, -30) or world.dig(100, 63, -30, \"up\").");
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                string face = args.Count == 4 ? RequireFace(args[3], "world.dig", origin) : "up";
                RequireWorldWriteGates("world.dig", origin);
                WriteGate.Acquire(origin, "world.dig");
                BeaconDigResult dug;
                try
                {
                    Spend(scope, span);
                    dug = await Host.DigAsync(coords.X, coords.Y, coords.Z, face, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.WorldWriteGate,
                        $"world.dig({coords.X}, {coords.Y}, {coords.Z}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["broken"] = BeaconValue.YesNo(dug.Broken),
                    ["detail"] = BeaconValue.Text(dug.Detail),
                });
            }

            if (string.Equals(member, "place", StringComparison.Ordinal))
            {
                if (args.Count is not 3 and not 4)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.place' expects 3 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.place(100, 63, -30) or world.place(100, 63, -30, \"up\").");
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                string face = args.Count == 4 ? RequireFace(args[3], "world.place", origin) : "up";
                RequireWorldWriteGates("world.place", origin);
                WriteGate.Acquire(origin, "world.place");
                bool sent;
                try
                {
                    Spend(scope, span);
                    sent = await Host.PlaceAsync(coords.X, coords.Y, coords.Z, face, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.WorldWriteGate,
                        $"world.place({coords.X}, {coords.Y}, {coords.Z}) failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return BeaconValue.YesNo(sent);
            }

            if (string.Equals(member, "use", StringComparison.Ordinal))
            {
                if (args.Count != 3)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        CommandStrings.ScriptsWorldArity("world.use", args.Count),
                        origin,
                        CommandStrings.ScriptsWorldUseUsage);
                }

                (int X, int Y, int Z) coords = BeaconReadBounds.ValidateBlockCoords(args[0], args[1], args[2], origin);
                RequireWorldWriteGates("world.use", origin);
                WriteGate.Acquire(origin, "world.use");
                BeaconContainerInfo? container;
                try
                {
                    Spend(scope, span);
                    container = await Host.OpenContainerAtAsync(coords.X, coords.Y, coords.Z, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.WorldWriteGate,
                        CommandStrings.ScriptsWorldUseFailed(coords.X, coords.Y, coords.Z, ex.Message),
                        origin,
                        CommandStrings.ScriptsWorldCatchSuggestion,
                        ex);
                }

                if (container is null)
                    return BeaconValue.None;

                return ToContainerValue(container);
            }

            if (string.Equals(member, "looking_at", StringComparison.Ordinal))
            {
                if (args.Count is not 0 and not 1)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Function 'world.looking_at' expects 0 argument(s) but got {args.Count}.",
                        origin,
                        "Write world.looking_at() or world.looking_at(5).");
                }

                double maxDistance = 5.0;
                if (args.Count == 1)
                {
                    if (args[0] is not BeaconNumberValue reach || double.IsNaN(reach.Value) || double.IsInfinity(reach.Value) || reach.Value <= 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a positive number for 'world.looking_at'.",
                            origin,
                            "Write world.looking_at(5).");
                    }

                    maxDistance = Math.Min(reach.Value, 32);
                }

                RequireWorldWriteGates("world.looking_at", origin);
                BeaconRaycastHit? hit;
                try
                {
                    hit = Host.Raycast(maxDistance);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.WorldWriteGate,
                        $"world.looking_at() failed: {ex.Message}",
                        origin,
                        "Wrap it in try/catch err and read err.message.",
                        ex);
                }

                return hit is null
                    ? BeaconValue.None
                    : BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                    {
                        ["x"] = BeaconValue.Number(hit.X),
                        ["y"] = BeaconValue.Number(hit.Y),
                        ["z"] = BeaconValue.Number(hit.Z),
                        ["name"] = BeaconValue.Text(hit.Name),
                        ["distance"] = BeaconValue.Number(hit.Distance),
                    });
            }
        }

        if (string.Equals(ns, "time", StringComparison.Ordinal))
        {
            if (string.Equals(member, "format", StringComparison.Ordinal))
            {
                if (args.Count != 2 || args[0] is not BeaconNumberValue || args[1] is not BeaconTextValue fmt)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected time.format(stamp, \"HH:mm\"), with a number and text.",
                        origin,
                        "Write time.format(time.stamp, \"HH:mm\").");
                }

                var when = DateTimeOffset.FromUnixTimeSeconds((long)((BeaconNumberValue)args[0]).Value);
                try
                {
                    return BeaconValue.Text(when.ToString(fmt.Value, CultureInfo.InvariantCulture));
                }
                catch (FormatException ex)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        $"I expected a valid format for time.format, but \"{fmt.Value}\" failed: {ex.Message}",
                        origin,
                        "Write time.format(time.stamp, \"HH:mm\").",
                        ex);
                }
            }

            if (string.Equals(member, "ago", StringComparison.Ordinal))
            {
                if (args.Count != 1 || args[0] is not BeaconNumberValue stamp)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected time.ago(stamp), with a number.",
                        origin,
                        "Write time.ago(time.stamp).");
                }

                TimeSpan delta = Clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)stamp.Value);
                if (delta < TimeSpan.Zero)
                    delta = TimeSpan.Zero;

                return BeaconValue.Text(BeaconTimeText.Ago(Clock.UtcNow, DateTimeOffset.FromUnixTimeSeconds((long)stamp.Value)));
            }
        }

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.UnknownName,
            $"Unknown name '{ns}.{member}'.",
            origin,
            string.Equals(ns, "econ", StringComparison.Ordinal)
                ? "The shop library arrives via import, not as a builtin: write import \"lib/econ.mcc\" as econ."
                : $"Check the spelling of '{ns}.{member}'.");
    }

    /// <summary>
    /// The <c>inv.*</c> builtins over the host inventory snapshot.
    /// Matchers are text or map (see <see cref="BeaconMatchers"/>); an empty snapshot matches nothing and writes refuse with no, never with silence.
    /// </summary>
    private async Task<BeaconValue> CallInvAsync(
        string member, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        switch (member)
        {
            case "list":
                RequireArity($"inv.{member}", args, 0, origin);
                return BeaconValue.List(Host.InventorySlots.Select(BeaconMatchers.ToSlotMap).ToList<BeaconValue>());

            case "count":
                {
                    RequireArity($"inv.{member}", args, 1, origin);
                    BeaconValue matcher = RequireMatcher(args[0], $"inv.{member}", origin);
                    long total = 0;
                    foreach (BeaconInvSlot slot in Host.InventorySlots)
                    {
                        if (BeaconMatchers.Matches(matcher, slot))
                            total += slot.Count;
                    }

                    return BeaconValue.Number(total);
                }

            case "has":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'inv.has' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write inv.has(\"compass\") or inv.has(\"compass\", 2).");
                    }

                    BeaconValue matcher = RequireMatcher(args[0], "inv.has", origin);
                    long need = 1;
                    if (args.Count == 2)
                    {
                        if (args[1] is not BeaconNumberValue want || want.Value < 0 || want.Value != Math.Floor(want.Value))
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected a non-negative whole number count for 'inv.has'.",
                                origin,
                                "Write inv.has(\"compass\", 2).");
                        }

                        need = (long)want.Value;
                    }

                    long total = 0;
                    foreach (BeaconInvSlot slot in Host.InventorySlots)
                    {
                        if (BeaconMatchers.Matches(matcher, slot))
                            total += slot.Count;
                    }

                    return BeaconValue.YesNo(total >= need);
                }

            case "find":
                {
                    RequireArity($"inv.{member}", args, 1, origin);
                    BeaconValue matcher = RequireMatcher(args[0], $"inv.{member}", origin);
                    foreach (BeaconInvSlot slot in Host.InventorySlots)
                    {
                        if (BeaconMatchers.Matches(matcher, slot))
                            return BeaconValue.Number(slot.Slot);
                    }

                    return BeaconValue.None;
                }

            case "find_all":
                {
                    RequireArity($"inv.{member}", args, 1, origin);
                    BeaconValue matcher = RequireMatcher(args[0], $"inv.{member}", origin);
                    var slots = new List<BeaconValue>();
                    foreach (BeaconInvSlot slot in Host.InventorySlots)
                    {
                        if (BeaconMatchers.Matches(matcher, slot))
                            slots.Add(BeaconValue.Number(slot.Slot));
                    }

                    return BeaconValue.List(slots);
                }

            case "selected":
                RequireArity($"inv.{member}", args, 0, origin);
                return BeaconValue.Number(Host.SelectedSlot);

            case "select":
                {
                    RequireArity($"inv.{member}", args, 1, origin);
                    int slot = RequireSlotNumber(
                        args[0], origin,
                        "I expected a non-negative whole slot number for 'inv.select'.",
                        "Write inv.select(inv.find(\"compass\")).");
                    if (slot > 8)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a hotbar slot (0-8) for 'inv.select', but found {slot}.",
                            origin,
                            "Write inv.select(inv.find(\"compass\")) with a hotbar slot 0-8.");
                    }

                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.SelectSlotAsync(slot, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"inv.select({slot}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            case "drop":
                {
                    RequireArity($"inv.{member}", args, 0, origin);
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    return BeaconValue.YesNo(await Host.DropAsync(false, ct).ConfigureAwait(false));
                }

            case "drop_stack":
                {
                    RequireArity($"inv.{member}", args, 0, origin);
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    return BeaconValue.YesNo(await Host.DropAsync(true, ct).ConfigureAwait(false));
                }

            case "move":
                {
                    RequireArity($"inv.{member}", args, 2, origin);
                    if (args[0] is not BeaconNumberValue from || from.Value < 0 || from.Value != Math.Floor(from.Value))
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a non-negative whole slot number first for 'inv.move'.",
                            origin,
                            "Write inv.move(inv.find(\"totem_of_undying\"), \"offhand\").");
                    }

                    string to = args[1] switch
                    {
                        BeaconTextValue text => text.Value,
                        BeaconNumberValue number when number.Value >= 0 && number.Value == Math.Floor(number.Value)
                            => ((long)number.Value).ToString(CultureInfo.InvariantCulture),
                        _ => throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a slot number or a named slot (\"offhand\") second for 'inv.move'.",
                            origin,
                            "Write inv.move(5, \"offhand\") or inv.move(5, 40)."),
                    };
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.MoveAsync((int)from.Value, to, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"inv.move({(int)from.Value}, \"{to}\") failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            case "click":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'inv.click' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write inv.click(5) or inv.click(5, \"right\").");
                    }

                    if (args[0] is not BeaconNumberValue slot || slot.Value < 0 || slot.Value != Math.Floor(slot.Value))
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a non-negative whole slot number for 'inv.click'.",
                            origin,
                            "Write inv.click(5).");
                    }

                    string mode = args.Count == 2
                        ? args[1] as BeaconTextValue is { } modeText
                            ? modeText.Value
                            : throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected text for the click mode.",
                                origin,
                                "Write inv.click(5, \"right\").")
                        : "left";
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    return BeaconValue.YesNo(await Host.ClickAsync((int)slot.Value, mode, ct).ConfigureAwait(false));
                }

            case "armor":
                {
                    RequireArity($"inv.{member}", args, 0, origin);
                    var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
                    foreach ((string key, BeaconInvSlot? piece) in Host.ArmorSlots)
                        entries[key] = piece is null ? BeaconValue.None : BeaconMatchers.ToSlotMap(piece);

                    return BeaconValue.Map(entries);
                }

            case "container":
                {
                    RequireArity($"inv.{member}", args, 0, origin);
                    BeaconContainerInfo? container = Host.OpenContainer;
                    if (container is null)
                        return BeaconValue.None;

                    return ToContainerValue(container);
                }

            case "take":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'inv.take' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write inv.take(3) or inv.take(3, 16).");
                    }

                    if (args[0] is not BeaconNumberValue slot || slot.Value < 0 || slot.Value != Math.Floor(slot.Value))
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a non-negative whole container slot number for 'inv.take'.",
                            origin,
                            "Write inv.take(3).");
                    }

                    int count = int.MaxValue;
                    if (args.Count == 2)
                    {
                        if (args[1] is not BeaconNumberValue want || want.Value <= 0 || want.Value != Math.Floor(want.Value))
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected a positive whole count for 'inv.take'.",
                                origin,
                                "Write inv.take(3, 16).");
                        }

                        count = (int)want.Value;
                    }

                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.TakeFromContainerAsync((int)slot.Value, count, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"inv.take({(int)slot.Value}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            case "put":
                {
                    RequireArity($"inv.{member}", args, 1, origin);
                    int slot = RequireSlotNumber(
                        args[0], origin,
                        "I expected a non-negative whole inventory slot number for 'inv.put'.",
                        "Write inv.put(5).");

                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.PutIntoContainerAsync(slot, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"inv.put({slot}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name 'inv.{member}'.",
                    origin,
                    "Use inv.list, inv.count, inv.has, inv.find, inv.find_all, inv.selected, inv.select, inv.drop, inv.drop_stack, inv.move, inv.click, inv.armor, inv.container, inv.take, or inv.put.");
        }
    }

    /// <summary>
    /// The <c>entities.*</c> builtins over the host entity snapshot.
    /// Reads degrade to empty when entity tracking is off, the same way inventory reads degrade; only ids are stable within one session, never across reconnects.
    /// </summary>
    private async Task<BeaconValue> CallEntitiesAsync(
        string member, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        _ = scope;
        _ = ct;

        // Host entity reads are synchronous (session-loop-guarded snapshots); the async shape stays so a future async source does not reshuffle every call site.
        await Task.CompletedTask.ConfigureAwait(false);
        switch (member)
        {
            case "near":
                {
                    if (args.Count is not 0 and not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'entities.near' expects 0, 1, or 2 argument(s) but got {args.Count}.",
                            origin,
                            "Write entities.near() or entities.near(32, 10).");
                    }

                    int radius = args.Count >= 1
                        ? BeaconReadBounds.ClampEntityRadius(args[0], origin, "entities.near radius")
                        : BeaconReadBounds.EntitiesDefaultRadius;
                    int max = args.Count == 2
                        ? BeaconReadBounds.ClampEntityMax(args[1], origin, "entities.near max")
                        : BeaconReadBounds.EntitiesMaxResults;
                    return BeaconValue.List(SortEntities(ReadEntities(radius, origin))
                        .Take(max).Select(ToEntityMap).ToList<BeaconValue>());
                }

            case "of_type":
                {
                    if (args.Count is not 1 and not 2 and not 3)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'entities.of_type' expects 1 to 3 argument(s) but got {args.Count}.",
                            origin,
                            "Write entities.of_type(\"zombie\", 64, 20).");
                    }

                    if (args[0] is not BeaconTextValue type)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'entities.of_type', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write entities.of_type(\"zombie\") or entities.of_type(\"minecraft:player\").");
                    }

                    int radius = args.Count >= 2
                        ? BeaconReadBounds.ClampEntityRadius(args[1], origin, "entities.of_type radius")
                        : BeaconReadBounds.EntitiesDefaultRadius;
                    int max = args.Count == 3
                        ? BeaconReadBounds.ClampEntityMax(args[2], origin, "entities.of_type max")
                        : BeaconReadBounds.EntitiesMaxResults;
                    return BeaconValue.List(SortEntities(ReadEntities(radius, origin))
                        .Where(e => EntityTypeEquals(type.Value, e.TypeId))
                        .Take(max).Select(ToEntityMap).ToList<BeaconValue>());
                }

            case "by_id":
                {
                    RequireArity($"entities.{member}", args, 1, origin);
                    int id = RequireEntityIdArg(args[0], origin, "entities.by_id");
                    BeaconEntityInfo? found = null;
                    try
                    {
                        found = Host.EntityById(id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"entities.by_id({id}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return found is null ? BeaconValue.None : ToEntityMap(found);
                }

            case "nearest":
                {
                    if (args.Count is not 0 and not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'entities.nearest' expects 0 to 2 argument(s) but got {args.Count}.",
                            origin,
                            "Write entities.nearest() or entities.nearest(\"zombie\", 16).");
                    }

                    BeaconValue? matcher = args.Count >= 1 ? RequireEntityMatcher(args[0], "entities.nearest", origin) : null;
                    int radius = args.Count == 2
                        ? BeaconReadBounds.ClampEntityRadius(args[1], origin, "entities.nearest radius")
                        : BeaconReadBounds.EntitiesDefaultRadius;
                    BeaconEntityInfo? nearest = SortEntities(ReadEntities(radius, origin))
                        .FirstOrDefault(e => matcher is null || EntityMatches(matcher, e));
                    return nearest is null ? BeaconValue.None : ToEntityMap(nearest);
                }

            case "count":
                {
                    if (args.Count is not 0 and not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'entities.count' expects 0 to 2 argument(s) but got {args.Count}.",
                            origin,
                            "Write entities.count() or entities.count(\"zombie\", 64).");
                    }

                    BeaconValue? matcher = args.Count >= 1 ? RequireEntityMatcher(args[0], "entities.count", origin) : null;
                    int radius = args.Count == 2
                        ? BeaconReadBounds.ClampEntityRadius(args[1], origin, "entities.count radius")
                        : BeaconReadBounds.EntitiesDefaultRadius;
                    long total = 0;
                    foreach (BeaconEntityInfo entity in ReadEntities(radius, origin))
                    {
                        if (matcher is null || EntityMatches(matcher, entity))
                            total++;
                    }

                    return BeaconValue.Number(total);
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name 'entities.{member}'.",
                    origin,
                    "Use entities.near, entities.of_type, entities.by_id, entities.nearest, or entities.count.");
        }
    }

    /// <summary>
    /// The <c>trade.*</c> builtins over the open merchant window.
    /// A missing merchant window is a catchable error (open one first: right-click a villager with <c>interact</c> and wait for <c>on container_open</c>); a bad index answers no instead.
    /// </summary>
    private async Task<BeaconValue> CallTradeAsync(
        string member, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        switch (member)
        {
            case "list":
                {
                    RequireArity($"trade.{member}", args, 0, origin);
                    return BeaconValue.List(ReadTrades(origin).Select(ToTradeMap).ToList<BeaconValue>());
                }

            case "select":
                {
                    RequireArity($"trade.{member}", args, 1, origin);
                    int index = RequireTradeIndex(args[0], origin, "trade.select");
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.SelectTradeAsync(index, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"trade.select({index}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            case "buy":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'trade.buy' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write trade.buy(0) or trade.buy(0, 3).");
                    }

                    int index = RequireTradeIndex(args[0], origin, "trade.buy");
                    int count = args.Count == 2
                        ? BeaconReadBounds.ClampTradeBuyCount(args[1], origin)
                        : 1;
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    int done;
                    try
                    {
                        done = await Host.BuyTradeAsync(index, count, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"trade.buy({index}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.Number(done);
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name 'trade.{member}'.",
                    origin,
                    "Use trade.list, trade.select, or trade.buy.");
        }
    }

    /// <summary>
    /// The <c>enchant.*</c> builtins over the open enchanting table.
    /// A missing table is a catchable error (open one first: right-click it with <c>world.use</c> and wait for <c>on container_open</c>); a bad slot answers no instead.
    /// </summary>
    private async Task<BeaconValue> CallEnchantAsync(
        string member, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        switch (member)
        {
            case "options":
                {
                    RequireArity($"enchant.{member}", args, 0, origin);
                    return BeaconValue.List(ReadEnchantOptions(origin).Select(ToEnchantMap).ToList<BeaconValue>());
                }

            case "choose":
                {
                    RequireArity($"enchant.{member}", args, 1, origin);
                    int slot = RequireEnchantSlot(args[0], origin);
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    bool ok;
                    try
                    {
                        ok = await Host.ChooseEnchantAsync(slot, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"enchant.choose({slot}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name 'enchant.{member}'.",
                    origin,
                    "Use enchant.options or enchant.choose.");
        }
    }

    /// <summary>
    /// The <c>dialog.*</c> builtins over the shown server dialog (1.21.6+), in configuration or in play.
    /// A missing dialog is a catchable error (wait for <c>on dialog</c> first); a bad button index answers no instead.
    /// <c>dialog.set</c> stages one value, <c>dialog.click</c> submits the staged values, and <c>dialog.answer</c> submits its map in one call.
    /// </summary>
    private async Task<BeaconValue> CallDialogAsync(
        string member, IReadOnlyList<BeaconValue> args, SourceSpan origin,
        BeaconScope scope, CancellationToken ct)
    {
        switch (member)
        {
            case "show":
                {
                    RequireArity($"dialog.{member}", args, 0, origin);
                    BeaconDialogInfo? dialog = ReadDialog(origin);
                    return dialog is null ? BeaconValue.None : ToDialogMap(dialog);
                }

            case "set":
                {
                    RequireArity($"dialog.{member}", args, 2, origin);
                    string key = RequireDialogKey(args[0], origin, "dialog.set");
                    string value = RequireDialogValue(args[1], origin, "dialog.set");
                    bool staged;
                    try
                    {
                        staged = Host.StageDialogInput(key, value);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw DialogMissing(origin, "dialog.set", ex);
                    }

                    if (!staged)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"I expected a dialog input named \"{key}\" for 'dialog.set', but the open dialog has no such input.",
                            origin,
                            "Read dialog.show().inputs for the input keys.");
                    }

                    return BeaconValue.YesNo(true);
                }

            case "click":
                {
                    RequireArity($"dialog.{member}", args, 1, origin);
                    BeaconDialogInfo dialog = RequireOpenDialog(origin, "dialog.click");
                    int button = RequireDialogButton(args[0], dialog, origin, "dialog.click");
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    return BeaconValue.YesNo(await AnswerDialogAsync(null, button, origin, "dialog.click", ct).ConfigureAwait(false));
                }

            case "answer":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'dialog.answer' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write dialog.answer({auth_login_password: settings.login_password}) or dialog.answer({code: \"1234\"}, 2).");
                    }

                    Dictionary<string, string> values = RequireDialogValues(args[0], origin);
                    BeaconDialogInfo dialog = RequireOpenDialog(origin, "dialog.answer");
                    int button = args.Count == 2
                        ? RequireDialogButton(args[1], dialog, origin, "dialog.answer")
                        : 1;
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    return BeaconValue.YesNo(await AnswerDialogAsync(values, button, origin, "dialog.answer", ct).ConfigureAwait(false));
                }

            case "close":
                {
                    RequireArity($"dialog.{member}", args, 0, origin);
                    RequireOpenDialog(origin, "dialog.close");
                    Spend(scope, new SourceSpan(origin.File, origin.Line, origin.Column, origin.Length));
                    try
                    {
                        await Host.CancelDialogAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw DialogMissing(origin, "dialog.close", ex);
                    }

                    return BeaconValue.None;
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name 'dialog.{member}'.",
                    origin,
                    "Use dialog.show, dialog.set, dialog.click, dialog.answer, or dialog.close.");
        }
    }

    private BeaconDialogInfo? ReadDialog(SourceSpan origin)
    {
        try
        {
            return Host.CurrentDialog;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"dialog.show failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }
    }

    private BeaconDialogInfo RequireOpenDialog(SourceSpan origin, string what)
    {
        BeaconDialogInfo? dialog = ReadDialog(origin);
        if (dialog is null)
        {
            throw new BeaconRuntimeException(
                "B4002",
                $"I expected an open dialog for '{what}', but none is open. "
                + "Wait for on dialog first.",
                origin,
                "Wait for on dialog first, inside try/catch err.");
        }

        return dialog;
    }

    private static BeaconRuntimeException DialogMissing(SourceSpan origin, string what, Exception ex) => new(
        "B4002",
        $"I expected an open dialog for '{what}', but none is open. "
        + "Wait for on dialog first.",
        origin,
        "Wait for on dialog first, inside try/catch err.",
        ex);

    private static BeaconValue ToDialogMap(BeaconDialogInfo dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["title"] = BeaconValue.Text(dialog.Title),
            ["body"] = BeaconValue.Text(dialog.Body),
            ["inputs"] = BeaconValue.List(dialog.Inputs.Select(i => BeaconValue.Map(
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["key"] = BeaconValue.Text(i.Key),
                    ["label"] = i.Label is null ? BeaconValue.None : BeaconValue.Text(i.Label),
                    ["kind"] = BeaconValue.Text(i.Kind),
                    ["value"] = BeaconValue.Text(i.Value),
                })).ToList<BeaconValue>()),
            ["buttons"] = BeaconValue.List(dialog.Buttons.Select(b => BeaconValue.Map(
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                {
                    ["index"] = BeaconValue.Number(b.Index),
                    ["label"] = BeaconValue.Text(b.Label),
                })).ToList<BeaconValue>()),
            ["input_keys"] = BeaconValue.List(
                dialog.Inputs.Select(i => BeaconValue.Text(i.Key)).ToList<BeaconValue>()),
            ["registry_id"] = dialog.RegistryId is int id ? BeaconValue.Number(id) : BeaconValue.None,
        });
    }

    private async Task<bool> AnswerDialogAsync(
        IReadOnlyDictionary<string, string>? values, int buttonOneBased,
        SourceSpan origin, string what, CancellationToken ct)
    {
        try
        {
            return await Host.AnswerDialogAsync(values, buttonOneBased, ct).ConfigureAwait(false);
        }
        catch (BeaconRuntimeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"{what} failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }
    }

    private static string RequireDialogKey(BeaconValue value, SourceSpan origin, string what)
    {
        if (value is BeaconTextValue text && !string.IsNullOrWhiteSpace(text.Value))
            return text.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a dialog input key (text) for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            "Write dialog.set(\"auth_login_password\", settings.login_password).");
    }

    private static string RequireDialogValue(BeaconValue value, SourceSpan origin, string what)
    {
        return value switch
        {
            BeaconTextValue text => text.Value,
            BeaconNumberValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BeaconYesNoValue yesno => yesno.Value ? "true" : "false",
            _ => throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected text for the dialog value in '{what}', but found {DescribeValueKind(value)}.",
                origin,
                "Write dialog.set(\"code\", \"1234\")."),
        };
    }

    private static Dictionary<string, string> RequireDialogValues(BeaconValue value, SourceSpan origin)
    {
        if (value is not BeaconMapValue map)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a value map for 'dialog.answer', but found {DescribeValueKind(value)}.",
                origin,
                "Write dialog.answer({auth_login_password: settings.login_password}).");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, BeaconValue entry) in map.Entries)
            values[key] = RequireDialogValue(entry, origin, "dialog.answer");

        return values;
    }

    private static int RequireDialogButton(
        BeaconValue value, BeaconDialogInfo dialog, SourceSpan origin, string what)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (value is BeaconNumberValue number
            && number.Value >= 1 && number.Value == Math.Floor(number.Value))
            return (int)number.Value;

        if (value is BeaconTextValue text)
        {
            int found = 0;
            int matches = 0;
            foreach (BeaconDialogButton button in dialog.Buttons)
            {
                if (string.Equals(button.Label, text.Value, StringComparison.OrdinalIgnoreCase))
                {
                    found = button.Index;
                    matches++;
                }
            }

            if (matches == 1)
                return found;

            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                matches == 0
                    ? $"I expected a button label of the open dialog for '{what}', but found \"{text.Value}\"."
                    : $"The label \"{text.Value}\" matches {matches} buttons for '{what}'; use the button number instead.",
                origin,
                "Read dialog.show().buttons for the button labels and numbers.");
        }

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a 1-based button number or a button label for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            "Write dialog.click(1).");
    }

    private IReadOnlyList<BeaconEntityInfo> ReadEntities(double radius, SourceSpan origin)
    {
        try
        {
            return Host.NearbyEntities(radius);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"entities.near({radius:G}) failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }
    }

    private IReadOnlyList<BeaconTradeOffer> ReadTrades(SourceSpan origin)
    {
        IReadOnlyList<BeaconTradeOffer>? trades;
        try
        {
            trades = Host.OpenTrades;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"trade.list failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }

        if (trades is null)
        {
            throw new BeaconRuntimeException(
                "B4002",
                "I expected an open merchant window for 'trade.list', but none is open. "
                + "Right-click a villager with interact(\"villager\") and wait for on container_open first.",
                origin,
                "Open a merchant window first, inside try/catch err.");
        }

        return trades;
    }

    private IReadOnlyList<BeaconEnchantOption> ReadEnchantOptions(SourceSpan origin)
    {
        IReadOnlyList<BeaconEnchantOption>? options;
        try
        {
            options = Host.EnchantOptions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"enchant.options failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }

        if (options is null)
        {
            throw new BeaconRuntimeException(
                "B4002",
                "I expected an open enchanting table for 'enchant.options', but none is open. "
                + "Right-click one with world.use(x, y, z) and wait for on container_open first.",
                origin,
                "Open an enchanting table first, inside try/catch err.");
        }

        return options;
    }

    private BeaconValue ToEntityMap(BeaconEntityInfo entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        BeaconPosition? self = null;
        try
        {
            self = Host.SelfPosition;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return BeaconValue.Map(new Dictionary<string, BeaconValue>(
            BeaconEventFields.EntityRow(entity, BeaconEventFields.EntityDistance(entity, self)),
            StringComparer.Ordinal));
    }

    private List<BeaconEntityInfo> SortEntities(IEnumerable<BeaconEntityInfo> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        BeaconPosition? self = null;
        try
        {
            self = Host.SelfPosition;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var scored = new List<(BeaconEntityInfo Entity, double Distance, bool Known)>();
        foreach (BeaconEntityInfo entity in entities)
        {
            double? distance = BeaconEventFields.EntityDistance(entity, self);
            scored.Add((entity, distance ?? double.PositiveInfinity, distance.HasValue));
        }

        return scored
            .OrderBy(s => s.Known ? 0 : 1)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.Entity.Id)
            .Select(s => s.Entity)
            .ToList();
    }

    /// <summary>
    /// Reads an x/y/z position out of a row map (<c>entities.*</c>, <c>find_blocks</c>, <c>me.pos</c>).
    /// Anything else refuses with both spellings.
    /// </summary>
    private static (double X, double Y, double Z) RequirePosition(BeaconMapValue row, SourceSpan origin, string verb)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        if (row.Entries.TryGetValue("x", out BeaconValue? x) && x is BeaconNumberValue xn
            && row.Entries.TryGetValue("y", out BeaconValue? y) && y is BeaconNumberValue yn
            && row.Entries.TryGetValue("z", out BeaconValue? z) && z is BeaconNumberValue zn)
            return (xn.Value, yn.Value, zn.Value);

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a row with numeric x, y, z for '{verb}', but found one without.",
            origin,
            $"Write {verb}(120, 65, -40) or {verb}(entities.nearest()).");
    }

    private static BeaconValue RequireEntityMatcher(BeaconValue value, string what, SourceSpan origin)
    {
        if (value is BeaconTextValue or BeaconNumberValue or BeaconMapValue)
            return value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a text, number, or entity row matcher for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            $"Write {what}(\"zombie\") or {what}(entities.nearest()).");
    }

    /// <summary>True when an entity matcher (text, id, or row map) hits an entity.</summary>
    private static bool EntityMatches(BeaconValue matcher, BeaconEntityInfo entity)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(entity);
        switch (matcher)
        {
            case BeaconTextValue text:
                return EntityTextMatches(text.Value, entity);
            case BeaconNumberValue number:
                return number.Value == Math.Floor(number.Value) && (int)number.Value == entity.Id;
            case BeaconMapValue map:
                if (map.Entries.TryGetValue("id", out BeaconValue? id)
                    && id is BeaconNumberValue numberId
                    && numberId.Value == Math.Floor(numberId.Value))
                    return (int)numberId.Value == entity.Id;

                if (map.Entries.TryGetValue("uuid", out BeaconValue? uuid)
                    && uuid is BeaconTextValue uuidText)
                    return string.Equals(uuidText.Value, entity.Uuid, StringComparison.OrdinalIgnoreCase);

                return false;
            default:
                return false;
        }
    }

    private static bool EntityTextMatches(string needle, BeaconEntityInfo entity)
    {
        ArgumentNullException.ThrowIfNull(needle);
        ArgumentNullException.ThrowIfNull(entity);
        string query = needle.Trim();
        if (query.Length == 0)
            return false;

        if (int.TryParse(query, out int id) && id == entity.Id)
            return true;

        if (entity.PlayerName is not null
            && entity.PlayerName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entity.CustomName is not null
            && entity.CustomName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return entity.TypeId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ShortEntityName(entity.TypeId).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EntityTypeEquals(string query, string typeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(typeId);
        string norm = query.Trim().TrimStart(':');
        if (norm.Contains(':'))
            return string.Equals(norm, typeId, StringComparison.OrdinalIgnoreCase);

        return string.Equals(norm, ShortEntityName(typeId), StringComparison.OrdinalIgnoreCase)
            || string.Equals("minecraft:" + norm, typeId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortEntityName(string typeId)
    {
        ArgumentNullException.ThrowIfNull(typeId);
        int colon = typeId.IndexOf(':');
        return colon >= 0 ? typeId[(colon + 1)..] : typeId;
    }

    private static int RequireEntityIdArg(BeaconValue value, SourceSpan origin, string what)
    {
        if (value is BeaconNumberValue number && number.Value >= 0 && number.Value == Math.Floor(number.Value))
            return (int)number.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a non-negative whole entity id for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            $"Write {what}(21339).");
    }

    private static int RequireTradeIndex(BeaconValue value, SourceSpan origin, string what)
    {
        if (value is BeaconNumberValue number && number.Value >= 0 && number.Value == Math.Floor(number.Value))
            return (int)number.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a non-negative whole trade index for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            "Write trade.select(0) with the index from trade.list().");
    }

    private static int RequireEnchantSlot(BeaconValue value, SourceSpan origin)
    {
        if (value is BeaconTextValue text)
        {
            return text.Value.Trim().ToLowerInvariant() switch
            {
                "top" or "0" => 0,
                "middle" or "1" => 1,
                "bottom" or "2" => 2,
                _ => throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.StrictMixedOperands,
                    $"I expected top, middle, or bottom for 'enchant.choose', but found \"{text.Value}\".",
                    origin,
                    "Write enchant.choose(\"bottom\")."),
            };
        }

        if (value is BeaconNumberValue number && number.Value is >= 0 and <= 2 && number.Value == Math.Floor(number.Value))
            return (int)number.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected an enchant option (0 top, 1 middle, 2 bottom) for 'enchant.choose', but found {DescribeValueKind(value)}.",
            origin,
            "Write enchant.choose(\"bottom\").");
    }

    /// <summary>Resolves an entity target (id number or entities.* row map) to its id.</summary>
    private static int RequireEntityTargetId(BeaconValue target, SourceSpan origin, string verb)
    {
        if (target is BeaconNumberValue number && number.Value >= 0 && number.Value == Math.Floor(number.Value))
            return (int)number.Value;

        if (target is BeaconMapValue map
            && map.Entries.TryGetValue("id", out BeaconValue? id)
            && id is BeaconNumberValue mapId
            && mapId.Value >= 0 && mapId.Value == Math.Floor(mapId.Value))
            return (int)mapId.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected an entity id or an entities.* row for '{verb}', but found {DescribeValueKind(target)}.",
            origin,
            $"Write {verb}(entities.nearest(\"zombie\")) or {verb}(\"zombie\").");
    }

    private static string DescribeEntityTarget(BeaconValue target) => target switch
    {
        BeaconNumberValue number => $"#{number.Value:G}",
        BeaconMapValue map when map.Entries.TryGetValue("id", out BeaconValue? id) => $"#{ToDisplayText(id)}",
        _ => ToDisplayText(target),
    };

    private static BeaconValue ToTradeMap(BeaconTradeOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["index"] = BeaconValue.Number(offer.Index),
            ["first"] = ToTradeItemMap(offer.First),
            ["second"] = offer.Second is null ? BeaconValue.None : ToTradeItemMap(offer.Second),
            ["result"] = ToTradeItemMap(offer.Result),
            ["uses"] = BeaconValue.Number(offer.Uses),
            ["max_uses"] = BeaconValue.Number(offer.MaxUses),
            ["sold_out"] = BeaconValue.YesNo(offer.SoldOut),
            ["xp"] = BeaconValue.Number(offer.Xp),
        });
    }

    private static BeaconValue ToTradeItemMap(BeaconTradeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["type"] = BeaconValue.Text(item.Type),
            ["name"] = BeaconValue.Text(item.Name),
            ["count"] = BeaconValue.Number(item.Count),
        });
    }

    private static BeaconValue ToEnchantMap(BeaconEnchantOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["slot"] = BeaconValue.Number(option.Slot),
            ["level"] = option.Level is int level ? BeaconValue.Number(level) : BeaconValue.None,
        });
    }

    private static BeaconValue RequireMatcher(BeaconValue value, string what, SourceSpan origin)
    {
        if (value is BeaconTextValue or BeaconMapValue)
            return value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a text or map matcher for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            $"Write {what}(\"compass\") or {what}({{type: \"compass\"}}).");
    }

    private static readonly HashSet<string> WorldFaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "north", "south", "east", "west",
    };

    private static string RequireFace(BeaconValue value, string what, SourceSpan origin)
    {
        if (value is BeaconTextValue text && WorldFaces.Contains(text.Value.Trim()))
            return text.Value.Trim().ToLowerInvariant();

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected a face (up, down, north, south, east, west) for '{what}', but found {DescribeValueKind(value)}.",
            origin,
            $"Write {what}(100, 63, -30, \"up\").");
    }

    private void RequireWorldWriteGates(string what, SourceSpan origin)
    {
        var missing = new List<string>();
        if (!Gates.Terrain)
            missing.Add("Terrain");

        if (!Gates.Physics)
            missing.Add("Physics");

        if (missing.Count > 0)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.WorldWriteGate,
                $"I expected Gameplay.{string.Join(" and Gameplay.", missing)} on for '{what}', but it is off. " +
                "World writes need the world tracked and simulated.",
                origin,
                "Turn on Gameplay.Terrain (and Gameplay.Physics) in client.toml [Gameplay].");
        }
    }

    private static BeaconMoveOptions RequireMoveOptions(BeaconValue value, SourceSpan origin)
    {
        if (value is not BeaconMapValue map)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a map for move_goto options, but found {DescribeValueKind(value)}.",
                origin,
                "Write move_goto(120, 65, -40, {tolerance: 2, sneak: no, sprint: no}).");
        }

        double tolerance = 1.0;
        bool sneak = false;
        bool sprint = false;
        foreach ((string key, BeaconValue entry) in map.Entries)
        {
            switch (key)
            {
                case "tolerance":
                    if (entry is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value <= 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a positive number for move_goto tolerance.",
                            origin,
                            "Write move_goto(120, 65, -40, {tolerance: 2}).");
                    }

                    tolerance = Math.Min(number.Value, 16);
                    break;
                case "sneak":
                    if (entry is not BeaconYesNoValue sneakValue)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected yes/no for move_goto sneak.",
                            origin,
                            "Write move_goto(120, 65, -40, {sneak: yes}).");
                    }

                    sneak = sneakValue.Value;
                    break;
                case "sprint":
                    if (entry is not BeaconYesNoValue sprintValue)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected yes/no for move_goto sprint.",
                            origin,
                            "Write move_goto(120, 65, -40, {sprint: yes}).");
                    }

                    sprint = sprintValue.Value;
                    break;
                default:
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.UnknownName,
                        $"Unknown move_goto option '{key}'.",
                        origin,
                        "Use tolerance, sneak, or sprint.");
            }
        }

        return new BeaconMoveOptions(tolerance, sneak, sprint);
    }

    private async Task<BeaconValue> CallBuiltinAsync(
        string name, IReadOnlyList<BeaconValue> args, SourceSpan origin, BeaconScope scope, CancellationToken ct)
    {
        switch (name)
        {
            case "text":
                {
                    RequireArity(name, args, 1, origin);
                    return BeaconValue.Text(ToDisplayText(args[0]));
                }

            case "number":
                {
                    RequireArity(name, args, 1, origin);
                    return ToNumber(args[0]);
                }

            case "yesno":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is BeaconYesNoValue yesNo)
                        return BeaconValue.YesNo(yesNo.Value);

                    return BeaconValue.YesNo(!IsFalsy(args[0]));
                }

            case "len":
                {
                    RequireArity(name, args, 1, origin);
                    return args[0] switch
                    {
                        BeaconTextValue text => BeaconValue.Number(text.Value.Length),
                        BeaconListValue list => BeaconValue.Number(list.Items.Count),
                        BeaconMapValue map => BeaconValue.Number(map.Entries.Count),
                        _ => throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text, a list, or a map for 'len', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write len(message) or len([1, 2])."),
                    };
                }

            case "lower":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'lower', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write lower(message).");
                    }

                    return BeaconValue.Text(text.Value.ToLowerInvariant());
                }

            case "upper":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'upper', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write upper(message).");
                    }

                    return BeaconValue.Text(text.Value.ToUpperInvariant());
                }

            case "trim":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'trim', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write trim(message).");
                    }

                    return BeaconValue.Text(text.Value.Trim());
                }

            case "trim_start":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'trim_start', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write trim_start(message).");
                    }

                    return BeaconValue.Text(text.Value.TrimStart());
                }

            case "trim_end":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'trim_end', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write trim_end(message).");
                    }

                    return BeaconValue.Text(text.Value.TrimEnd());
                }

            case "split":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue text || args[1] is not BeaconTextValue sep)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected split(text, sep), both text.",
                            origin,
                            "Write split(message, \",\").");
                    }

                    return BeaconValue.List(text.Value.Split(sep.Value).Select(s => BeaconValue.Text(s)).ToList<BeaconValue>());
                }

            case "join":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconListValue list || args[1] is not BeaconTextValue sep)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected join(list, sep), a list plus text.",
                            origin,
                            "Write join([\"a\", \"b\"], \", \").");
                    }

                    return BeaconValue.Text(string.Join(sep.Value, list.Items.Select(ToDisplayText)));
                }

            case "slice":
                {
                    if (args.Count is not 2 and not 3)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'slice' expects 2 argument(s) but got {args.Count}.",
                            origin,
                            "Write slice(message, 6) or slice(message, 0, 3).");
                    }
                    if (args[0] is BeaconTextValue text)
                    {
                        long start = RequireSliceBound(args[1], origin);
                        long end = args.Count == 3 ? RequireSliceBound(args[2], origin) : text.Value.Length;
                        start = Math.Clamp(start, 0, text.Value.Length);
                        end = Math.Clamp(end, 0, text.Value.Length);
                        if (end < start)
                            end = start;

                        return BeaconValue.Text(text.Value.Substring((int)start, (int)(end - start)));
                    }

                    if (args[0] is BeaconListValue list)
                    {
                        long start = RequireSliceBound(args[1], origin);
                        long end = args.Count == 3 ? RequireSliceBound(args[2], origin) : list.Items.Count;
                        start = Math.Clamp(start, 0, list.Items.Count);
                        end = Math.Clamp(end, 0, list.Items.Count);
                        if (end < start)
                            end = start;

                        return BeaconValue.List(list.Items.Skip((int)start).Take((int)(end - start)).ToList());
                    }

                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        $"I expected text or a list for 'slice', but found {DescribeValueKind(args[0])}.",
                        origin,
                        "Write slice(message, 6).");
                }

            case "replace":
                {
                    RequireArity(name, args, 3, origin);
                    if (args[0] is not BeaconTextValue text
                        || args[1] is not BeaconTextValue oldValue
                        || args[2] is not BeaconTextValue newValue)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected replace(text, old, new), all text.",
                            origin,
                            "Write replace(message, \" ere\", \"ere\").");
                    }

                    if (oldValue.Value.Length == 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a non-empty search for 'replace', but found empty text.",
                            origin,
                            "Write replace(message, \"old\", \"new\").");
                    }

                    return BeaconValue.Text(text.Value.Replace(oldValue.Value, newValue.Value, StringComparison.Ordinal));
                }

            case "replace_first":
                {
                    RequireArity(name, args, 3, origin);
                    if (args[0] is not BeaconTextValue text
                        || args[1] is not BeaconTextValue oldValue
                        || args[2] is not BeaconTextValue newValue)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected replace_first(text, old, new), all text.",
                            origin,
                            "Write replace_first(message, \"old\", \"new\").");
                    }

                    if (oldValue.Value.Length == 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a non-empty search for 'replace_first', but found empty text.",
                            origin,
                            "Write replace_first(message, \"old\", \"new\").");
                    }

                    int hit = text.Value.IndexOf(oldValue.Value, StringComparison.Ordinal);
                    if (hit < 0)
                        return BeaconValue.Text(text.Value);

                    string replaced = text.Value[..hit] + newValue.Value + text.Value[(hit + oldValue.Value.Length)..];
                    return BeaconValue.Text(replaced);
                }

            case "index_of":
                {
                    if (args.Count is not 2 and not 3)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'index_of' expects 2 argument(s) but got {args.Count}.",
                            origin,
                            "Write index_of(message, \"x\") or index_of(message, \"x\", 5).");
                    }

                    if (args[0] is not BeaconTextValue text || args[1] is not BeaconTextValue needle)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected index_of(text, needle), both text.",
                            origin,
                            "Write index_of(message, \"!\").");
                    }

                    long from = 0;
                    if (args.Count == 3)
                        from = RequireIndexFrom(args[2], origin);

                    from = Math.Clamp(from, 0, text.Value.Length);
                    if (needle.Value.Length == 0)
                        return BeaconValue.Number(from);

                    int found = text.Value.IndexOf(needle.Value, (int)from, StringComparison.Ordinal);
                    return found < 0 ? BeaconValue.None : BeaconValue.Number(found);
                }

            case "pad_start":
            case "pad_end":
                {
                    if (args.Count is not 2 and not 3)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function '{name}' expects 2 argument(s) but got {args.Count}.",
                            origin,
                            $"Write {name}(message, 10) or {name}(message, 10, \"0\").");
                    }

                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for '{name}', but found {DescribeValueKind(args[0])}.",
                            origin,
                            $"Write {name}(message, 10).");
                    }

                    long width = RequirePadWidth(args[1], name, origin);
                    string pad = " ";
                    if (args.Count == 3)
                    {
                        if (args[2] is not BeaconTextValue padValue)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                $"I expected text for the pad of '{name}', but found {DescribeValueKind(args[2])}.",
                                origin,
                                $"Write {name}(message, 10, \"0\").");
                        }

                        pad = padValue.Value;
                    }

                    if (pad.Length == 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a non-empty pad for '{name}', but found empty text.",
                            origin,
                            $"Write {name}(message, 10, \"0\").");
                    }

                    if (width <= text.Value.Length)
                        return BeaconValue.Text(text.Value);

                    if (width > RepeatCap)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a width within {RepeatCap} for '{name}', but found {width}.",
                            origin,
                            $"Write {name}(message, 10) with a smaller width.");
                    }

                    int need = (int)(width - text.Value.Length);
                    var fill = new System.Text.StringBuilder(need);
                    while (fill.Length < need)
                    {
                        int take = Math.Min(pad.Length, need - fill.Length);
                        fill.Append(pad, 0, take);
                    }

                    string padded = name == "pad_start"
                        ? fill.ToString() + text.Value
                        : text.Value + fill.ToString();
                    return BeaconValue.Text(padded);
                }

            case "repeat_str":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'repeat_str', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write repeat_str(\"ab\", 3).");
                    }

                    long count = RequireRepeatBuiltinCount(args[1], origin);
                    if (text.Value.Length == 0 || count == 0)
                        return BeaconValue.Text(string.Empty);

                    long output = (long)text.Value.Length * count;
                    if (output > RepeatCap)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected repeat_str output within {RepeatCap} chars, but {text.Value.Length} chars  x {count} would be {output} chars.",
                            origin,
                            "Write repeat_str(\"ab\", 3) with a smaller count.");
                    }

                    var sb = new System.Text.StringBuilder((int)output);
                    for (long i = 0; i < count; i++)
                        sb.Append(text.Value);

                    return BeaconValue.Text(sb.ToString());
                }

            case "escape_regex":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue text)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'escape_regex', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write escape_regex(message).");
                    }

                    return BeaconValue.Text(System.Text.RegularExpressions.Regex.Escape(text.Value));
                }

            case "sort":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconListValue list)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a list for 'sort', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write sort([3, 1, 2]).");
                    }

                    if (list.Items.All(i => i is BeaconNumberValue))
                    {
                        return BeaconValue.List(list.Items.Cast<BeaconNumberValue>()
                            .OrderBy(i => i.Value).ToList<BeaconValue>());
                    }

                    if (list.Items.All(i => i is BeaconTextValue))
                    {
                        return BeaconValue.List(list.Items.Cast<BeaconTextValue>()
                            .OrderBy(i => i.Value, StringComparer.Ordinal).ToList<BeaconValue>());
                    }

                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected a list of all numbers or all text for 'sort', but found mixed kinds.",
                        origin,
                        "Write sort([3, 1, 2]) or sort([\"b\", \"a\"]).");
                }

            case "reverse":
                {
                    RequireArity(name, args, 1, origin);
                    return args[0] switch
                    {
                        BeaconListValue list => BeaconValue.List(list.Items.AsEnumerable().Reverse().ToList()),
                        BeaconTextValue text => BeaconValue.Text(new string(text.Value.Reverse().ToArray())),
                        _ => throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text or a list for 'reverse', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write reverse([1, 2, 3])."),
                    };
                }

            case "unique":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconListValue list)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a list for 'unique', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write unique([1, 2, 2, 3]).");
                    }

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var kept = new List<BeaconValue>();
                    foreach (BeaconValue item in list.Items)
                    {
                        string key = BeaconJson.Stringify(item);
                        if (seen.Add(key))
                            kept.Add(item);
                    }

                    return BeaconValue.List(kept);
                }

            case "assert":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'assert' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write assert(health > 0, \"bot survived\").");
                    }

                    if (args[0] is not BeaconYesNoValue cond)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected yes/no for 'assert', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write assert(x is 1).");
                    }

                    if (!cond.Value)
                    {
                        string label = args.Count == 2 ? ToDisplayText(args[1]) : "assertion failed";
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.Parse,
                            $"Assert failed: {label}.",
                            origin,
                            "Fix the script or the world so the condition holds, then run again.");
                    }

                    return BeaconValue.YesNo(true);
                }

            case "chat_bucket":
                {
                    RequireArity(name, args, 0, origin);
                    if (ChatBucket is null)
                    {
                        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                        {
                            ["burst"] = BeaconValue.Number(BeaconChatBucket.BurstCapacity),
                            ["window_seconds"] = BeaconValue.Number(BeaconChatBucket.RefillInterval.TotalSeconds),
                            ["available"] = BeaconValue.None,
                            ["muted"] = BeaconValue.None,
                        });
                    }

                    return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                    {
                        ["burst"] = BeaconValue.Number(BeaconChatBucket.BurstCapacity),
                        ["window_seconds"] = BeaconValue.Number(BeaconChatBucket.RefillInterval.TotalSeconds),
                        ["available"] = BeaconValue.Number(ChatBucket.Available),
                        ["muted"] = BeaconValue.YesNo(ChatBucket.Muted),
                    });
                }

            case "keys":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconMapValue map)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a map for 'keys', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write keys({a: 1}).");
                    }

                    return BeaconValue.List(map.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => BeaconValue.Text(k)).ToList<BeaconValue>());
                }

            case "values":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconMapValue map)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a map for 'values', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write values({a: 1}).");
                    }

                    return BeaconValue.List(map.Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList());
                }

            case "has_key":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconMapValue map || args[1] is not BeaconTextValue key)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected has_key(map, key), a map plus text.",
                            origin,
                            "Write has_key(quiz, \"running\").");
                    }

                    return BeaconValue.YesNo(map.Entries.ContainsKey(key.Value));
                }

            case "match":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue text || args[1] is not BeaconTextValue pattern)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected match(text, /pattern/), text plus a pattern.",
                            origin,
                            "Write match(text, /(?<n>[0-9]+)/).");
                    }

                    try
                    {
                        Match match = Regex.Match(text.Value, pattern.Value, RegexOptions.None, TimeSpan.FromSeconds(1));
                        if (!match.Success)
                            return BeaconValue.None;

                        return BeaconValue.Map(BuildMatchMap(match));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a valid pattern for 'match', but \"{pattern.Value}\" does not compile: {ex.Message}",
                            origin,
                            "Write match(text, /(?<n>[0-9]+)/).",
                            ex);
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "The 'match' pattern timed out on this text.",
                            origin,
                            "Simplify the pattern and try again.",
                            ex);
                    }
                }

            case "match_all":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue text || args[1] is not BeaconTextValue pattern)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected match_all(text, /pattern/), text plus a pattern.",
                            origin,
                            "Write match_all(text, /(?<n>[0-9]+)/).");
                    }

                    try
                    {
                        MatchCollection hits = Regex.Matches(text.Value, pattern.Value, RegexOptions.None, TimeSpan.FromSeconds(1));
                        var rows = new List<BeaconValue>(hits.Count);
                        foreach (Match hit in hits)
                            rows.Add(BeaconValue.Map(BuildMatchMap(hit)));

                        return BeaconValue.List(rows);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a valid pattern for 'match_all', but \"{pattern.Value}\" does not compile: {ex.Message}",
                            origin,
                            "Write match_all(text, /(?<n>[0-9]+)/).",
                            ex);
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "The 'match_all' pattern timed out on this text.",
                            origin,
                            "Simplify the pattern and try again.",
                            ex);
                    }
                }

            case "random":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value <= 0 || number.Value != Math.Floor(number.Value))
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a positive whole number for 'random', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write random(6).");
                    }

                    return BeaconValue.Number(Rng.Next((int)number.Value));
                }

            case "pick":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconListValue list)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a list for 'pick', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write pick(online_players).");
                    }

                    if (list.Items.Count == 0)
                        return BeaconValue.None;

                    return Rng.Pick(list.Items);
                }

            case "chance":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconNumberValue number || double.IsNaN(number.Value) || number.Value < 0 || number.Value > 1)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a number from 0 to 1 for 'chance'.",
                            origin,
                            "Write chance(0.5).");
                    }

                    return BeaconValue.YesNo(Rng.NextDouble() < number.Value);
                }

            case "min":
            case "max":
                {
                    if (args.Count == 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function '{name}' expects 1 argument(s) but got 0.",
                            origin,
                            $"Write {name}(1, 2) or {name}([1, 2]).");
                    }

                    List<double> numbers = FlattenNumbers(name, args, origin);
                    double result = name == "min" ? numbers.Min() : numbers.Max();
                    return BeaconValue.Number(result);
                }

            case "clamp":
                {
                    RequireArity(name, args, 3, origin);
                    if (args[0] is not BeaconNumberValue v || args[1] is not BeaconNumberValue lo || args[2] is not BeaconNumberValue hi)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected clamp(value, low, high), all numbers.",
                            origin,
                            "Write clamp(health, 0, 20).");
                    }

                    return BeaconValue.Number(Math.Clamp(v.Value, lo.Value, hi.Value));
                }

            case "round":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'round' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write round(2.5) or round(2.567, 2).");
                    }

                    if (args[0] is not BeaconNumberValue v)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a number for 'round', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write round(2.5).");
                    }

                    int digits = 0;
                    if (args.Count == 2)
                    {
                        if (args[1] is not BeaconNumberValue d || d.Value < 0 || d.Value != Math.Floor(d.Value))
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected a non-negative whole number of digits for 'round'.",
                                origin,
                                "Write round(2.567, 2).");
                        }

                        digits = (int)d.Value;
                    }

                    return BeaconValue.Number(Math.Round(v.Value, digits, MidpointRounding.AwayFromZero));
                }

            case "abs":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconNumberValue v)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected a number for 'abs', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write abs(-3).");
                    }

                    return BeaconValue.Number(Math.Abs(v.Value));
                }

            case "log":
                {
                    if (args.Count is not 1 and not 2)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'log' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write log(10).");
                    }

                    if (args[0] is not BeaconNumberValue v || v.Value <= 0)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a positive number for 'log'.",
                            origin,
                            "Write log(10).");
                    }

                    if (args.Count == 1)
                        return BeaconValue.Number(Math.Log(v.Value));

                    if (args[1] is not BeaconNumberValue b || b.Value <= 0 || b.Value == 1)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected a positive base (not 1) for 'log'.",
                            origin,
                            "Write log(8, 2).");
                    }

                    return BeaconValue.Number(Math.Log(v.Value, b.Value));
                }

            case "arg":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue argName)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected arg(name), with text.",
                            origin,
                            "Write arg(\"item\").");
                    }

                    if (CommandArgs is not null
                        && CommandArgs.TryGetValue(argName.Value, out string? bound)
                        && bound is not null)
                        return BeaconValue.Text(bound);

                    return BeaconValue.None;
                }

            case "saved":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue key)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected saved(key), with text.",
                            origin,
                            "Write saved(\"seen\") or {}.");
                    }

                    return _saved.TryGetValue(key.Value, out BeaconValue? found) && found is not null
                        ? found
                        : BeaconValue.None;
                }

            case "online_players":
                {
                    if (args.Count == 0)
                    {
                        return BeaconValue.List(Host.OnlinePlayers(BeaconReadBounds.OnlinePlayersDefaultPage)
                            .Select(p => BeaconValue.Text(p)).ToList<BeaconValue>());
                    }

                    if (args.Count == 1)
                    {
                        int take = BeaconReadBounds.ClampOnlinePlayersPage(args[0], origin);
                        return BeaconValue.List(Host.OnlinePlayers(take)
                            .Select(p => BeaconValue.Text(p)).ToList<BeaconValue>());
                    }

                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected online_players() or online_players(50), with a non-negative number.",
                        origin,
                        "Write for each p in online_players.");
                }

            case "chat_history":
                {
                    if (args.Count == 0)
                    {
                        return BeaconValue.List(Host.ChatHistory(BeaconReadBounds.ChatHistoryMax)
                            .Select(p => BeaconValue.Text(p)).ToList<BeaconValue>());
                    }

                    if (args.Count == 1)
                    {
                        int take = BeaconReadBounds.ClampChatHistory(args[0], origin);
                        return BeaconValue.List(Host.ChatHistory(take)
                            .Select(p => BeaconValue.Text(p)).ToList<BeaconValue>());
                    }

                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        "I expected chat_history() or chat_history(50), with a non-negative number.",
                        origin,
                        "Write chat_history(50).");
                }

            case "last_from":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue player)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'last_from', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write last_from(\"Steve\").");
                    }

                    string? line = Host.LastFrom(player.Value);
                    return line is null ? BeaconValue.None : BeaconValue.Text(line);
                }

            case "count_matching":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue needle || args[1] is not BeaconNumberValue minutes)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected count_matching(text, minutes), text plus a number.",
                            origin,
                            "Write count_matching(\"!bid\", 10).");
                    }

                    DateTimeOffset cutoff = Clock.UtcNow - TimeSpan.FromMinutes(minutes.Value);
                    int hits = 0;
                    foreach (BeaconChatLine line in Host.ChatHistoryDetailed(BeaconReadBounds.ChatHistoryMax))
                    {
                        if (line.When >= cutoff
                            && line.Text.Contains(needle.Value, StringComparison.OrdinalIgnoreCase))
                            hits++;
                    }

                    return BeaconValue.Number(hits);
                }

            case "craft_list":
                {
                    RequireArity(name, args, 0, origin);
                    return BeaconValue.List(Host.CraftList()
                        .Select(r => BeaconValue.Text(r)).ToList<BeaconValue>());
                }

            case "craft_one":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue recipe)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'craft_one', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write craft_one(\"torch\").");
                    }

                    bool ok;
                    try
                    {
                        ok = await Host.CraftOneAsync(recipe.Value, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"craft_one(\"{recipe.Value}\") failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(ok);
                }

            case "move_goto":
                {
                    if (args.Count is not 1 and not 2 and not 3 and not 4)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'move_goto' expects 1 to 4 argument(s) but got {args.Count}.",
                            origin,
                            "Write move_goto(120, 65, -40) or move_goto(entity_row).");
                    }

                    double gx;
                    double gy;
                    double gz;
                    BeaconMoveOptions options;
                    if (args[0] is BeaconMapValue row)
                    {
                        // An entities.* (or find_blocks, or me.pos) row walks to what it describes.
                        (gx, gy, gz) = RequirePosition(row, origin, "move_goto");
                        if (args.Count > 2)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.UnknownName,
                                $"Function 'move_goto' expects 1 argument(s) but got {args.Count}.",
                                origin,
                                "Write move_goto(entity_row) or move_goto(entity_row, {tolerance: 2}).");
                        }

                        options = args.Count == 2
                            ? RequireMoveOptions(args[1], origin)
                            : new BeaconMoveOptions();
                    }
                    else
                    {
                        if (args.Count is not 3 and not 4)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.UnknownName,
                                $"Function 'move_goto' expects 3 argument(s) but got {args.Count}.",
                                origin,
                                "Write move_goto(120, 65, -40) or move_goto(120, 65, -40, {tolerance: 2}).");
                        }

                        if (args[0] is not BeaconNumberValue nx
                            || args[1] is not BeaconNumberValue ny
                            || args[2] is not BeaconNumberValue nz)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                "I expected move_goto(x, y, z), all numbers.",
                                origin,
                                "Write move_goto(120, 65, -40).");
                        }

                        (gx, gy, gz) = (nx.Value, ny.Value, nz.Value);
                        options = args.Count == 4
                            ? RequireMoveOptions(args[3], origin)
                            : new BeaconMoveOptions();
                    }

                    BeaconMovementRunner? runner = Movement;
                    if (runner is null)
                        throw SchedulerMissing(origin, "move_goto", "Load the script through the engine, which binds the movement runner.");

                    try
                    {
                        BeaconValue arrival = await runner.GotoAsync(gx, gy, gz, ScriptId, options, ct).ConfigureAwait(false);
                        return arrival;
                    }
                    finally
                    {
                        Budget.NoteWaitCompleted();
                    }
                }

            case "move_follow":
                {
                    RequireArity(name, args, 1, origin);
                    BeaconMovementRunner? runner = Movement;
                    if (runner is null)
                        throw SchedulerMissing(origin, "move_follow", "Load the script through the engine, which binds the movement runner.");

                    try
                    {
                        // Text follows a player by name; a number or an entities.* row follows that entity id.
                        if (args[0] is BeaconTextValue follow)
                            return await runner.FollowAsync(follow.Value, ScriptId, ct).ConfigureAwait(false);

                        int entityId = RequireEntityTargetId(args[0], origin, "move_follow");
                        return await runner.FollowEntityAsync(entityId, DescribeEntityTarget(args[0]), ScriptId, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Budget.NoteWaitCompleted();
                    }
                }

            case "stop_moving":
                {
                    RequireArity(name, args, 0, origin);
                    Movement?.StopMoving($"stop_moving by {ScriptId}");
                    return BeaconValue.YesNo(true);
                }

            case "look_at":
                {
                    if (args.Count is not 1 and not 3)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.UnknownName,
                            $"Function 'look_at' expects 1 argument(s) but got {args.Count}.",
                            origin,
                            "Write look_at(120, 65, -40) or look_at(entity_row).");
                    }

                    double lx;
                    double ly;
                    double lz;
                    if (args.Count == 1)
                    {
                        if (args[0] is not BeaconMapValue lookRow)
                        {
                            throw new BeaconRuntimeException(
                                BeaconDiagnosticCodes.StrictMixedOperands,
                                $"I expected an entity row for 'look_at', but found {DescribeValueKind(args[0])}.",
                                origin,
                                "Write look_at(120, 65, -40) or look_at(entities.nearest()).");
                        }

                        (lx, ly, lz) = RequirePosition(lookRow, origin, "look_at");
                    }
                    else if (args[0] is not BeaconNumberValue lnx
                        || args[1] is not BeaconNumberValue lny
                        || args[2] is not BeaconNumberValue lnz)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected look_at(x, y, z), all numbers.",
                            origin,
                            "Write look_at(120, 65, -40).");
                    }
                    else
                        (lx, ly, lz) = (lnx.Value, lny.Value, lnz.Value);

                    await Host.LookAtAsync(lx, ly, lz, ct).ConfigureAwait(false);
                    return BeaconValue.YesNo(true);
                }

            case "attack":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is BeaconTextValue target)
                    {
                        await Host.AttackAsync(target.Value, ct).ConfigureAwait(false);
                        return BeaconValue.YesNo(true);
                    }

                    int entityId = RequireEntityTargetId(args[0], origin, "attack");
                    try
                    {
                        await Host.AttackEntityAsync(entityId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"attack({entityId}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(true);
                }

            case "interact":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is BeaconTextValue named)
                    {
                        try
                        {
                            await Host.InteractAsync(named.Value, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw new BeaconRuntimeException(
                                BeaconBridgeErrorCodes.BridgeCall,
                                $"interact(\"{named.Value}\") failed: {ex.Message}",
                                origin,
                                "Wrap it in try/catch err and read err.message.",
                                ex);
                        }

                        return BeaconValue.YesNo(true);
                    }

                    int entityId = RequireEntityTargetId(args[0], origin, "interact");
                    try
                    {
                        await Host.InteractEntityAsync(entityId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"interact({entityId}) failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    return BeaconValue.YesNo(true);
                }

            case "use_in_hand":
                {
                    RequireArity(name, args, 0, origin);
                    await Host.UseInHandAsync(ct).ConfigureAwait(false);
                    return BeaconValue.YesNo(true);
                }

            case "tasks":
                {
                    RequireArity(name, args, 0, origin);
                    if (Scheduler is null)
                        return BeaconValue.List([]);

                    var rows = new List<BeaconValue>();
                    foreach (BeaconTaskInfo info in Scheduler.ListTasks())
                    {
                        rows.Add(BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
                        {
                            ["id"] = BeaconValue.Number(info.Id),
                            ["name"] = BeaconValue.Text(info.Name),
                            ["status"] = BeaconValue.Text(info.Status.ToString().ToLowerInvariant()),
                            ["result"] = info is { Status: BeaconTaskStatus.Completed, Result: not null }
                                ? info.Result
                                : BeaconValue.None,
                        }));
                    }

                    return BeaconValue.List(rows);
                }

            case "file_read":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue path)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for the file path, but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write file_read(\"notes.txt\").");
                    }

                    if (FileJail is null)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.FileJail,
                            "I expected a script data folder for 'file_read', but this session wires none. "
                            + "File IO lives only under '<script-dir>/data/'.",
                            origin,
                            "Load the script through the engine with a configurations folder.");
                    }

                    return BeaconValue.Text(FileJail.ReadText(path.Value, origin));
                }

            case "file_write":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue path || args[1] is not BeaconTextValue body)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected file_write(path, text), both text.",
                            origin,
                            "Write file_write(\"notes.txt\", \"hi\").");
                    }

                    if (FileJail is null)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.FileJail,
                            "I expected a script data folder for 'file_write', but this session wires none. "
                            + "File IO lives only under '<script-dir>/data/'.",
                            origin,
                            "Load the script through the engine with a configurations folder.");
                    }

                    FileJail.WriteText(path.Value, body.Value, origin);
                    return BeaconValue.YesNo(true);
                }

            case "http_get":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue url)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for the URL, but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write http_get(\"https://example.com/path\").");
                    }

                    if (NetGate is null)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.NetGate,
                            "I expected network access for 'http_get', but this session wires no allowlist. "
                            + "Fetches need net.fetch plus a beacon.toml host entry.",
                            origin,
                            "Add the host to beacon.toml:\n[Net]\nAllowedHosts = [\"example.com\"]");
                    }

                    return BeaconValue.Text(await NetGate.GetAsync(url.Value, origin, ct).ConfigureAwait(false));
                }

            case "http_post":
                {
                    RequireArity(name, args, 2, origin);
                    if (args[0] is not BeaconTextValue url || args[1] is not BeaconTextValue body)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            "I expected http_post(url, body), both text.",
                            origin,
                            "Write http_post(\"https://example.com/hook\", \"n=1\").");
                    }

                    if (NetGate is null)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.NetGate,
                            "I expected network access for 'http_post', but this session wires no allowlist. "
                            + "Fetches need net.fetch plus a beacon.toml host entry.",
                            origin,
                            "Add the host to beacon.toml:\n[Net]\nAllowedHosts = [\"example.com\"]");
                    }

                    return BeaconValue.Text(await NetGate.PostAsync(url.Value, body.Value, origin, ct).ConfigureAwait(false));
                }

            case "eat":
                {
                    RequireArity(name, args, 0, origin);
                    bool? eaten;
                    try
                    {
                        eaten = await Host.EatBestAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"eat() failed: {ex.Message}",
                            origin,
                            "Wrap it in try/catch err and read err.message.",
                            ex);
                    }

                    if (eaten is null)
                    {
                        BeaconInvSlot? best = BeaconFood.PickBest(Host.InventorySlots);
                        return BeaconValue.YesNo(best is not null);
                    }

                    return BeaconValue.YesNo(eaten.Value);
                }

            case "json_parse":
                {
                    RequireArity(name, args, 1, origin);
                    if (args[0] is not BeaconTextValue json)
                    {
                        throw new BeaconRuntimeException(
                            BeaconDiagnosticCodes.StrictMixedOperands,
                            $"I expected text for 'json_parse', but found {DescribeValueKind(args[0])}.",
                            origin,
                            "Write json_parse(body).");
                    }

                    try
                    {
                        return BeaconJson.Parse(json.Value);
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"json_parse failed: {ex.Message}",
                            origin,
                            "Check the text with show first; objects become maps, arrays become lists.",
                            ex);
                    }
                }

            case "json_stringify":
                {
                    RequireArity(name, args, 1, origin);
                    try
                    {
                        return BeaconValue.Text(BeaconJson.Stringify(args[0]));
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new BeaconRuntimeException(
                            BeaconBridgeErrorCodes.BridgeCall,
                            $"json_stringify failed: {ex.Message}",
                            origin,
                            "Keep values 32 levels deep or shallower.",
                            ex);
                    }
                }

            default:
                throw new BeaconRuntimeException(
                    BeaconDiagnosticCodes.UnknownName,
                    $"Unknown name '{name}'.",
                    origin,
                    $"Check the spelling of '{name}'.");
        }
    }

    private static void RequireArity(string name, IReadOnlyList<BeaconValue> args, int expected, SourceSpan origin)
    {
        if (args.Count != expected)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.UnknownName,
                $"Function '{name}' expects {expected} argument(s) but got {args.Count}.",
                origin,
                expected == 1 ? $"Write {name}(x)." : $"Write {name}(...).");
        }
    }

    /// <summary>Maximum output chars for <c>repeat_str</c> and padded widths.</summary>
    private const long RepeatCap = 10000;

    private static Dictionary<string, BeaconValue> BuildMatchMap(Match match)
    {
        var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["0"] = BeaconValue.Text(match.Value),
        };
        for (int i = 1; i < match.Groups.Count; i++)
            entries[i.ToString(CultureInfo.InvariantCulture)] = BeaconValue.Text(match.Groups[i].Value);

        foreach (string groupName in match.Groups.Keys.Where(k => !int.TryParse(k, out _)))
            entries[groupName] = BeaconValue.Text(match.Groups[groupName].Value);

        return entries;
    }

    private static long RequireIndexFrom(BeaconValue value, SourceSpan origin)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0 || number.Value != Math.Floor(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative whole number start for 'index_of', but found {DescribeValueKind(value)}.",
                origin,
                "Write index_of(message, \"x\", 5).");
        }

        return (long)number.Value;
    }

    private static long RequirePadWidth(BeaconValue value, string name, SourceSpan origin)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0 || number.Value != Math.Floor(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative whole number width for '{name}', but found {DescribeValueKind(value)}.",
                origin,
                $"Write {name}(message, 10).");
        }

        return (long)number.Value;
    }

    private static long RequireRepeatBuiltinCount(BeaconValue value, SourceSpan origin)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0 || number.Value != Math.Floor(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative whole number count for 'repeat_str', but found {DescribeValueKind(value)}.",
                origin,
                "Write repeat_str(\"ab\", 3).");
        }

        return (long)number.Value;
    }

    private static long RequireSliceBound(BeaconValue value, SourceSpan origin)
    {
        if (value is not BeaconNumberValue number || double.IsNaN(number.Value) || double.IsInfinity(number.Value) || number.Value < 0 || number.Value != Math.Floor(number.Value))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected a non-negative whole number for 'slice', but found {DescribeValueKind(value)}.",
                origin,
                "Write slice(message, 6).");
        }

        return (long)number.Value;
    }

    private static List<double> FlattenNumbers(string name, IReadOnlyList<BeaconValue> args, SourceSpan origin)
    {
        var numbers = new List<double>();
        if (args.Count == 1 && args[0] is BeaconListValue list)
        {
            foreach (BeaconValue item in list.Items)
            {
                if (item is not BeaconNumberValue number)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        $"I expected numbers for '{name}', but found {DescribeValueKind(item)}.",
                        origin,
                        $"Write {name}(1, 2) with numbers only.");
                }

                numbers.Add(number.Value);
            }
        }
        else
        {
            foreach (BeaconValue item in args)
            {
                if (item is not BeaconNumberValue number)
                {
                    throw new BeaconRuntimeException(
                        BeaconDiagnosticCodes.StrictMixedOperands,
                        $"I expected numbers for '{name}', but found {DescribeValueKind(item)}.",
                        origin,
                        $"Write {name}(1, 2) with numbers only.");
                }

                numbers.Add(number.Value);
            }
        }

        if (numbers.Count == 0)
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictMixedOperands,
                $"I expected at least one number for '{name}', but the list was empty.",
                origin,
                $"Write {name}(1, 2).");
        }

        return numbers;
    }

    private static BeaconValue ToNumber(BeaconValue value)
    {
        if (value is BeaconNumberValue number)
            return number;

        if (value is BeaconTextValue text)
        {
            string trimmed = text.Value.Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return BeaconValue.Number(parsed);

            return BeaconValue.None;
        }

        if (value is BeaconYesNoValue yesNo)
            return BeaconValue.Number(yesNo.Value ? 1 : 0);

        return BeaconValue.None;
    }
    #endregion
}

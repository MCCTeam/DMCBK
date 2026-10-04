using DMCBK.Core.Commands;

namespace DMCBK.Core.Beacon;

/// <summary>
/// The Beacon session-facing facade: owns one registry entry per loaded script and runs the lint pipeline over it (tokenizer, header gate, manifest front door, unit check).
/// The public surface is stable: the pipeline grows behind <c>Lint</c> without changing it.
/// </summary>
/// <remarks>
/// Execution path: <c>RunTopLevelAsync</c> registers functions and runs bare top-level statements.
/// <c>InvokeHandlerAsync</c> runs matching <c>on</c> blocks with an event snapshot.
/// Per-script globals live in one <see cref="BeaconInterpreter"/> per script id (never shared).
/// <c>saved</c> memory survives reloads while task state resets (the TOML store backs it).
/// Fuel resets per dispatch.
/// <para/>
/// Runtime wiring for the scheduler, event, and state runtimes: <c>RunScriptAsync</c> lexes, parses, and checks.
/// A failure means diagnostics with no run.
/// It registers <c>on</c> blocks with <c>EventBus</c> (collecting <c>B2001</c> warnings).
/// It registers <c>every</c> blocks with per-script <see cref="BeaconLifecycle"/> timers.
/// It faults <c>saved</c> state in from <c>configurations/beacon/&lt;script&gt;.toml</c> when a config folder is given.
/// It binds the session <c>Detached</c> token, then runs top-level statements serialized on the per-script <see cref="BeaconScheduler"/>.
/// <c>FireEventAsync(string,System.Collections.Generic.IReadOnlyDictionary{string,BeaconValue},System.Threading.CancellationToken)</c> builds snapshots via <c>BeaconScheduler.CopyEventSnapshot</c> and delegates to the bus, whose invoker calls <c>BeaconInterpreter.InvokeHandlerAsync</c> per <c>on</c> block (never engine fan-out).
/// <c>every</c> bodies run via <c>TickEveryAsync</c> (at most one catch-up per timer per tick).
/// <c>HandleReconnectAsync</c> cancels pending sleeps and refires <c>login</c>/<c>reconnect</c>; <c>HandleReload</c> resets tasks and throttles while <c>saved</c> survives.
/// <c>RemoveScript</c> unregisters scheduler, lifecycle, and bus entries.
/// </remarks>

/// <summary>
/// One load's live movement binding: the steering scope, the session executor, and the gameplay gates.
/// Produced by <see cref="BeaconEngine.MovementBinder"/> per load.
/// </summary>
/// <param name="Scope">The steering-lease scope.</param>
/// <param name="Executor">The session navigator call; null keeps immediate arrival.</param>
/// <param name="Gates">Gameplay gates for the load.</param>
public sealed record BeaconMovementBinding(
    IBeaconMovementScope Scope,
    Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>>? Executor,
    BeaconGameplayGates? Gates);

/// <summary>Provides the client runtime operation.</summary>
public sealed class BeaconEngine : IBeaconEngine
{
    IBeaconBridge IBeaconEngine.Bridge => Bridge;

    /// <summary>The only accepted <c># beacon MAJOR</c> version in this build.</summary>
    public const int SupportedBeaconMajor = 1;

    /// <summary>The independent extension metadata for this engine.</summary>
    public BeaconEnvironment Environment { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptRecord> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeState> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeaconScheduler> _schedulers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeaconLifecycle> _lifecycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EveryRegistration>> _everyRegs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<OnceRegistration>> _onceRegs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BeaconScriptCommandSpec>> _commandSpecs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _commandOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly BeaconEventBus _bus;
    private readonly BeaconSharedState _sharedState = new();
    private BeaconSavedState? _savedState;
    private string? _configFolder;

    /// <summary>Builds an engine over injectable host IO, clock, RNG, and fuel seams.</summary>
    public BeaconEngine(
        IBeaconHostServices host,
        IVirtualClock? clock = null,
        ISeededRng? rng = null,
        IFuelBudget? fuel = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        Clock = clock ?? SystemClock.Shared;
        Rng = rng ?? SeededRng.FromEntropy();
        Fuel = fuel ?? new FuelBudget();
        ChatBucket = new BeaconChatBucket(Clock);
        Bridge = new BeaconBridge((name, fields, ct) => FireEventAsync(name, fields, ct), Environment);
        ModuleResolver = new BeaconImportLoader();
        _bus = new BeaconEventBus(InvokeViaInterpreterAsync, Clock);
    }

    /// <summary>Host IO (chat, commands, session reads, storage).</summary>
    public IBeaconHostServices Host { get; }

    /// <summary>Scheduler clock (virtual in tests, wall clock in production).</summary>
    public IVirtualClock Clock { get; }

    /// <summary>Per-dispatch seeded RNG source.</summary>
    public ISeededRng Rng { get; }

    /// <summary>Step accounting budget.</summary>
    public IFuelBudget Fuel { get; }

    /// <summary>
    /// The global chat bucket shared by every script on this engine (burst 8 per 10 s).
    /// Assigned to each interpreter on the load path; replace before loading to share one bucket across engines (tests) or to observe mute state.
    /// </summary>
    public BeaconChatBucket ChatBucket { get; set; }

    /// <summary>
    /// The one movement runner for the whole engine: exactly one Beacon request steers at a time across ALL scripts, so the newest <c>move_goto</c>/<c>move_follow</c> from any script supersedes the running one (whose awaiter gets catchable <c>B4003</c>) no matter which script started it.
    /// Like the global chat bucket, newest-wins is an engine property, not a per-script one: per-script runners let two scripts silently fight over the feet through the session lease instead.
    /// </summary>
    public BeaconMovementRunner SharedMovement { get; } = new(
        new BeaconFreeMovementScope(), BeaconGameplayGates.AllOn);

    private BeaconTraceSink? _tracer;

    /// <summary>
    /// Trace sink: fanned out to every interpreter on set (existing runtimes) and stamped onto new ones at creation, so <c>run --trace</c> sees top-level statements and the synchronous <c>start</c> fire alike.
    /// Null disables tracing.
    /// </summary>
    public BeaconTraceSink? Tracer
    {
        get
        {
            lock (_gate)
                return _tracer;
        }

        set
        {
            lock (_gate)
            {
                _tracer = value;
                foreach (RuntimeState state in _runtimes.Values)
                    state.Interpreter.Tracer = value;
            }
        }
    }

    /// <summary>Net allowlist currently gating fetches (deny-all until a config folder loads).</summary>
    public BeaconNetConfig CurrentNetConfig { get; private set; } = BeaconConfig.Default.Net;

    /// <summary>Shared event bus (per-script handlers, per-script throttle windows).</summary>
    public BeaconEventBus EventBus => _bus;

    /// <summary>
    /// The interop bridge for this engine: C# extension functions, custom events, script exports, and script-registered commands.
    /// One per engine, so one process can host engines with different plugin sets in tests.
    /// </summary>
    public BeaconBridge Bridge { get; }

    /// <summary>
    /// Module resolver for <c>import</c> (file-backed by default; tests swap the overlay).
    /// </summary>
    public IBeaconModuleResolver ModuleResolver { get; set; }

    /// <summary>
    /// Shared <c>%var%</c> store behind <c>vars.beacon.*</c>.
    /// Null leaves <c>vars</c> empty; the live host binds the client store.
    /// </summary>
    public VariableStore? Variables { get; set; }

    /// <summary>
    /// Builds the net gate per load (tests inject a fake transport; production streams with the cap).
    /// Null uses the default gate over the loaded allowlist.
    /// </summary>
    public Func<BeaconNetConfig, BeaconNetGate>? NetGateFactory { get; set; }

    /// <summary>
    /// Binds the live movement executor per load (the in-client host steers through the session navigator; headless runs keep the free scope with immediate arrival).
    /// Invoked inside <see cref="RunScriptAsync"/> after registration, so start-hook and started-task movement dispatches during the load already steer for real.
    /// Null keeps the free scope.
    /// </summary>
    public Func<string, BeaconMovementBinding?>? MovementBinder { get; set; }

    /// <summary>RAM-only cross-script <c>shared</c> store (evaluator writes land with the provider registry).</summary>
    public BeaconSharedState SharedState => _sharedState;

    /// <summary>Persisted per-script <c>saved</c> store, or null before a config folder is given.</summary>
    public BeaconSavedState? SavedState
    {
        get
        {
            lock (_gate)
                return _savedState;
        }
    }

    /// <summary>Currently registered script ids.</summary>
    public IReadOnlyCollection<string> ScriptIds
    {
        get
        {
            lock (_gate)
                return [.. _scripts.Keys];
        }
    }

    /// <summary>
    /// Registers (or replaces, on reload) a script source.
    /// Registration never parses; diagnostics come out of <see cref="Lint"/>.
    /// </summary>
    public void LoadSource(string scriptId, string fileName, string source)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            _scripts[scriptId] = new ScriptRecord(scriptId, fileName, source);
    }

    /// <summary>
    /// Removes a script registration; returns false when <paramref name="scriptId"/> is unknown.
    /// Unregisters the scheduler, lifecycle timers, bus handlers, bridge exports, and script-registered commands for the script.
    /// Throttle windows reset; <c>saved</c> files on disk are kept.
    /// </summary>
    public bool RemoveScript(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        BeaconScheduler? scheduler;
        RuntimeState? state;
        bool removed;
        lock (_gate)
        {
            _schedulers.TryGetValue(scriptId, out scheduler);
            _runtimes.TryGetValue(scriptId, out state);
            _runtimes.Remove(scriptId);
            _schedulers.Remove(scriptId);
            _lifecycles.Remove(scriptId);
            _everyRegs.Remove(scriptId);
            _bus.UnregisterScript(scriptId);
            WithdrawCommandSpecs(scriptId);
            Bridge.WithdrawScript(scriptId);
            _onceRegs.Remove(scriptId);
            removed = _scripts.Remove(scriptId);
        }

        // Outside the gate: stopping must settle running work, not just forget the registrations.
        // Otherwise a stopped script's walk keeps steering the session (an orphaned navigation no later move can preempt) and its waits linger.
        // Cancellation is idempotent, so a concurrent reload racing this remove stays safe.
        try
        {
            scheduler?.HandleReload("unload");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        try
        {
            // Shared runner: only stop the walk when the unloaded script owns it, so unloading one script never yanks the feet out from under another.
            if (state is not null && string.Equals(SharedMovement.ActiveOwner, state.Interpreter.ScriptId, StringComparison.Ordinal))
                SharedMovement.HandleSessionLost("unload");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        return removed;
    }

    /// <summary>
    /// Runs the lint pipeline over a registered script: paste normalization, the tokenizer (strict <c>=</c>, leading-slash, and comment diagnostics), the mandatory-header gate (missing or newer major fails closed with <c>B0001</c>), the manifest front door, then the parser plus structural static checks (boolean conditions, numeric counts, unit placement, end labels, nesting, unknown hooks).
    /// An empty list means clean.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scriptId"/> was never loaded.</exception>
    public IReadOnlyList<BeaconDiagnostic> Lint(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ScriptRecord record;
        lock (_gate)
        {
            if (!_scripts.TryGetValue(scriptId, out ScriptRecord? found) || found is null)
                throw new ArgumentException($"Unknown Beacon script '{scriptId}'.", nameof(scriptId));

            record = found;
        }

        string source = record.Source.StartsWith("\uFEFF", StringComparison.Ordinal)
            ? record.Source[1..]
            : record.Source;
        BeaconHeaderResult header = BeaconPipeline.LexAndParseHeader(
            record.FileName, source, out BeaconLexResult lexed, out int? firstCodeLine);
        if (!header.Ok)
            return header.Diagnostics;

        var diagnostics = new List<BeaconDiagnostic>(lexed.Diagnostics);
        diagnostics.AddRange(header.Diagnostics);
        BeaconParseResult parsed = BeaconParser.Parse(record.FileName, lexed.Tokens, header.Major);
        diagnostics.AddRange(parsed.Diagnostics);
        if (parsed.Script is not null)
        {
            diagnostics.AddRange(BeaconStaticCheck.Check(parsed.Script));
            // Manifest front door (capability gate): declared needs must cover inferred use (B1001 refuses with the paste line); unknown entries warn and load (B1002).
            // Undeclared scripts skip enforcement (zero-config start).
            diagnostics.AddRange(BeaconManifestEnforcer.Check(
                record.FileName, parsed.Script, header.Needs, header.Wants));
        }

        BeaconPipeline.SortByLocation(diagnostics);
        return diagnostics;
    }

    /// <summary>
    /// Runs bare top-level statements for <paramref name="scriptId"/> after a clean <see cref="Lint"/>.
    /// Functions are registered (with <c># desc:</c> lines); <c>on</c>/<c>every</c>/<c>command</c> blocks run through the scheduler and event runtimes and are skipped here.
    /// Never executes when Lint reports errors.
    /// </summary>
    public async Task<BeaconRunResult> RunTopLevelAsync(string scriptId, CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ScriptRecord record = GetRecord(scriptId);
        IReadOnlyList<BeaconDiagnostic> lint = Lint(scriptId);
        IReadOnlyList<BeaconDiagnostic> errors = lint.Where(d => d.Severity == BeaconSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            BeaconDiagnostic first = errors[0];
            return new BeaconRunResult(false, null, lint, [], [], [], first);
        }

        (BeaconScript Script, IReadOnlyList<LexedComment> Comments) parsed = ParseClean(record);
        BeaconScript desugared = BeaconDesugar.Desugar(parsed.Script);
        BeaconInterpreter interpreter = GetOrCreateInterpreter(record);
        lock (_gate)
            interpreter.ResetForNewSource();

        Fuel.Reset();
        BeaconRunResult result = await interpreter.RunTopLevelAsync(desugared, parsed.Comments, ct).ConfigureAwait(false);
        lock (_gate)
            GetRuntimeState(record.ScriptId).Script = desugared;

        if (result.Success)
        {
            PublishExports(record.ScriptId, desugared, interpreter);
            var notes = new List<BeaconDiagnostic>();
            CollectCommandSpecs(record.ScriptId, desugared, parsed.Comments, notes);
            if (notes.Count > 0)
            {
                var mergedNotes = new List<BeaconDiagnostic>(result.Diagnostics);
                mergedNotes.AddRange(notes);
                result = result with { Diagnostics = mergedNotes };
            }
        }

        return MergeLintNotes(lint, result);
    }

    /// <summary>
    /// Invokes matching <c>on</c> blocks for <paramref name="eventName"/> with an event snapshot.
    /// Filters run through the runtime evaluator; a false <c>when</c> skips quietly.
    /// </summary>
    public async Task<BeaconRunResult> InvokeHandlerAsync(
        string scriptId,
        string eventName,
        IReadOnlyDictionary<string, BeaconValue> eventFields,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(eventFields);
        ScriptRecord record = GetRecord(scriptId);
        IReadOnlyList<BeaconDiagnostic> lint = Lint(scriptId);
        if (lint.Any(d => d.Severity == BeaconSeverity.Error))
        {
            BeaconDiagnostic first = lint.First(d => d.Severity == BeaconSeverity.Error);
            return new BeaconRunResult(false, null, lint, [], [], [], first);
        }

        BeaconInterpreter interpreter = GetOrCreateInterpreter(record);
        BeaconScript? script;
        lock (_gate)
            script = GetRuntimeState(record.ScriptId).Script;

        if (script is null)
        {
            (BeaconScript Parsed, IReadOnlyList<LexedComment> Comments) parsed = ParseClean(record);
            script = BeaconDesugar.Desugar(parsed.Parsed);
            Fuel.Reset();
            _ = await interpreter.RunTopLevelAsync(script, parsed.Comments, ct).ConfigureAwait(false);
            lock (_gate)
                GetRuntimeState(record.ScriptId).Script = script;
        }

        List<OnBlock> matches = script.Decls
            .OfType<OnBlock>()
            .Where(b => string.Equals(b.EventName, eventName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return new BeaconRunResult(true, null, [], [], [], [], null);

        Fuel.Reset();
        var output = new List<string>();
        var passthrough = new List<BeaconPassthroughLog>();
        var echo = new List<string>();
        var diagnostics = new List<BeaconDiagnostic>();
        bool suppressed = false;
        foreach (OnBlock block in matches)
        {
            BeaconRunResult one = await interpreter.InvokeHandlerAsync(block, eventFields, ct).ConfigureAwait(false);
            output.AddRange(one.LocalOutput);
            passthrough.AddRange(one.PassthroughLog);
            echo.AddRange(one.LocalEcho);
            diagnostics.AddRange(one.Diagnostics);
            suppressed = suppressed || one.EventSuppressed;
            if (!one.Success)
                return new BeaconRunResult(false, one.ReturnValue, diagnostics, output, passthrough, echo, one.Error, suppressed);
        }

        return new BeaconRunResult(true, null, diagnostics, output, passthrough, echo, null, suppressed);
    }

    /// <summary>Snapshot of per-script globals for tests and timeout dumps.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scriptId"/> was never loaded.</exception>
    public IReadOnlyDictionary<string, BeaconValue> GetGlobalsSnapshot(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        GetRecord(scriptId);
        lock (_gate)
        {
            if (_runtimes.TryGetValue(scriptId, out RuntimeState? state) && state is not null)
                return state.Interpreter.GetGlobalsSnapshot();

            return new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        }
    }

    /// <summary>Returns the per-script scheduler, or null before <see cref="RunScriptAsync"/>.</summary>
    public BeaconScheduler? GetScheduler(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            return _schedulers.TryGetValue(scriptId, out BeaconScheduler? scheduler) ? scheduler : null;
        }
    }

    /// <summary>Returns the per-script lifecycle, or null before <see cref="RunScriptAsync"/>.</summary>
    public BeaconLifecycle? GetLifecycle(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            return _lifecycles.TryGetValue(scriptId, out BeaconLifecycle? lifecycle) ? lifecycle : null;
        }
    }

    /// <summary>
    /// Rebinds the movement scope, executor, and gates for <paramref name="scriptId"/> (live session wiring; headless runs keep the free scope with immediate arrival).
    /// Every load rebinds the ONE shared runner: all loads carry the same session, so the binding is identical and newest-wins stays global.
    /// </summary>
    public void BindMovement(
        string scriptId,
        IBeaconMovementScope scope,
        Func<BeaconMoveTarget, CancellationToken, Task<BeaconMoveArrival>>? executor = null,
        BeaconGameplayGates? gates = null)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(scope);
        GetRecord(scriptId);
        lock (_gate)
        {
            if (_runtimes.TryGetValue(scriptId, out RuntimeState? state) && state is not null)
            {
                SharedMovement.Rebind(scope, executor, gates);
                state.Interpreter.Movement = SharedMovement;
            }
        }
    }

    /// <summary>Script-registered commands of <paramref name="scriptId"/> (empty when none).</summary>
    public IReadOnlyList<BeaconScriptCommandSpec> GetCommandSpecs(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        lock (_gate)
        {
            return _commandSpecs.TryGetValue(scriptId, out List<BeaconScriptCommandSpec>? specs)
                ? specs.ToList()
                : [];
        }
    }

    /// <summary>Every script-registered command name and its owning script (for host Brigadier wiring).</summary>
    public IReadOnlyDictionary<string, string> CommandOwners
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_commandOwners, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Runs a script-registered command block with <paramref name="args"/> bound (fresh dispatch on the owning script scheduler).
    /// Unknown commands fail with B2002 naming the command.
    /// </summary>
    public async Task<BeaconRunResult> InvokeScriptCommandAsync(
        string commandName,
        IReadOnlyDictionary<string, string> args,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(args);
        string scriptId;
        BeaconScriptCommandSpec spec;
        BeaconInterpreter interpreter;
        BeaconScheduler? scheduler;
        lock (_gate)
        {
            if (!_commandOwners.TryGetValue(commandName, out string? owner) || owner is null
                || !_commandSpecs.TryGetValue(owner, out List<BeaconScriptCommandSpec>? specs))
            {
                var diagnostic = new BeaconDiagnostic(
                    BeaconDiagnosticCodes.UnknownName,
                    BeaconSeverity.Error,
                    $"Unknown script command '/{commandName}'.",
                    new SourceSpan(commandName + ".mcc", 1, 1, 0),
                    "List script commands with /scripts list.");
                return new BeaconRunResult(false, null, [diagnostic], [], [], [], diagnostic);
            }

            spec = specs.FirstOrDefault(s => string.Equals(s.Name, commandName, StringComparison.OrdinalIgnoreCase))
                ?? specs[0];
            scriptId = owner;
            interpreter = GetRuntimeState(scriptId).Interpreter;
            _schedulers.TryGetValue(scriptId, out scheduler);
        }

        if (scheduler is null)
            return await interpreter.InvokeCommandAsync(spec.Block, args, ct).ConfigureAwait(false);

        return await scheduler.EnqueueAsync(
            async runCt =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, ct);
                return await interpreter.InvokeCommandAsync(spec.Block, args, linked.Token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls an exported script function from the C# side (the <c>CallFunctionAsync</c> path): arguments cross as the six kinds, the body runs as a fresh dispatch on the owning script scheduler, and the return value crosses back the same way.
    /// A missing script, a missing export, or an arity mismatch raises <see cref="BeaconRuntimeException"/> with <see cref="BeaconBridgeErrorCodes.BridgeCall"/> naming all three (the SDK maps it to its own call exception).
    /// A read-only value export returns its snapshot with no dispatch.
    /// </summary>
    public async Task<object?> CallExportFromHostAsync(
        string scriptId,
        string function,
        IReadOnlyList<object?> args,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        ArgumentNullException.ThrowIfNull(args);
        if (Bridge.TryGetValueExport(scriptId, function, out BeaconExportedValue? valueExport) && valueExport is not null)
        {
            if (args.Count != 0)
            {
                throw new BeaconRuntimeException(
                    BeaconBridgeErrorCodes.BridgeCall,
                    $"Call '{scriptId}.{function}' failed: '{function}' is a value export, not a function.",
                    new SourceSpan(scriptId + ".mcc", 1, 1, 0),
                    $"Call '{scriptId}.{function}' with no arguments.");
            }

            return BeaconMarshal.FromBeacon(BeaconScheduler.CopyBeaconValue(valueExport.Value));
        }

        if (!Bridge.TryGetExport(scriptId, function, out BeaconExportedFunction? export) || export is null)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Call '{scriptId}.{function}' failed: script '{scriptId}' exports no function '{function}'.",
                new SourceSpan(scriptId + ".mcc", 1, 1, 0),
                $"Add 'export function {function}()' to {scriptId}.mcc.");
        }

        List<BeaconValue> beaconArgs;
        try
        {
            beaconArgs = args.Select(BeaconMarshal.ToBeacon).ToList();
        }
        catch (InvalidOperationException ex)
        {
            throw new BeaconRuntimeException(
                BeaconBridgeErrorCodes.BridgeCall,
                $"Call '{scriptId}.{function}' failed: {ex.Message}",
                new SourceSpan(scriptId + ".mcc", 1, 1, 0),
                "Pass only text, numbers, booleans, lists, string-keyed maps, or null.",
                ex);
        }

        BeaconScheduler? scheduler;
        lock (_gate)
            _schedulers.TryGetValue(scriptId, out scheduler);

        BeaconValue result;
        if (scheduler is null)
            result = await export.Interpreter.InvokeExportFromHostAsync(export, beaconArgs, ct).ConfigureAwait(false);
        else
        {
            result = await scheduler.EnqueueAsync(
                async runCt =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, ct);
                    return await export.Interpreter.InvokeExportFromHostAsync(export, beaconArgs, linked.Token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        FlushSaved(scriptId);
        return BeaconMarshal.FromBeacon(result);
    }

    /// <summary>Snapshot of a script's in-memory <c>saved</c> table (for tests and timeout dumps).</summary>
    public IReadOnlyDictionary<string, BeaconValue> GetSavedSnapshot(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        GetRecord(scriptId);
        lock (_gate)
        {
            if (_runtimes.TryGetValue(scriptId, out RuntimeState? state) && state is not null)
                return state.Interpreter.SnapshotSaved();

            return new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Overlays one live setting onto a running script (<c>scripts config id key value</c>).
    /// False when the script is not running (the file overlay still applies on next load).
    /// </summary>
    public bool SetRuntimeSetting(string scriptId, string key, BeaconValue value)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_runtimes.TryGetValue(scriptId, out RuntimeState? state) && state is not null)
            {
                var copy = new Dictionary<string, BeaconValue>(state.Interpreter.Settings, StringComparer.Ordinal);
                copy[key] = value;
                state.Interpreter.Settings = copy;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Loads and runs one script: lexes, parses, and static-checks <paramref name="source"/> for <paramref name="scriptId"/>.
    /// A failure means diagnostics with no run and no registration change.
    /// Registers <c>on</c> blocks with the event bus (collecting <c>B2001</c> warnings).
    /// Registers <c>every</c> blocks with the per-script lifecycle.
    /// Faults <c>saved</c> state in from <paramref name="configFolder"/> when given.
    /// Binds <paramref name="detachedCt"/>, then runs top-level statements serialized on the per-script scheduler.
    /// </summary>
    /// <param name="scriptId">Plain script name (state file name when a config folder is given).</param>
    /// <param name="source">Full script source including the <c># beacon 1</c> header.</param>
    /// <param name="hostServices">Host seam override for this load; null means the engine host.</param>
    /// <param name="configFolder">Configurations folder for <c>saved</c> persistence; null keeps RAM-only.</param>
    /// <param name="detachedCt">Session <c>Detached</c> token; binds scheduler disconnect on fire.</param>
    /// <param name="fileName">File name for spans; defaults to <c>&lt;scriptId&gt;.mcc</c>.</param>
    /// <param name="ct">Run cancellation.</param>
    public async Task<BeaconRunResult> RunScriptAsync(
        string scriptId,
        string source,
        IBeaconHostServices? hostServices = null,
        string? configFolder = null,
        CancellationToken detachedCt = default,
        string? fileName = null,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(source);
        string resolvedFile = string.IsNullOrWhiteSpace(fileName) ? scriptId + ".mcc" : fileName!;

        IBeaconHostServices effectiveHost = hostServices ?? Host;
        string? resolvedConfig = configFolder;
        BeaconSavedState? savedForFlush = null;
        BeaconScheduler scheduler;
        BeaconLifecycle lifecycle;
        BeaconInterpreter interpreter;
        BeaconScript desugared;
        IReadOnlyList<LexedComment> comments;
        IReadOnlyList<BeaconDiagnostic> lint;
        List<OnBlock> onBlocks;
        List<EveryBlock> everyBlocks;
        List<OnceBlock> onceBlocks;

        lock (_gate)
            _scripts[scriptId] = new ScriptRecord(scriptId, resolvedFile, source);

        lint = Lint(scriptId);
        IReadOnlyList<BeaconDiagnostic> errors = lint.Where(d => d.Severity == BeaconSeverity.Error).ToList();
        if (errors.Count > 0)
            return new BeaconRunResult(false, null, lint, [], [], [], errors[0]);

        ScriptRecord record = GetRecord(scriptId);
        (BeaconScript Script, IReadOnlyList<LexedComment> Comments) parsed = ParseClean(record);
        desugared = BeaconDesugar.Desugar(parsed.Script);
        comments = parsed.Comments;
        onBlocks = desugared.Decls.OfType<OnBlock>().ToList();
        everyBlocks = desugared.Decls.OfType<EveryBlock>().ToList();
        onceBlocks = desugared.Decls.OfType<OnceBlock>().ToList();

        lock (_gate)
        {
            if (!ReferenceEquals(effectiveHost, Host))
            {
                var replacement = new BeaconInterpreter(scriptId, resolvedFile, effectiveHost, Clock, Rng, Fuel);
                if (_runtimes.TryGetValue(scriptId, out RuntimeState? existing) && existing is not null)
                    replacement.RestoreSaved(existing.Interpreter.SnapshotSaved());

                _runtimes[scriptId] = new RuntimeState(replacement);
            }

            interpreter = GetOrCreateInterpreter(record);
            scheduler = GetOrCreateScheduler(scriptId);
            lifecycle = GetOrCreateLifecycle(scriptId);

            if (resolvedConfig is not null)
            {
                string full = Path.GetFullPath(resolvedConfig);
                if (_savedState is null || !string.Equals(_configFolder, full, StringComparison.Ordinal))
                {
                    _savedState = new BeaconSavedState(full);
                    _configFolder = full;
                }

                savedForFlush = _savedState;
            }
            else
                savedForFlush = _savedState;

            interpreter.ResetForNewSource();
        }

        if (savedForFlush is not null)
        {
            try
            {
                savedForFlush.Load(scriptId);
                IReadOnlyDictionary<string, BeaconValue> disk = savedForFlush.Snapshot(scriptId);
                lock (_gate)
                    interpreter.RestoreSaved(disk);
            }
            catch (InvalidDataException ex)
            {
                var diagnostic = new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"Beacon saved-state file for script '{scriptId}' does not parse: {ex.Message}",
                    new SourceSpan(resolvedFile, 1, 1, 0),
                    "Delete the file under configurations/beacon/ to reset the script.");
                var withLint = new List<BeaconDiagnostic>(lint) { diagnostic };
                return new BeaconRunResult(false, null, withLint, [], [], [], diagnostic);
            }
        }

        _bus.RegisterScriptHandlers(scriptId, onBlocks, out IReadOnlyList<BeaconDiagnostic> hookWarnings);

        // Safety seams: the scheduler (tasks plus capped waits), the global chat bucket, the fs jail, and the net gate.
        // Without a configurations folder the jail stays unwired (file IO refuses) and the allowlist stays deny-all (fail closed).
        BeaconFileJail? fileJail = null;
        BeaconNetGate netGate;
        if (resolvedConfig is not null)
        {
            try
            {
                string full = Path.GetFullPath(resolvedConfig);
                fileJail = new BeaconFileJail(BeaconFileJail.ScriptsDataDir(full));
                BeaconConfigLoadResult beaconConfig = new BeaconConfigLoader(full).Load(generateMissing: false);
                netGate = NetGateFactory is not null
                    ? NetGateFactory(beaconConfig.Config.Net)
                    : new BeaconNetGate(beaconConfig.Config.Net);
                lock (_gate)
                    CurrentNetConfig = beaconConfig.Config.Net;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                fileJail = null;
                netGate = NetGateFactory is not null
                    ? NetGateFactory(BeaconConfig.Default.Net)
                    : new BeaconNetGate(BeaconConfig.Default.Net);
            }
        }
        else
        {
            netGate = NetGateFactory is not null
                ? NetGateFactory(BeaconConfig.Default.Net)
                : new BeaconNetGate(BeaconConfig.Default.Net);
        }

        interpreter.Scheduler = scheduler;
        interpreter.ChatBucket = ChatBucket;
        interpreter.FileJail = fileJail;
        interpreter.NetGate = netGate;
        if (MovementBinder?.Invoke(scriptId) is { } movement)
            BindMovement(scriptId, movement.Scope, movement.Executor, movement.Gates);

        var settingsWarnings = new List<BeaconDiagnostic>();
        try
        {
            BeaconHeaderResult header = BeaconHeader.Parse(resolvedFile, record.Source);
            if (header.Ok)
            {
                interpreter.Settings = BeaconScriptSettings.Resolve(
                    scriptId, header.Settings, resolvedConfig, settingsWarnings);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            settingsWarnings.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.ManifestWantsUnavailable,
                BeaconSeverity.Warning,
                $"Beacon settings for script '{scriptId}' unreadable ({ex.Message}); defaults apply.",
                new SourceSpan(resolvedFile, 1, 1, 0),
                "Delete the settings file to restore commented defaults."));
        }

        List<EveryRegistration> regs;
        lock (_gate)
        {
            lifecycle.HandleReload();
            scheduler.HandleReload();
            _bus.ResetThrottle(scriptId);
            regs = [];
            for (int i = 0; i < everyBlocks.Count; i++)
            {
                EveryBlock block = everyBlocks[i];
                TimeSpan interval = ResolveEveryInterval(block);
                string timerName = $"every-{i}";
                BeaconEveryTimer timer = lifecycle.RegisterEvery(timerName, interval);
                regs.Add(new EveryRegistration(timer, block));
            }

            _everyRegs[scriptId] = regs;
            var onceRegs = new List<OnceRegistration>(onceBlocks.Count);
            for (int i = 0; i < onceBlocks.Count; i++)
            {
                OnceBlock block = onceBlocks[i];
                TimeSpan delay = ResolveOnceDelay(block);
                BeaconOneShot oneShot = lifecycle.RegisterOnce($"in-{i}", delay);
                onceRegs.Add(new OnceRegistration(oneShot, block));
            }

            _onceRegs[scriptId] = onceRegs;
            if (detachedCt.CanBeCanceled)
                scheduler.BindDetached(detachedCt);
        }

        BeaconRunResult topResult = await scheduler.EnqueueAsync(
            async runCt =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, ct);
                Fuel.Reset();
                return await interpreter.RunTopLevelAsync(desugared, comments, linked.Token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        lock (_gate)
            GetRuntimeState(scriptId).Script = desugared;

        FlushSaved(scriptId);

        var mergedDiagnostics = new List<BeaconDiagnostic>(lint.Where(d => d.Severity != BeaconSeverity.Error));
        mergedDiagnostics.AddRange(hookWarnings);
        mergedDiagnostics.AddRange(settingsWarnings);
        mergedDiagnostics.AddRange(topResult.Diagnostics);
        BeaconPipeline.SortByLocation(mergedDiagnostics);

        BeaconRunResult merged = topResult with { Diagnostics = mergedDiagnostics };
        if (!topResult.Success)
            return merged;

        PublishExports(scriptId, desugared, interpreter);
        CollectCommandSpecs(scriptId, desugared, comments, mergedDiagnostics);

        if (HasLifecycleHook(onBlocks, "start"))
        {
            // Scoped to the loading script on purpose: a broadcast here would re-fire every loaded script's `on start` on each load (duplicate patrols, double welcomes).
            // Session events (`login`, `reconnect`) stay broadcast at their own call sites.
            BeaconFireResult startFire = await _bus.FireEventAsync(
                "start",
                new Dictionary<string, BeaconValue>(StringComparer.Ordinal),
                ct,
                scriptId).ConfigureAwait(false);
            mergedDiagnostics.AddRange(startFire.Diagnostics);
            // The start hook's effects belong to the load: merge its local output, echo, and passthrough into the run result so `show` in `on start` (the empty template's load announcement) is observable on the returned result, not dropped.
            var startOutput = new List<string>(merged.LocalOutput);
            var startPassthrough = new List<BeaconPassthroughLog>(merged.PassthroughLog);
            var startEcho = new List<string>(merged.LocalEcho);
            foreach (BeaconHandlerFire handler in startFire.Handlers)
            {
                if (handler.Result is null)
                    continue;

                startOutput.AddRange(handler.Result.LocalOutput);
                startPassthrough.AddRange(handler.Result.PassthroughLog);
                startEcho.AddRange(handler.Result.LocalEcho);
            }

            merged = merged with
            {
                Diagnostics = mergedDiagnostics,
                LocalOutput = startOutput,
                PassthroughLog = startPassthrough,
                LocalEcho = startEcho,
            };
            return merged;
        }

        return merged with { Diagnostics = mergedDiagnostics };
    }

    /// <summary>
    /// Fires <paramref name="eventName"/> to every matching <c>on</c> block across all scripts.
    /// The incoming <paramref name="fields"/> are deep-copied via <see cref="BeaconScheduler.CopyEventSnapshot"/> before the bus defensively copies again, so a caller mutating its map mid-handler (including across <c>wait</c>) can never move the handler snapshot.
    /// Throttles apply per script; <c>stop event</c> suppression is reported on the returned <see cref="BeaconFireResult"/>.
    /// </summary>
    public async Task<BeaconFireResult> FireEventAsync(
        string eventName,
        IReadOnlyDictionary<string, BeaconValue> fields,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(fields);
        lock (_gate)
        {
            // Dialogs arrive during configuration, before the reconnect handshake marks the schedulers connected again.
            // Drop nothing for them: the fields are a snapshot and the dialog verbs drive the live client.
            if (!string.Equals(eventName, "dialog", StringComparison.OrdinalIgnoreCase)
                && _schedulers.Count > 0 && _schedulers.Values.All(s => !s.IsConnected))
                return new BeaconFireResult(eventName, false, [], [], [], []);
        }

        IReadOnlyDictionary<string, BeaconValue> snapshot = BeaconScheduler.CopyEventSnapshot(fields);
        BeaconFireResult result = await _bus.FireEventAsync(eventName, snapshot, ct).ConfigureAwait(false);
        foreach (string touched in result.Handlers.Select(h => h.ScriptId).Distinct(StringComparer.Ordinal))
            FlushSaved(touched);

        return result;
    }

    /// <summary>
    /// Fires <paramref name="eventName"/> after validating <paramref name="scriptId"/> is loaded.
    /// Dispatch is still global (all scripts see the event); the id exists so callers can attribute a session source to the script that owns it and fail fast on typos.
    /// Prefer the two-argument overload for true broadcast.
    /// </summary>
    public Task<BeaconFireResult> FireEventAsync(
        string scriptId,
        string eventName,
        IReadOnlyDictionary<string, BeaconValue> fields,
        CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(fields);
        GetRecord(scriptId);
        return FireEventAsync(eventName, fields, ct);
    }

    /// <summary>
    /// Advances every per-script timer once and runs each due <c>every</c> body via its scheduler.
    /// Missed intervals coalesce upstream into a single catch-up fire per timer, then the normal cadence resumes.
    /// Returns all fires in script-then-registration order.
    /// </summary>
    public async Task<IReadOnlyList<BeaconEveryRun>> TickEveryAsync(CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        List<(string ScriptId, BeaconScheduler Scheduler, BeaconLifecycle Lifecycle, BeaconInterpreter Interpreter, List<EveryRegistration> Regs)> plan;
        lock (_gate)
        {
            plan = [];
            foreach (string id in _scripts.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (_schedulers.TryGetValue(id, out BeaconScheduler? scheduler)
                    && _lifecycles.TryGetValue(id, out BeaconLifecycle? lifecycle)
                    && _runtimes.TryGetValue(id, out RuntimeState? runtime)
                    && runtime is not null
                    && _everyRegs.TryGetValue(id, out List<EveryRegistration>? regs))
                    plan.Add((id, scheduler, lifecycle, runtime.Interpreter, regs.ToList()));
            }
        }

        var runs = new List<BeaconEveryRun>();
        foreach ((string scriptId, BeaconScheduler scheduler, BeaconLifecycle lifecycle, BeaconInterpreter interpreter, List<EveryRegistration> regs) in plan)
        {
            IReadOnlyList<BeaconEveryFire> fires = lifecycle.EveryTick();
            foreach (BeaconEveryFire fire in fires)
            {
                ct.ThrowIfCancellationRequested();
                EveryRegistration? reg = regs.FirstOrDefault(r => string.Equals(r.Timer.Name, fire.Name, StringComparison.Ordinal));
                if (reg is null)
                    continue;

                BeaconRunResult result = await scheduler.EnqueueAsync(
                    async runCt =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, ct);
                        Fuel.Reset();
                        return await interpreter.InvokeEveryAsync(reg.Block, linked.Token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
                FlushSaved(scriptId);
                runs.Add(new BeaconEveryRun(scriptId, fire, result));
            }
        }

        return runs;
    }

    /// <summary>
    /// Advances every per-script one-shot once and runs each due <c>in</c> body via its scheduler.
    /// A fired one-shot settles and never refires; pending one-shots cancel on reconnect (stated here and on the language reference: a reconnect drops them like pending <c>wait</c> sleeps).
    /// Returns all fires in script-then-registration order.
    /// </summary>
    public async Task<IReadOnlyList<BeaconOnceRun>> TickOnceAsync(CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        List<(string ScriptId, BeaconScheduler Scheduler, BeaconLifecycle Lifecycle, BeaconInterpreter Interpreter, List<OnceRegistration> Regs)> plan;
        lock (_gate)
        {
            plan = [];
            foreach (string id in _scripts.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (_schedulers.TryGetValue(id, out BeaconScheduler? scheduler)
                    && _lifecycles.TryGetValue(id, out BeaconLifecycle? lifecycle)
                    && _runtimes.TryGetValue(id, out RuntimeState? runtime)
                    && runtime is not null
                    && _onceRegs.TryGetValue(id, out List<OnceRegistration>? regs))
                    plan.Add((id, scheduler, lifecycle, runtime.Interpreter, regs.ToList()));
            }
        }

        var runs = new List<BeaconOnceRun>();
        foreach ((string scriptId, BeaconScheduler scheduler, BeaconLifecycle lifecycle, BeaconInterpreter interpreter, List<OnceRegistration> regs) in plan)
        {
            IReadOnlyList<BeaconOneShot> due = lifecycle.OnceTick();
            foreach (BeaconOneShot oneShot in due)
            {
                ct.ThrowIfCancellationRequested();
                OnceRegistration? reg = regs.FirstOrDefault(r => string.Equals(r.OneShot.Name, oneShot.Name, StringComparison.Ordinal));
                if (reg is null)
                    continue;

                BeaconRunResult result = await scheduler.EnqueueAsync(
                    async runCt =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt, ct);
                        Fuel.Reset();
                        return await interpreter.InvokeOnceAsync(reg.Block, linked.Token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
                FlushSaved(scriptId);
                runs.Add(new BeaconOnceRun(scriptId, oneShot.Name, result));
            }
        }

        return runs;
    }

    /// <summary>Drops all sessions: pending sleeps abort and dispatches are refused until reconnect.</summary>
    public void HandleDisconnect(string reason = "disconnect")
    {
        using IDisposable environmentScope = Environment.Enter();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        List<BeaconScheduler> schedulers;
        List<BeaconMovementRunner> runners;
        lock (_gate)
        {
            schedulers = _schedulers.Values.ToList();
            runners = _runtimes.Values.Select(s => s.Interpreter.Movement).OfType<BeaconMovementRunner>()
                .Append(SharedMovement).Distinct().ToList();
        }

        foreach (BeaconScheduler scheduler in schedulers)
            scheduler.HandleDisconnect(reason);

        foreach (BeaconMovementRunner runner in runners)
            runner.HandleSessionLost(reason);
    }

    /// <summary>
    /// Re-arms after a disconnect: cancels anything still pending from the dead session, keeps <c>every</c> cadence (next tick coalesces the outage into one catch-up), keeps throttles and <c>saved</c> as-is, then refires <c>login</c> and <c>reconnect</c> hooks.
    /// </summary>
    public async Task<BeaconFireResult> HandleReconnectAsync(CancellationToken ct = default)
    {
        using IDisposable environmentScope = Environment.Enter();
        List<BeaconScheduler> schedulers;
        List<BeaconLifecycle> lifecycles;
        lock (_gate)
        {
            schedulers = _schedulers.Values.ToList();
            lifecycles = _lifecycles.Values.ToList();
        }

        foreach (BeaconScheduler scheduler in schedulers)
            scheduler.HandleReconnect();

        foreach (BeaconLifecycle lifecycle in lifecycles)
            lifecycle.HandleReconnect();

        BeaconFireResult login = await FireEventAsync(
            "login",
            new Dictionary<string, BeaconValue>(StringComparer.Ordinal),
            ct).ConfigureAwait(false);
        BeaconFireResult reconnect = await FireEventAsync(
            "reconnect",
            new Dictionary<string, BeaconValue>(StringComparer.Ordinal),
            ct).ConfigureAwait(false);

        var handlers = new List<BeaconHandlerFire>(login.Handlers.Count + reconnect.Handlers.Count);
        handlers.AddRange(login.Handlers);
        handlers.AddRange(reconnect.Handlers);
        var diagnostics = new List<BeaconDiagnostic>(login.Diagnostics.Count + reconnect.Diagnostics.Count);
        diagnostics.AddRange(login.Diagnostics);
        diagnostics.AddRange(reconnect.Diagnostics);
        var debug = new List<string>(login.DebugLog.Count + reconnect.DebugLog.Count);
        debug.AddRange(login.DebugLog);
        debug.AddRange(reconnect.DebugLog);
        var notes = new List<string>(login.SuppressionNotes.Count + reconnect.SuppressionNotes.Count);
        notes.AddRange(login.SuppressionNotes);
        notes.AddRange(reconnect.SuppressionNotes);
        return new BeaconFireResult("reconnect", login.Suppressed || reconnect.Suppressed, handlers, diagnostics, debug, notes);
    }

    /// <summary>
    /// Resets task state for <paramref name="scriptId"/> (or all scripts when null): cancels pending tasks, clears scheduler registries (ids keep increasing), clears lifecycle hooks/timers and re-registers <c>every</c> blocks from the last clean script, and resets throttle windows.
    /// Only <c>saved</c> survives (reloaded from disk when a config folder is set).
    /// </summary>
    public void HandleReload(string? scriptId = null)
    {
        using IDisposable environmentScope = Environment.Enter();
        List<string> targets;
        lock (_gate)
        {
            targets = scriptId is null
                ? _scripts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
                : [scriptId];
            foreach (string id in targets)
                GetRecord(id);
        }

        foreach (string id in targets)
        {
            BeaconScheduler? scheduler;
            BeaconLifecycle? lifecycle;
            BeaconScript? script;
            lock (_gate)
            {
                _schedulers.TryGetValue(id, out scheduler);
                _lifecycles.TryGetValue(id, out lifecycle);
                script = _runtimes.TryGetValue(id, out RuntimeState? runtime) ? runtime?.Script : null;
            }

            scheduler?.HandleReload();
            lifecycle?.HandleReload();
            _bus.ResetThrottle(id);

            if (lifecycle is not null && script is not null)
            {
                List<EveryBlock> everyBlocks = script.Decls.OfType<EveryBlock>().ToList();
                var regs = new List<EveryRegistration>(everyBlocks.Count);
                for (int i = 0; i < everyBlocks.Count; i++)
                {
                    TimeSpan interval = ResolveEveryInterval(everyBlocks[i]);
                    BeaconEveryTimer timer = lifecycle.RegisterEvery($"every-{i}", interval);
                    regs.Add(new EveryRegistration(timer, everyBlocks[i]));
                }

                List<OnceBlock> onceBlocks = script.Decls.OfType<OnceBlock>().ToList();
                var onceRegs = new List<OnceRegistration>(onceBlocks.Count);
                for (int i = 0; i < onceBlocks.Count; i++)
                {
                    TimeSpan delay = ResolveOnceDelay(onceBlocks[i]);
                    BeaconOneShot oneShot = lifecycle.RegisterOnce($"in-{i}", delay);
                    onceRegs.Add(new OnceRegistration(oneShot, onceBlocks[i]));
                }

                lock (_gate)
                {
                    _everyRegs[id] = regs;
                    _onceRegs[id] = onceRegs;
                }
            }

            BeaconSavedState? saved;
            lock (_gate)
                saved = _savedState;

            if (saved is not null)
            {
                try
                {
                    saved.Load(id);
                    IReadOnlyDictionary<string, BeaconValue> disk = saved.Snapshot(id);
                    lock (_gate)
                    {
                        if (_runtimes.TryGetValue(id, out RuntimeState? runtime) && runtime is not null)
                            runtime.Interpreter.RestoreSaved(disk);
                    }
                }
                catch (InvalidDataException)
                {
                    // A corrupt file stays on disk for the owner to fix; in-memory keeps running.
                }
            }
        }
    }

    private async Task<BeaconRunResult> InvokeViaInterpreterAsync(
        string scriptId, OnBlock block, IReadOnlyDictionary<string, BeaconValue> snapshot, CancellationToken ct)
    {
        using IDisposable environmentScope = Environment.Enter();
        BeaconInterpreter interpreter;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(scriptId, out RuntimeState? state) || state is null)
                throw new ArgumentException($"Unknown Beacon script '{scriptId}'.", nameof(scriptId));

            interpreter = state.Interpreter;
        }

        Fuel.Reset();
        try
        {
            return await interpreter.InvokeHandlerAsync(block, snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // A throwing handler must not break its siblings or the subscription feeding the bus: host IO (a chat send on a dying transport, a read mid-reconnect) raises raw errors the handler body does not catch, and without this the bus aborts the whole fire.
            // Cancellation still propagates so waits and grace windows abort promptly.
            var diagnostic = new BeaconDiagnostic(
                "B4002",
                BeaconSeverity.Error,
                Commands.CommandStrings.ScriptsEventHandlerFailed(block.EventName, ex.Message),
                block.Span.Origin,
                Commands.CommandStrings.ScriptsWorldCatchSuggestion);
            return new BeaconRunResult(false, null, [diagnostic], [], [], [], diagnostic);
        }
    }

    /// <summary>Publishes <c>export function</c> definitions to the bridge (reloads replace).</summary>
    private void PublishExports(string scriptId, BeaconScript script, BeaconInterpreter interpreter)
    {
        using IDisposable environmentScope = Environment.Enter();
        Bridge.WithdrawScript(scriptId);
        foreach (FunctionDef function in script.Decls.OfType<FunctionDef>().Where(f => f.IsExport))
        {
            Bridge.PublishExport(new BeaconExportedFunction(
                scriptId,
                function.Name,
                function.Params.Select(p => p.Name).ToList(),
                function.Body,
                function.Span.Origin,
                interpreter));
        }

        foreach ((string name, (BeaconValue value, SourceSpan span)) in interpreter.ValueExports)
        {
            Bridge.PublishValueExport(new BeaconExportedValue(
                scriptId, name, BeaconScheduler.CopyBeaconValue(value), span));
        }
    }

    /// <summary>
    /// Collects <c>command</c> blocks into script-registered specs.
    /// A pattern without its leading slash is a load error naming the block; a name already owned by another script keeps the first owner and warns naming both (the loader refuses nothing it can route).
    /// </summary>
    private void CollectCommandSpecs(
        string scriptId, BeaconScript script, IReadOnlyList<LexedComment> comments, List<BeaconDiagnostic> diagnostics)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            WithdrawCommandSpecs(scriptId);
            foreach (CommandBlock block in script.Decls.OfType<CommandBlock>())
            {
                BeaconScriptCommandSpec? spec = BeaconCommandSpec.Parse(scriptId, block, comments);
                if (spec is null)
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        $"I expected command \"{block.Pattern}\" to start with '/' followed by a name and <args>.",
                        block.PatternSpan.Origin,
                        "Write command \"/price <item>\" with the leading slash and <name> arguments."));
                    continue;
                }

                if (_commandOwners.TryGetValue(spec.Name, out string? owner)
                    && owner is not null
                    && !string.Equals(owner, scriptId, StringComparison.Ordinal))
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.UnknownName,
                        BeaconSeverity.Warning,
                        $"Script command '/{spec.Name}' is already owned by script '{owner}'; " +
                        $"script '{scriptId}' keeps its block but the console routes to '{owner}'.",
                        block.PatternSpan.Origin,
                        $"Rename one of the two '/{spec.Name}' commands."));
                    continue;
                }

                if (!_commandSpecs.TryGetValue(scriptId, out List<BeaconScriptCommandSpec>? list))
                {
                    list = [];
                    _commandSpecs[scriptId] = list;
                }

                list.Add(spec);
                _commandOwners[spec.Name] = scriptId;
            }
        }
    }

    private void WithdrawCommandSpecs(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        if (_commandSpecs.TryGetValue(scriptId, out List<BeaconScriptCommandSpec>? specs))
        {
            foreach (BeaconScriptCommandSpec spec in specs)
            {
                if (_commandOwners.TryGetValue(spec.Name, out string? owner)
                    && string.Equals(owner, scriptId, StringComparison.Ordinal))
                    _commandOwners.Remove(spec.Name);
            }

            _commandSpecs.Remove(scriptId);
        }
    }

    private void FlushSaved(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        BeaconSavedState? saved; BeaconInterpreter? interpreter;
        lock (_gate)
        {
            saved = _savedState;
            interpreter = _runtimes.TryGetValue(scriptId, out RuntimeState? state) ? state?.Interpreter : null;
        }

        if (saved is null || interpreter is null)
            return;

        IReadOnlyDictionary<string, BeaconValue> snapshot = interpreter.SnapshotSaved();
        foreach (KeyValuePair<string, BeaconValue> entry in snapshot)
            saved.Set(scriptId, entry.Key, entry.Value);

        saved.Save(scriptId);
    }

    private static bool HasLifecycleHook(IEnumerable<OnBlock> blocks, string name)
        => blocks.Any(b => string.Equals(b.EventName, name, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan ResolveEveryInterval(EveryBlock block)
    {
        BeaconExpr count = block.Interval;
        while (count is ParenExpr paren)
            count = paren.Inner;

        if (count is NumberLiteral number)
            return BeaconLifecycle.EveryInterval(number.Value, block.Unit);

        return BeaconLifecycle.EveryInterval(60, "seconds");
    }

    private static TimeSpan ResolveOnceDelay(OnceBlock block)
    {
        BeaconExpr count = block.Delay;
        while (count is ParenExpr paren)
            count = paren.Inner;

        if (count is NumberLiteral number)
            return BeaconLifecycle.EveryInterval(number.Value, block.Unit);

        return BeaconLifecycle.EveryInterval(30, "seconds");
    }

    private ScriptRecord GetRecord(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (!_scripts.TryGetValue(scriptId, out ScriptRecord? found) || found is null)
                throw new ArgumentException($"Unknown Beacon script '{scriptId}'.", nameof(scriptId));

            return found;
        }
    }

    private BeaconInterpreter GetOrCreateInterpreter(ScriptRecord record)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(record.ScriptId, out RuntimeState? state) || state is null)
            {
                var interpreter = new BeaconInterpreter(record.ScriptId, record.FileName, Host, Clock, Rng, Fuel);
                interpreter.Tracer = _tracer;
                interpreter.Bridge = Bridge;
                interpreter.ModuleResolver = ModuleResolver;
                interpreter.Variables = Variables;
                state = new RuntimeState(interpreter);
                interpreter.Movement = SharedMovement;
                _runtimes[record.ScriptId] = state;
            }
            else
            {
                state.Interpreter.Bridge = Bridge;
                state.Interpreter.ModuleResolver = ModuleResolver;
                state.Interpreter.Variables = Variables;
                state.Interpreter.Movement ??= SharedMovement;
            }

            return state.Interpreter;
        }
    }

    private BeaconScheduler GetOrCreateScheduler(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (!_schedulers.TryGetValue(scriptId, out BeaconScheduler? scheduler) || scheduler is null)
            {
                scheduler = new BeaconScheduler(scriptId, Clock, Fuel, Rng);
                _schedulers[scriptId] = scheduler;
            }

            return scheduler;
        }
    }

    private BeaconLifecycle GetOrCreateLifecycle(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        lock (_gate)
        {
            if (!_lifecycles.TryGetValue(scriptId, out BeaconLifecycle? lifecycle) || lifecycle is null)
            {
                lifecycle = new BeaconLifecycle(Clock);
                _lifecycles[scriptId] = lifecycle;
            }

            return lifecycle;
        }
    }

    private RuntimeState GetRuntimeState(string scriptId)
    {
        using IDisposable environmentScope = Environment.Enter();
        if (!_runtimes.TryGetValue(scriptId, out RuntimeState? state) || state is null)
            throw new ArgumentException($"Unknown Beacon script '{scriptId}'.", nameof(scriptId));

        return state;
    }

    private static (BeaconScript Script, IReadOnlyList<LexedComment> Comments) ParseClean(ScriptRecord record)
    {
        string source = record.Source.StartsWith("\uFEFF", StringComparison.Ordinal)
            ? record.Source[1..]
            : record.Source;
        BeaconLexResult lexed = BeaconLexer.Lex(record.FileName, source);
        BeaconHeaderResult header = BeaconHeader.Parse(record.FileName, lexed.NormalizedSource, FirstCodeLine(lexed), lexed.Comments);
        BeaconParseResult parsed = BeaconParser.Parse(record.FileName, lexed.Tokens, header.Major);
        if (parsed.Script is null)
            throw new InvalidOperationException($"Beacon script '{record.ScriptId}' parsed to null after a clean Lint.");

        return (parsed.Script, lexed.Comments);
    }

    private static int? FirstCodeLine(BeaconLexResult lexed)
    {
        foreach (BeaconToken token in lexed.Tokens)
        {
            if (token.Kind != BeaconTokenKind.EndOfFile)
                return token.Span.Line;
        }

        return null;
    }

    private static BeaconRunResult MergeLintNotes(IReadOnlyList<BeaconDiagnostic> lint, BeaconRunResult result)
    {
        IReadOnlyList<BeaconDiagnostic> notes = lint.Where(d => d.Severity != BeaconSeverity.Error).ToList();
        if (notes.Count == 0)
            return result;

        var merged = new List<BeaconDiagnostic>(notes);
        merged.AddRange(result.Diagnostics);
        return result with { Diagnostics = merged };
    }

    private sealed class RuntimeState(BeaconInterpreter interpreter)
    {
        public BeaconInterpreter Interpreter { get; } = interpreter;
        public BeaconScript? Script { get; set; }

        /// <summary>Per-script movement runner (newest-wins steering survives across dispatches).</summary>
        public BeaconMovementRunner Movement { get; } = new(
            new BeaconFreeMovementScope(), BeaconGameplayGates.AllOn);
    }

    private sealed record EveryRegistration(BeaconEveryTimer Timer, EveryBlock Block);

    private sealed record OnceRegistration(BeaconOneShot OneShot, OnceBlock Block);

    private sealed record ScriptRecord(string ScriptId, string FileName, string Source);
}

/// <summary>One executed <c>every</c> firing: which script/timer fired and its run result.</summary>
/// <param name="ScriptId">Owning script.</param>
/// <param name="Fire">Timer fire record (name, catch-up flag, missed count).</param>
/// <param name="Result">Interpreter run result for the timer body.</param>
public sealed record BeaconEveryRun(string ScriptId, BeaconEveryFire Fire, BeaconRunResult Result);

/// <summary>One executed one-shot firing: which script/timer fired and its run result.</summary>
/// <param name="ScriptId">Owning script.</param>
/// <param name="Name">Timer name from registration.</param>
/// <param name="Result">Interpreter run result for the timer body.</param>
public sealed record BeaconOnceRun(string ScriptId, string Name, BeaconRunResult Result);

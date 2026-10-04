using System.Globalization;

namespace DMCBK.Core.Beacon;

/// <summary>
/// One traced statement execution: where it ran, what it was, and the locals afterwards.
/// Line and column come from <see cref="SourceSpan.Origin"/>, so desugared nodes still point at the user's own text (through the span chain).
/// </summary>
/// <param name="Seq">Monotonic sequence number (gaps mean the cap dropped lines).</param>
/// <param name="Handler">Dispatch attribution (<c>top-level</c>, <c>on chat</c>, ...).</param>
/// <param name="File">Source file name.</param>
/// <param name="Line">1-based line in the user's source.</param>
/// <param name="Column">1-based column in the user's source.</param>
/// <param name="Kind">Short statement kind (<c>set</c>, <c>say</c>, <c>if</c>, ...).</param>
/// <param name="Depth">Interpreter call depth.</param>
/// <param name="Locals">Truncated <c>name=value</c> scope dump after the statement ran.</param>
public sealed record BeaconTraceLine(
    long Seq,
    string Handler,
    string File,
    int Line,
    int Column,
    string Kind,
    int Depth,
    string Locals);

/// <summary>
/// The trace sink: the interpreter calls it around every executed statement and disposes the returned scope when the statement completes (or throws).
/// Null disables tracing.
/// The scope snapshot is taken at dispose, so locals show the statement's effect.
/// </summary>
/// <param name="Statement">The statement about to run (spans chain back to user text).</param>
/// <param name="Scope">The live scope; snapshotted when the returned scope is disposed.</param>
/// <param name="Handler">Dispatch attribution for abort-dump-style context.</param>
/// <param name="Depth">Interpreter call depth.</param>
public delegate IDisposable? BeaconTraceSink(
    BeaconStatement Statement, BeaconScope Scope, string Handler, int Depth);

/// <summary>
/// Collects <see cref="BeaconTraceLine"/> rows behind a <see cref="BeaconTraceSink"/> and renders the structured per-line log <c>run --trace</c> prints.
/// Capped: past <see cref="MaxLines"/> new rows are dropped and counted, so an overnight loop cannot grow memory without bound.
/// </summary>
public sealed class BeaconTraceCollector
{
    /// <summary>Maximum retained trace rows; older rows are kept, newer ones drop past the cap.</summary>
    public const int MaxLines = 4096;

    private readonly object _gate = new();
    private readonly List<BeaconTraceLine> _lines = [];
    private long _seq;

    /// <summary>Rows dropped past the cap.</summary>
    public int Dropped
    {
        get
        {
            lock (_gate)
                return _dropped;
        }
    }

    private int _dropped;

    /// <summary>The sink to hand to the interpreter or engine.</summary>
    public BeaconTraceSink Sink => Trace;

    /// <summary>Retained rows, oldest first.</summary>
    public IReadOnlyList<BeaconTraceLine> Lines
    {
        get
        {
            lock (_gate)
                return _lines.ToList();
        }
    }

    private IDisposable? Trace(BeaconStatement statement, BeaconScope scope, string handler, int depth)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        return new Frame(this, statement, scope, handler, depth);
    }

    private void Add(BeaconStatement statement, BeaconScope scope, string handler, int depth)
    {
        SourceSpan origin = statement.Span.Origin;
        string locals;
        try
        {
            locals = BeaconDiagnostics.SummarizeScope(scope);
        }
        catch
        {
            locals = "(locals unavailable)";
        }

        lock (_gate)
        {
            long seq = ++_seq;
            if (_lines.Count >= MaxLines)
            {
                _dropped++;
                return;
            }

            _lines.Add(new BeaconTraceLine(
                seq, handler, origin.File, origin.Line, origin.Column,
                KindOf(statement), depth, locals));
        }
    }

    private sealed class Frame(
        BeaconTraceCollector owner, BeaconStatement statement, BeaconScope scope, string handler, int depth)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner.Add(statement, scope, handler, depth);
        }
    }

    /// <summary>Short kind name for a statement (<c>set</c>, <c>say</c>, ...).</summary>
    public static string KindOf(BeaconStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement switch
        {
            SetStmt => "set",
            SayStmt => "say",
            WhisperStmt => "whisper",
            ServerStmt => "server",
            DisconnectStmt => "disconnect",
            ShowStmt => "show",
            WaitStmt => "wait",
            StopStmt => "stop",
            SkipStmt => "skip",
            ReturnStmt => "return",
            SaveStmt => "save",
            LockStmt => "lock",
            StartStmt => "start",
            AwaitStmt => "await",
            CancelTaskStmt => "cancel-task",
            IfStmt => "if",
            WhileStmt => "while",
            RepeatStmt => "repeat",
            ForStmt => "for",
            TryStmt => "try",
            ExprStmt => "expr",
            NestedDeclStmt => "decl",
            _ => "stmt",
        };
    }

    /// <summary>
    /// Renders the structured per-line log: sequence, handler, <c>file:line:col</c>, kind, depth, and locals.
    /// One line per row; stable columns for overnight-log grepping.
    /// </summary>
    public string RenderText()
    {
        IReadOnlyList<BeaconTraceLine> lines = Lines;
        var sb = new System.Text.StringBuilder();
        foreach (BeaconTraceLine line in lines)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{line.Seq:0000} {line.Handler} {line.File}:{line.Line}:{line.Column} {line.Kind} depth={line.Depth} locals: {line.Locals}\n");
        }

        if (Dropped > 0)
            sb.Append(CultureInfo.InvariantCulture, $"... {Dropped} further line(s) dropped past the {MaxLines}-line cap\n");

        return sb.ToString().TrimEnd('\n');
    }
}

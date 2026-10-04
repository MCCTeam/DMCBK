namespace DMCBK.Core.Beacon;

/// <summary>One evaluated REPL line: display output or a failure, plus the replay seed.</summary>
/// <param name="Success">True when the line evaluated without an error.</param>
/// <param name="Output">Display text (<c>show</c> lines); null when the line printed nothing.</param>
/// <param name="Error">Human-readable failure text with a paste-ready suggestion, if any.</param>
/// <param name="Seed">The dispatch seed, for bit-for-bit replay of RNG inside the line.</param>
/// <param name="Diagnostics">Interpreter warnings/notes collected while the line ran.</param>
public sealed record BeaconReplResult(
    bool Success,
    string? Output,
    string? Error,
    int Seed,
    IReadOnlyList<BeaconDiagnostic> Diagnostics);

/// <summary>
/// The console-free line-eval engine behind <c>/scripts repl</c>.
/// One persistent <see cref="BeaconInterpreter"/> backs every evaluation, so <c>set</c> locals survive across lines and <see cref="LocalsText"/> can dump them for inspection.
/// Each invocation evaluates exactly one line (commands here are request/response; there is deliberately no blocking read loop): a line starting with a statement keyword runs as-is, anything else is read as an expression and wrapped in <c>show</c>, which is the default output so a first run cannot spam public chat (<c>say</c> still sends when typed explicitly, against the live session).
/// An explicit <c>seed</c> replays a failed line's RNG bit-for-bit.
/// </summary>
public sealed class BeaconRepl
{
    private static readonly HashSet<string> StatementStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "say", "whisper", "server", "show", "log", "wait", "stop", "skip",
        "break", "continue", "return", "save", "lock", "start", "await", "cancel",
        "if", "while", "repeat", "for", "try",
    };

    private const string ReplFileName = "repl.mcc";

    private readonly BeaconInterpreter _interpreter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Builds a REPL over a host (the live-session adapter in-client, a fake in tests).</summary>
    public BeaconRepl(
        IBeaconHostServices host,
        IVirtualClock? clock = null,
        ISeededRng? rng = null,
        IFuelBudget? fuel = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _interpreter = new BeaconInterpreter("repl", ReplFileName, host, clock, rng, fuel);
    }

    /// <summary>Seed of the most recent dispatch, for replaying a failed line bit-for-bit.</summary>
    public int LastSeed => _interpreter.DispatchSeed;

    /// <summary>Persistent locals across evaluated lines (what <c>set</c> has defined).</summary>
    public IReadOnlyDictionary<string, BeaconValue> Locals => _interpreter.GetGlobalsSnapshot();

    /// <summary>Renders persistent locals as <c>name = value</c> lines for inspection.</summary>
    public string LocalsText()
    {
        IReadOnlyDictionary<string, BeaconValue> snapshot = Locals;
        if (snapshot.Count == 0)
            return "(no locals)";

        var sb = new System.Text.StringBuilder();
        foreach (string name in snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string display;
            try
            {
                display = BeaconInterpreter.ToDisplayText(snapshot[name]!);
            }
            catch
            {
                display = "(unrenderable)";
            }

            if (display.Length > 200)
                display = display[..200] + "...";

            sb.Append(name).Append(" = ").Append(display).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Evaluates one line: expression, <c>show</c>, <c>set</c>, or any single-line statement.</summary>
    public async Task<BeaconReplResult> EvalLineAsync(
        string line, int? seed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EvalLockedAsync(line, seed, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BeaconReplResult> EvalLockedAsync(string line, int? seed, CancellationToken ct)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return new BeaconReplResult(true, null, null, _interpreter.DispatchSeed, []);

        string body = IsStatementStart(trimmed) ? trimmed : "show " + trimmed;
        string wrapped = "# beacon 1\n" + body + "\n";

        BeaconLexResult lexed = BeaconLexer.Lex(ReplFileName, wrapped);
        BeaconHeaderResult header = BeaconHeader.Parse(
            ReplFileName, lexed.NormalizedSource, BeaconPipeline.FirstCodeLine(lexed), lexed.Comments);
        if (!header.Ok)
            return Fail(header.Diagnostics, seed);

        BeaconParseResult parsed = BeaconParser.Parse(ReplFileName, lexed.Tokens, header.Major);
        var errors = new List<BeaconDiagnostic>();
        errors.AddRange(lexed.Diagnostics.Where(d => d.Severity == BeaconSeverity.Error));
        errors.AddRange(parsed.Diagnostics.Where(d => d.Severity == BeaconSeverity.Error));
        if (parsed.Script is null || errors.Count > 0)
        {
            if (errors.Count == 0)
                errors.AddRange(parsed.Diagnostics);

            BeaconPipeline.SortByLocation(errors);
            return Fail(errors, seed);
        }

        // Globals deliberately survive across lines (no ResetForNewSource): this is the persistence that makes a REPL a REPL.
        // Fuel and RNG re-arm per dispatch inside.
        BeaconScript desugared = BeaconDesugar.Desugar(parsed.Script);
        BeaconRunResult result = await _interpreter
            .RunTopLevelAsync(desugared, lexed.Comments, ct, seed)
            .ConfigureAwait(false);

        int dispatchSeed = _interpreter.DispatchSeed;
        if (!result.Success)
        {
            string error = result.Error is { } diagnostic
                ? RenderError(diagnostic)
                : "The line failed with no diagnostic.";
            return new BeaconReplResult(false, null, error, dispatchSeed, result.Diagnostics);
        }

        string? output = result.LocalOutput.Count > 0
            ? string.Join("\n", result.LocalOutput)
            : null;
        return new BeaconReplResult(true, output, null, dispatchSeed, result.Diagnostics);
    }

    private BeaconReplResult Fail(IReadOnlyList<BeaconDiagnostic> diagnostics, int? seed)
    {
        _ = seed;
        BeaconDiagnostic first = diagnostics.Count > 0
            ? diagnostics[0]
            : new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                "The line does not parse.",
                new SourceSpan(ReplFileName, 1, 1, 0),
                "Try 'show 1 + 2' or 'set x to 5'.");
        return new BeaconReplResult(false, null, RenderError(first), _interpreter.DispatchSeed, diagnostics);
    }

    private static string RenderError(BeaconDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        string text = $"[{diagnostic.Code}] {diagnostic.Message}";
        if (!string.IsNullOrWhiteSpace(diagnostic.Suggestion))
            text += $"\nTry this: {diagnostic.Suggestion}";

        return text;
    }

    private static bool IsStatementStart(string trimmed)
    {
        int end = trimmed.AsSpan().IndexOfAny(' ', '\t');
        string first = end < 0 ? trimmed : trimmed[..end];
        return StatementStarters.Contains(first);
    }
}

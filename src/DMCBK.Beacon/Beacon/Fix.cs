using System.Text.RegularExpressions;

namespace DMCBK.Core.Beacon;

/// <summary>
/// One mechanical source edit: a single-line replacement at a span-anchored position.
/// Every edit carries its human reason for the diff preview.
/// </summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Length">Characters to replace.</param>
/// <param name="Replacement">Replacement text (single line).</param>
/// <param name="Reason">Why this edit is safe (shown in the preview).</param>
public sealed record BeaconFixEdit(int Line, int Column, int Length, string Replacement, string Reason);

/// <summary>
/// The result of a <c>--fix</c> preview: whether anything applies, the fixed source, the edits, and the unified-style diff that is always printed BEFORE anything is written.
/// </summary>
/// <param name="HasFixes">True when at least one safe edit applies.</param>
/// <param name="FixedSource">The source with every edit applied.</param>
/// <param name="Edits">The applied edits, in source order.</param>
/// <param name="Diff">The preview diff (empty when no fixes).</param>
public sealed record BeaconFixPreview(
    bool HasFixes, string FixedSource, IReadOnlyList<BeaconFixEdit> Edits, string Diff);

/// <summary>Which safe mechanical normalization a forgiven form admits.</summary>
internal enum BeaconForgivenKind
{
    /// <summary>Bare <c>end</c> awaiting its label.</summary>
    BareEnd,

    /// <summary><c>cancel event</c> awaiting <c>stop event</c>.</summary>
    CancelEvent,
}

/// <summary>One forgiven spelling: its kind, source span, and (for bare ends) the label.</summary>
/// <param name="Kind">Which normalization applies.</param>
/// <param name="Span">The source span (origin chain intact).</param>
/// <param name="Label">The suggested end label (bare ends only).</param>
internal sealed record BeaconForgivenForm(BeaconForgivenKind Kind, SourceSpan Span, string Label);

/// <summary>
/// The safe mechanical fix subset: completing bare <c>end</c> labels, rewriting the forgiven <c>cancel event</c> to the canonical <c>stop event</c>, normalizing indentation to two spaces per nesting level, and normalizing pasted smart quotes to ASCII.
/// All four are exactly what lexing, desugaring, or layout already normalize to, so applying them cannot change runtime meaning: the fixed source parses to the same desugared tree modulo labels.
/// <para/>
/// Anything touching meaning (<c>=</c> versus <c>is</c>, a missing <c>wait</c>, a missing <c>end</c> line whose insertion point is ambiguous) stays a suggestion and is never edited.
/// Every preview self-validates by re-linting: when the fixed source grows new errors, the preview is discarded and no fix is offered.
/// </summary>
public static class BeaconFix
{
    private static readonly Regex BareEndLine =
        new(@"^\s*end\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CancelEventSlice =
        new(@"^cancel\s+event$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Computes the fix preview for <paramref name="source"/> without writing anything.
    /// Returns an empty preview when the source fails to parse or no safe edit applies.
    /// </summary>
    public static BeaconFixPreview Preview(string fileName, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);

        BeaconScript? script = TryParseRaw(fileName, source);
        if (script is null)
            return Empty(source);

        string[] lines = SplitLines(source);
        List<BeaconFixEdit> edits = CollectEdits(script, lines);
        if (edits.Count == 0)
            return Empty(source);

        edits.Sort(static (a, b) =>
        {
            int line = a.Line.CompareTo(b.Line);
            return line != 0 ? line : a.Column.CompareTo(b.Column);
        });

        // One edit per line: overlapping candidates mean the structure surprised us, so offer nothing.
        for (int i = 1; i < edits.Count; i++)
        {
            if (edits[i].Line == edits[i - 1].Line)
                return Empty(source);
        }

        string fixedSource = ApplyEdits(lines, edits);
        if (!RelintAccepts(fileName, source, fixedSource, edits))
            return Empty(source);

        return new BeaconFixPreview(true, fixedSource, edits, RenderDiff(fileName, lines, edits));
    }

    private static BeaconFixPreview Empty(string source)
        => new(false, source, [], string.Empty);

    private static List<BeaconFixEdit> CollectEdits(BeaconScript script, string[] lines)
    {
        var edits = new List<BeaconFixEdit>();
        CollectCancelEventEdits(script, lines, edits);
        CollectBareEndEdits(script, lines, edits);
        CollectQuoteEdits(lines, edits);
        CollectIndentEdits(script, lines, edits);
        edits.Sort(static (a, b) =>
        {
            int line = a.Line.CompareTo(b.Line);
            return line != 0 ? line : a.Column.CompareTo(b.Column);
        });

        return edits;
    }

    private static void CollectQuoteEdits(string[] lines, List<BeaconFixEdit> edits)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var fixedLine = new System.Text.StringBuilder(line.Length);
            bool dirty = false;
            foreach (char c in line)
            {
                char mapped = c switch
                {
                    '\u201C' or '\u201D' or '\u201E' => '"',
                    '\u2018' or '\u2019' => '\'',
                    _ => c,
                };
                if (mapped != c)
                    dirty = true;

                fixedLine.Append(mapped);
            }

            if (dirty)
            {
                edits.Add(new BeaconFixEdit(
                    i + 1, 1, line.Length, fixedLine.ToString(),
                    "Pasted smart quotes read as ASCII quotes; the formatter writes ASCII."));
            }
        }
    }

    private static void CollectIndentEdits(BeaconScript script, string[] lines, List<BeaconFixEdit> edits)
    {
        var anchors = new Dictionary<int, int>();
        foreach (BeaconTopDecl decl in script.Decls)
            AnchorTop(decl, 0, lines, anchors);

        for (int i = 0; i < lines.Length; i++)
        {
            int line = i + 1;
            string text = lines[i];
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (!anchors.TryGetValue(line, out int depth))
                continue;

            string trimmed = text.TrimStart();
            if (trimmed.StartsWith('#'))
                continue;

            string expected = new(' ', depth * 2);
            int leading = 0;
            while (leading < text.Length && (text[leading] == ' ' || text[leading] == '\t'))
                leading++;

            string actual = text[..leading];
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                edits.Add(new BeaconFixEdit(
                    line, 1, leading, expected,
                    $"Line sits {depth} level(s) deep; the formatter indents two spaces per level."));
            }
        }
    }

    private static void AddAnchor(Dictionary<int, int> anchors, int line, int depth)
    {
        if (line >= 1 && !anchors.ContainsKey(line))
            anchors[line] = Math.Max(0, depth);
    }

    private static void AnchorTop(BeaconTopDecl decl, int depth, string[] lines, Dictionary<int, int> anchors)
    {
        switch (decl)
        {
            case OnBlock on:
                AddAnchor(anchors, on.Span.Line, depth);
                AnchorBlock(on.Body, depth + 1, lines, anchors);
                AnchorEnd(on.EndSpan, depth, anchors);
                break;
            case EveryBlock every:
                AddAnchor(anchors, every.Span.Line, depth);
                AnchorBlock(every.Body, depth + 1, lines, anchors);
                AnchorEnd(every.EndSpan, depth, anchors);
                break;
            case OnceBlock once:
                AddAnchor(anchors, once.Span.Line, depth);
                AnchorBlock(once.Body, depth + 1, lines, anchors);
                AnchorEnd(once.EndSpan, depth, anchors);
                break;
            case FunctionDef function:
                AddAnchor(anchors, function.Span.Line, depth);
                AnchorBlock(function.Body, depth + 1, lines, anchors);
                AnchorEnd(function.EndSpan, depth, anchors);
                break;
            case CommandBlock command:
                AddAnchor(anchors, command.Span.Line, depth);
                AnchorBlock(command.Body, depth + 1, lines, anchors);
                AnchorEnd(command.EndSpan, depth, anchors);
                break;
            case TopStatement top:
                AnchorStmt(top.Statement, depth, lines, anchors);
                break;
            case ExportValueDecl exported:
                AddAnchor(anchors, exported.Span.Line, depth);
                break;
            default:
                break;
        }
    }

    private static void AnchorEnd(SourceSpan? endSpan, int depth, Dictionary<int, int> anchors)
    {
        if (endSpan is not null)
            AddAnchor(anchors, endSpan.Line, depth);
    }

    private static void AnchorKeywordLine(string[] lines, int bodyStartLine, string keyword, int depth, Dictionary<int, int> anchors)
    {
        int candidate = bodyStartLine - 1;
        if (candidate >= 1 && candidate <= lines.Length
            && lines[candidate - 1].TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            AddAnchor(anchors, candidate, depth);
    }

    private static void AnchorBlock(BeaconBlock block, int depth, string[] lines, Dictionary<int, int> anchors)
    {
        foreach (BeaconStatement statement in block.Statements)
            AnchorStmt(statement, depth, lines, anchors);
    }

    private static void AnchorStmt(BeaconStatement statement, int depth, string[] lines, Dictionary<int, int> anchors)
    {
        switch (statement)
        {
            case IfStmt ifStmt:
                AddAnchor(anchors, statement.Span.Line, depth);
                foreach (BeaconIfBranch branch in ifStmt.Branches)
                    AnchorBlock(branch.Body, depth + 1, lines, anchors);

                if (ifStmt.ElseBody is not null)
                {
                    AnchorKeywordLine(lines, ifStmt.ElseBody.Span.Line, "else", depth, anchors);
                    AnchorBlock(ifStmt.ElseBody, depth + 1, lines, anchors);
                }

                AnchorEnd(ifStmt.EndSpan, depth, anchors);
                break;
            case WhileStmt whileStmt:
                AddAnchor(anchors, statement.Span.Line, depth);
                AnchorBlock(whileStmt.Body, depth + 1, lines, anchors);
                AnchorEnd(whileStmt.EndSpan, depth, anchors);
                break;
            case RepeatStmt repeat:
                AddAnchor(anchors, statement.Span.Line, depth);
                AnchorBlock(repeat.Body, depth + 1, lines, anchors);
                AnchorEnd(repeat.EndSpan, depth, anchors);
                break;
            case ForStmt forStmt:
                AddAnchor(anchors, statement.Span.Line, depth);
                AnchorBlock(forStmt.Body, depth + 1, lines, anchors);
                AnchorEnd(forStmt.EndSpan, depth, anchors);
                break;
            case TryStmt tryStmt:
                AddAnchor(anchors, statement.Span.Line, depth);
                AnchorBlock(tryStmt.Body, depth + 1, lines, anchors);
                AnchorKeywordLine(lines, tryStmt.CatchBody.Span.Line, "catch", depth, anchors);
                AnchorBlock(tryStmt.CatchBody, depth + 1, lines, anchors);
                if (tryStmt.FinallyBody is not null)
                {
                    AnchorKeywordLine(lines, tryStmt.FinallyBody.Span.Line, "finally", depth, anchors);
                    AnchorBlock(tryStmt.FinallyBody, depth + 1, lines, anchors);
                }

                AnchorEnd(tryStmt.EndSpan, depth, anchors);
                break;
            case LockStmt lockStmt:
                AddAnchor(anchors, statement.Span.Line, depth);
                AnchorBlock(lockStmt.Body, depth + 1, lines, anchors);
                AnchorEnd(lockStmt.EndSpan, depth, anchors);
                break;
            case NestedDeclStmt nested:
                AnchorTop(nested.Decl, depth, lines, anchors);
                break;
            default:
                AddAnchor(anchors, statement.Span.Line, depth);
                break;
        }
    }

    private static string[] SplitLines(string source)
        => source.Split(["\r\n", "\n"], StringSplitOptions.None);

    private static BeaconScript? TryParseRaw(string fileName, string source)
    {
        try
        {
            BeaconHeaderResult header = BeaconPipeline.LexAndParseHeader(
                fileName, source, out BeaconLexResult lexed, out _);
            if (!header.Ok)
                return null;

            BeaconParseResult parsed = BeaconParser.Parse(fileName, lexed.Tokens, header.Major);
            return parsed.Script;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void CollectCancelEventEdits(
        BeaconScript script, string[] lines, List<BeaconFixEdit> edits)
    {
        foreach (StopStmt stop in WalkStatements(script).OfType<StopStmt>())
        {
            if (!stop.IsCancelAlias)
                continue;

            SourceSpan origin = stop.Span.Origin;
            if (!SliceMatches(lines, origin, CancelEventSlice))
                continue;

            edits.Add(new BeaconFixEdit(
                origin.Line, origin.Column, origin.Length, "stop event",
                "'cancel event' is forgiven for 'stop event'; desugar already runs the latter."));
        }
    }

    private static void CollectBareEndEdits(
        BeaconScript script, string[] lines, List<BeaconFixEdit> edits)
    {
        // Bare `end` lines pair innermost-first: the parser matched each `end` token to the innermost block whose body loop saw it, so ascending ends meet their owners in order.
        List<int> bareEnds = [];
        for (int i = 0; i < lines.Length; i++)
        {
            if (BareEndLine.IsMatch(lines[i]))
                bareEnds.Add(i + 1);
        }

        if (bareEnds.Count == 0)
            return;

        List<(int Line, int Column, string Label)> missing = [];
        CollectMissingEnds(script, missing);
        missing.Sort(static (a, b) =>
        {
            int line = a.Line.CompareTo(b.Line);
            return line != 0 ? line : a.Column.CompareTo(b.Column);
        });

        var used = new HashSet<int>();
        foreach (int endLine in bareEnds)
        {
            int best = -1;
            for (int i = 0; i < missing.Count; i++)
            {
                if (used.Contains(i))
                    continue;

                if (missing[i].Line < endLine || (missing[i].Line == endLine && missing[i].Column <= lines[endLine - 1].Length + 1))
                    best = i;
            }

            if (best >= 0)
            {
                used.Add(best);
                string label = missing[best].Label;
                string line = lines[endLine - 1];
                int insertAt = line.Length;
                string comment = string.Empty;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    insertAt = hash;
                    while (insertAt > 0 && char.IsWhiteSpace(line[insertAt - 1]))
                        insertAt--;

                    comment = line[hash..];
                }

                string replacement = line[..insertAt] + " " + label + (comment.Length == 0 ? string.Empty : " " + comment);
                edits.Add(new BeaconFixEdit(
                    endLine, 1, line.Length, replacement,
                    $"Bare 'end' closes a '{label}' block; the formatter writes 'end {label}'."));
            }
        }
    }

    /// <summary>
    /// Finds every forgiven spelling in a raw (pre-desugar) script: bare <c>end</c> blocks with their suggested labels, and <c>cancel event</c> statements.
    /// Shared by the fix pairing and the lint B1008 notes so both agree on what is normalizable.
    /// </summary>
    internal static IReadOnlyList<BeaconForgivenForm> FindForgivenForms(BeaconScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var forms = new List<BeaconForgivenForm>();
        foreach (BeaconTopDecl decl in script.Decls)
            WalkForgivenTop(decl, forms);

        return forms;
    }

    private static void WalkForgivenTop(BeaconTopDecl decl, List<BeaconForgivenForm> forms)
    {
        switch (decl)
        {
            case OnBlock on:
                NoteMissingEnd(on.Span, on.EndLabelMissing, on.SuggestedEndLabel, forms);
                WalkForgivenBlock(on.Body, forms);
                break;
            case EveryBlock every:
                NoteMissingEnd(every.Span, every.EndLabelMissing, every.SuggestedEndLabel, forms);
                WalkForgivenBlock(every.Body, forms);
                break;
            case OnceBlock once:
                NoteMissingEnd(once.Span, once.EndLabelMissing, once.SuggestedEndLabel, forms);
                WalkForgivenBlock(once.Body, forms);
                break;
            case FunctionDef function:
                NoteMissingEnd(function.Span, function.EndLabelMissing, function.SuggestedEndLabel, forms);
                WalkForgivenBlock(function.Body, forms);
                break;
            case CommandBlock command:
                NoteMissingEnd(command.Span, command.EndLabelMissing, command.SuggestedEndLabel, forms);
                WalkForgivenBlock(command.Body, forms);
                break;
            case TopStatement top:
                WalkForgivenStmt(top.Statement, forms);
                break;
            default:
                break;
        }
    }

    private static void WalkForgivenBlock(BeaconBlock block, List<BeaconForgivenForm> forms)
    {
        foreach (BeaconStatement statement in block.Statements)
            WalkForgivenStmt(statement, forms);
    }

    private static void WalkForgivenStmt(BeaconStatement statement, List<BeaconForgivenForm> forms)
    {
        if (statement is StopStmt stop && stop.IsCancelAlias)
            forms.Add(new BeaconForgivenForm(BeaconForgivenKind.CancelEvent, stop.Span, string.Empty));

        switch (statement)
        {
            case IfStmt ifStmt:
                NoteMissingEnd(ifStmt.Span, ifStmt.EndLabelMissing, ifStmt.SuggestedEndLabel, forms);
                foreach (BeaconIfBranch branch in ifStmt.Branches)
                    WalkForgivenBlock(branch.Body, forms);

                if (ifStmt.ElseBody is not null)
                    WalkForgivenBlock(ifStmt.ElseBody, forms);

                break;
            case WhileStmt whileStmt:
                NoteMissingEnd(whileStmt.Span, whileStmt.EndLabelMissing, whileStmt.SuggestedEndLabel, forms);
                WalkForgivenBlock(whileStmt.Body, forms);
                break;
            case RepeatStmt repeat:
                NoteMissingEnd(repeat.Span, repeat.EndLabelMissing, repeat.SuggestedEndLabel, forms);
                WalkForgivenBlock(repeat.Body, forms);
                break;
            case ForStmt forStmt:
                NoteMissingEnd(forStmt.Span, forStmt.EndLabelMissing, forStmt.SuggestedEndLabel, forms);
                WalkForgivenBlock(forStmt.Body, forms);
                break;
            case TryStmt tryStmt:
                NoteMissingEnd(tryStmt.Span, tryStmt.EndLabelMissing, tryStmt.SuggestedEndLabel, forms);
                WalkForgivenBlock(tryStmt.Body, forms);
                WalkForgivenBlock(tryStmt.CatchBody, forms);
                if (tryStmt.FinallyBody is not null)
                    WalkForgivenBlock(tryStmt.FinallyBody, forms);

                break;
            case LockStmt lockStmt:
                NoteMissingEnd(lockStmt.Span, lockStmt.EndLabelMissing, lockStmt.SuggestedEndLabel, forms);
                WalkForgivenBlock(lockStmt.Body, forms);
                break;
            case NestedDeclStmt nested:
                WalkForgivenTop(nested.Decl, forms);
                break;
            default:
                break;
        }
    }

    private static void NoteMissingEnd(
        SourceSpan span, bool missing, string label, List<BeaconForgivenForm> forms)
    {
        if (!missing || string.IsNullOrWhiteSpace(label))
            return;

        forms.Add(new BeaconForgivenForm(BeaconForgivenKind.BareEnd, span, label.Trim()));
    }

    private static void CollectMissingEnds(BeaconScript script, List<(int Line, int Column, string Label)> missing)
    {
        foreach (BeaconForgivenForm form in FindForgivenForms(script))
        {
            if (form.Kind != BeaconForgivenKind.BareEnd)
                continue;

            SourceSpan origin = form.Span.Origin;
            missing.Add((origin.Line, origin.Column, form.Label));
        }
    }

    private static bool SliceMatches(string[] lines, SourceSpan origin, Regex pattern)
    {
        if (origin.Line < 1 || origin.Line > lines.Length || origin.Column < 1)
            return false;

        string line = lines[origin.Line - 1];
        if (origin.Column - 1 + origin.Length > line.Length)
            return false;

        return pattern.IsMatch(line.Substring(origin.Column - 1, origin.Length));
    }

    private static string ApplyEdits(string[] lines, List<BeaconFixEdit> edits)
    {
        string[] copy = (string[])lines.Clone();
        foreach (BeaconFixEdit edit in edits.OrderByDescending(e => e.Line))
        {
            string line = copy[edit.Line - 1];
            copy[edit.Line - 1] = line[..(edit.Column - 1)] + edit.Replacement
                + line[(edit.Column - 1 + edit.Length)..];
        }

        return string.Join("\n", copy);
    }

    private static IEnumerable<BeaconStatement> WalkStatements(BeaconScript script)
    {
        foreach (BeaconTopDecl decl in script.Decls)
        {
            foreach (BeaconStatement statement in WalkTop(decl))
                yield return statement;
        }
    }

    private static IEnumerable<BeaconStatement> WalkTop(BeaconTopDecl decl)
    {
        switch (decl)
        {
            case OnBlock on:
                foreach (BeaconStatement statement in WalkBlock(on.Body))
                    yield return statement;

                break;
            case EveryBlock every:
                foreach (BeaconStatement statement in WalkBlock(every.Body))
                    yield return statement;

                break;
            case OnceBlock once:
                foreach (BeaconStatement statement in WalkBlock(once.Body))
                    yield return statement;

                break;
            case FunctionDef function:
                foreach (BeaconStatement statement in WalkBlock(function.Body))
                    yield return statement;

                break;
            case CommandBlock command:
                foreach (BeaconStatement statement in WalkBlock(command.Body))
                    yield return statement;

                break;
            case TopStatement top:
                foreach (BeaconStatement statement in WalkStmt(top.Statement))
                    yield return statement;

                break;
            default:
                break;
        }
    }

    private static IEnumerable<BeaconStatement> WalkBlock(BeaconBlock block)
    {
        foreach (BeaconStatement statement in block.Statements)
        {
            foreach (BeaconStatement nested in WalkStmt(statement))
                yield return nested;
        }
    }

    private static IEnumerable<BeaconStatement> WalkStmt(BeaconStatement statement)
    {
        yield return statement;
        switch (statement)
        {
            case IfStmt ifStmt:
                foreach (BeaconIfBranch branch in ifStmt.Branches)
                {
                    foreach (BeaconStatement nested in WalkBlock(branch.Body))
                        yield return nested;
                }

                if (ifStmt.ElseBody is not null)
                {
                    foreach (BeaconStatement nested in WalkBlock(ifStmt.ElseBody))
                        yield return nested;
                }

                break;
            case WhileStmt whileStmt:
                foreach (BeaconStatement nested in WalkBlock(whileStmt.Body))
                    yield return nested;

                break;
            case RepeatStmt repeat:
                foreach (BeaconStatement nested in WalkBlock(repeat.Body))
                    yield return nested;

                break;
            case ForStmt forStmt:
                foreach (BeaconStatement nested in WalkBlock(forStmt.Body))
                    yield return nested;

                break;
            case TryStmt tryStmt:
                foreach (BeaconStatement nested in WalkBlock(tryStmt.Body))
                    yield return nested;

                foreach (BeaconStatement nested in WalkBlock(tryStmt.CatchBody))
                    yield return nested;

                if (tryStmt.FinallyBody is not null)
                {
                    foreach (BeaconStatement nested in WalkBlock(tryStmt.FinallyBody))
                        yield return nested;
                }

                break;
            case LockStmt lockStmt:
                foreach (BeaconStatement nested in WalkBlock(lockStmt.Body))
                    yield return nested;

                break;
            case NestedDeclStmt nested:
                foreach (BeaconStatement nestedStmt in WalkTop(nested.Decl))
                    yield return nestedStmt;

                break;
            default:
                break;
        }
    }

    private static bool RelintAccepts(string fileName, string before, string after, List<BeaconFixEdit> applied)
    {
        IReadOnlyList<BeaconDiagnostic> lintBefore;
        IReadOnlyList<BeaconDiagnostic> lintAfter;
        int candidatesBefore;
        int candidatesAfter;
        try
        {
            var engine = new BeaconEngine(BeaconOfflineHost.Shared);
            string id = "fix-guard-" + Guid.NewGuid().ToString("N");
            engine.LoadSource(id, fileName, before);
            lintBefore = engine.Lint(id);
            candidatesBefore = CandidateCount(fileName, before);
            engine.LoadSource(id, fileName, after);
            lintAfter = engine.Lint(id);
            candidatesAfter = CandidateCount(fileName, after);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        // Every applied edit resolves exactly one candidate and introduces none.
        if (candidatesAfter != candidatesBefore - applied.Count)
            return false;

        // No new errors: every remaining error (by code and origin) already existed before.
        IReadOnlyList<BeaconDiagnostic> beforeErrors =
            lintBefore.Where(d => d.Severity == BeaconSeverity.Error).ToList();
        IReadOnlyList<BeaconDiagnostic> afterErrors =
            lintAfter.Where(d => d.Severity == BeaconSeverity.Error).ToList();
        var beforeKeys = new HashSet<string>(
            beforeErrors.Select(d => d.Code + "@" + d.Span.Origin.Line + ":" + d.Span.Origin.Column),
            StringComparer.Ordinal);
        return afterErrors.All(d => beforeKeys.Contains(
            d.Code + "@" + d.Span.Origin.Line + ":" + d.Span.Origin.Column));
    }

    private static int CandidateCount(string fileName, string source)
    {
        BeaconScript? script = TryParseRaw(fileName, source);
        return script is null ? 0 : CollectEdits(script, SplitLines(source)).Count;
    }

    private static string RenderDiff(string fileName, string[] lines, List<BeaconFixEdit> edits)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- ").Append(fileName).Append('\n');
        sb.Append("+++ ").Append(fileName).Append(" (fixed)\n");
        string[] fixedLines = ApplyEdits(lines, edits).Split('\n');
        foreach (BeaconFixEdit edit in edits)
        {
            sb.Append("@@ ").Append(fileName).Append(':').Append(edit.Line).Append(" @@ ")
                .Append(edit.Reason).Append('\n');
            sb.Append("- ").Append(lines[edit.Line - 1]).Append('\n');
            sb.Append("+ ").Append(fixedLines[edit.Line - 1]).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}

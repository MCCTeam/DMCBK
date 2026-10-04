namespace DMCBK.Core.Beacon;

/// <summary>One parser run: an optional script plus never-throwing diagnostics.</summary>
public sealed record BeaconParseResult(
    BeaconScript? Script,
    IReadOnlyList<BeaconDiagnostic> Diagnostics);

/// <summary>
/// Pratt parser: precedence ladder, headtail, labeled closers, prologue placement.
/// Total: malformed input yields <c>B0001</c> diagnostics, never an exception.
/// Multiword <c>else if</c> arrives as one token and is split up front; <c>end label</c> stays two tokens with a same-line rule so a bare <c>end</c> followed by a next-block <c>on</c> is not eaten as a label.
/// Unit spellings are accepted in identifier slots (a variable named <c>s</c> lexes as Unit).
/// </summary>
public static class BeaconParser
{
    private static readonly string[] StatementKeywords =
    [
        "set", "say", "whisper", "server", "disconnect", "show", "log", "wait", "stop", "stop event",
        "skip", "break", "continue", "return", "save", "lock", "lock shared", "start",
        "await", "cancel event", "cancel task", "if", "while", "repeat", "for each",
        "try", "cancel",
    ];

    private static readonly string[] KeywordVocabulary =
    [
        "set", "to", "on", "every", "when", "as", "end", "if", "else", "while", "repeat",
        "times", "for", "each", "in", "try", "catch", "function", "return", "export",
        "command", "import", "extern", "from", "say", "whisper", "server", "show", "wait",
        "stop", "skip", "break", "continue", "save", "lock", "shared", "start", "await",
        "cancel", "call", "task", "event", "not", "and", "or", "is", "contains", "matches",
        "starts", "ends", "with", "empty", "set", "yes", "no", "none", "then", "cooldown",
        "named", "wants", "times", "to", "chat", "join", "leave", "health", "hunger",
        "inventory", "login", "logout", "disconnect", "reconnect", "tps", "mcc", "log",
    ];

    /// <summary>Parses tokens into a script; <paramref name="major"/> comes from the header gate.</summary>
    public static BeaconParseResult Parse(string fileName, IReadOnlyList<BeaconToken> tokens, int major = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(tokens);
        try
        {
            var inner = new Parser(fileName, SplitElseIf(fileName, tokens), major);
            return inner.Run();
        }
        catch (Exception ex)
        {
            BeaconToken first = tokens.Count > 0 ? tokens[0] : new BeaconToken(
                BeaconTokenKind.EndOfFile, string.Empty, new SourceSpan(fileName, 1, 1, 0));
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"I expected a script, but the parser failed internally near {first.Span}: {ex.Message}",
                first.Span,
                "Retry with a minimal script to isolate the input.");
            return new BeaconParseResult(null, [diagnostic]);
        }
    }

    private static IReadOnlyList<BeaconToken> SplitElseIf(string fileName, IReadOnlyList<BeaconToken> tokens)
    {
        var output = new List<BeaconToken>(tokens.Count);
        foreach (BeaconToken token in tokens)
        {
            if (token.Kind == BeaconTokenKind.Keyword
                && string.Equals(token.Normalized, "else if", StringComparison.Ordinal))
            {
                string[] words = token.Text.Split(' ');
                string firstWord = words.Length > 0 ? words[0] : "else";
                string secondWord = words.Length > 1 ? words[^1] : "if";
                var first = new BeaconToken(
                    BeaconTokenKind.Keyword, firstWord,
                    new SourceSpan(fileName, token.Span.Line, token.Span.Column, firstWord.Length))
                {
                    Normalized = "else",
                };
                var second = new BeaconToken(
                    BeaconTokenKind.Keyword, secondWord,
                    new SourceSpan(fileName, token.Span.Line, token.Span.Column + firstWord.Length + 1, secondWord.Length))
                {
                    Normalized = "if",
                };
                output.Add(first);
                output.Add(second);
            }
            else
                output.Add(token);
        }

        return output;
    }

    private sealed class Parser
    {
        private readonly string _file;
        private readonly IReadOnlyList<BeaconToken> _tokens;
        private readonly int _major;
        private readonly List<BeaconDiagnostic> _diagnostics = [];
        private int _pos;

        internal Parser(string file, IReadOnlyList<BeaconToken> tokens, int major)
        {
            _file = file;
            _tokens = tokens;
            _major = major;
        }

        internal BeaconParseResult Run()
        {
            var imports = new List<BeaconImport>();
            var externs = new List<BeaconExtern>();
            var decls = new List<BeaconTopDecl>();
            bool seenTopDecl = false;

            while (!AtEnd)
            {
                if (CheckKeyword("end") || CheckKeyword("else") || CheckKeyword("catch"))
                {
                    BeaconToken stray = Advance();
                    BeaconToken after = Peek();
                    if (after.Span.Line == stray.Span.Line
                        && (after.Kind == BeaconTokenKind.Identifier
                            || (after.Kind == BeaconTokenKind.Keyword && IsEndLabelWord(after.Normalized))))
                        Advance();

                    Error(stray.Span, "a top-level on/every/function/command block or statement",
                        $"'{stray.Text}' without an opener",
                        "Remove this stray closer or open a block first.");
                    continue;
                }

                if (CheckKeyword("import"))
                {
                    BeaconImport? import = ParseImport();
                    if (import is not null)
                    {
                        if (seenTopDecl)
                        {
                            Error(import.Span, "a prologue import before any declaration",
                                "an import after code",
                                "Move this import above the first on/every/function/command block.");
                        }

                        imports.Add(import);
                    }

                    continue;
                }

                if (CheckKeyword("extern"))
                {
                    BeaconExtern? externDecl = ParseExtern();
                    if (externDecl is not null)
                    {
                        if (seenTopDecl)
                        {
                            Error(externDecl.Span, "a prologue extern before any declaration",
                                "an extern after code",
                                "Move this extern above the first on/every/function/command block.");
                        }

                        externs.Add(externDecl);
                    }

                    continue;
                }

                BeaconTopDecl? decl = ParseTopDecl();
                if (decl is not null)
                {
                    seenTopDecl = true;
                    decls.Add(decl);
                }
            }

            SourceSpan span = decls.Count > 0
                ? SpanFrom(decls[0].Span, decls[^1].Span)
                : new SourceSpan(_file, 1, 1, 0);
            var script = new BeaconScript(span, _major, imports, externs, decls);
            return new BeaconParseResult(script, _diagnostics);
        }

        private BeaconToken Peek(int ahead = 0)
        {
            int index = _pos + ahead;
            if (index < 0)
                return _tokens[0];

            return index < _tokens.Count ? _tokens[index] : _tokens[^1];
        }

        private bool AtEnd => Peek().Kind == BeaconTokenKind.EndOfFile;

        private BeaconToken Advance()
        {
            BeaconToken current = Peek();
            if (!AtEnd)
                _pos++;

            return current;
        }

        private bool CheckKeyword(string normalized, int ahead = 0)
        {
            BeaconToken token = Peek(ahead);
            return token.Kind == BeaconTokenKind.Keyword
                && string.Equals(token.Normalized, normalized, StringComparison.Ordinal);
        }

        private bool CheckSymbol(string text, int ahead = 0)
        {
            BeaconToken token = Peek(ahead);
            return token.Kind == BeaconTokenKind.Symbol
                && string.Equals(token.Text, text, StringComparison.Ordinal);
        }

        private bool CheckKind(BeaconTokenKind kind, int ahead = 0) => Peek(ahead).Kind == kind;

        private SourceSpan SpanFrom(SourceSpan first, SourceSpan last)
        {
            if (!string.Equals(first.File, last.File, StringComparison.Ordinal) || first.Line != last.Line)
                return first;

            return new SourceSpan(first.File, first.Line, first.Column, Math.Max(0, last.EndColumn - first.Column));
        }

        private SourceSpan SpanFrom(BeaconToken first, BeaconToken last) => SpanFrom(first.Span, last.Span);

        private void Error(SourceSpan span, string expected, string found, string suggestion)
        {
            _diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"I expected {expected}, but found {found}.",
                span,
                suggestion));
        }

        private void ErrorToken(BeaconToken token, string expected, string suggestion)
        {
            string found = token.Kind == BeaconTokenKind.EndOfFile
                ? "the end of the file"
                : $"'{token.Text}'";
            Error(token.Span, expected, found, suggestion);
        }

        private void ErrorCode(SourceSpan span, string code, string message, string suggestion)
        {
            _diagnostics.Add(new BeaconDiagnostic(code, BeaconSeverity.Error, message, span, suggestion));
        }

        private static string? DidYouMean(string word, IEnumerable<string> candidates)
            => BeaconTextDistance.SuggestNearest(word, candidates);

        private (string Name, SourceSpan Span)? AcceptIdent()
        {
            BeaconToken token = Peek();
            if (token.Kind == BeaconTokenKind.Identifier || token.Kind == BeaconTokenKind.Unit)
            {
                Advance();
                return (token.Text, token.Span);
            }

            return null;
        }

        private (string Name, SourceSpan Span)? AcceptBuiltinBase()
        {
            BeaconToken token = Peek();
            if (token.Kind == BeaconTokenKind.Keyword
                && token.Normalized is "event" or "server" or "shared")
            {
                Advance();
                return (token.Normalized, token.Span);
            }

            return null;
        }

        private (string Name, SourceSpan Span)? AcceptEventName()
        {
            BeaconToken token = Peek();
            if (token.Kind is BeaconTokenKind.Identifier or BeaconTokenKind.Unit or BeaconTokenKind.Keyword)
            {
                Advance();
                return (token.Text, token.Span);
            }

            return null;
        }

        /// <summary>
        /// A member name after <c>.</c>: identifiers plus reserved words, so verbs like <c>dialog.show</c> and <c>dialog.set</c> parse.
        /// Keywords normalize (the language is case-insensitive for them); identifiers keep their case.
        /// </summary>
        private (string Name, SourceSpan Span)? AcceptMemberName()
        {
            BeaconToken token = Peek();
            if (token.Kind is BeaconTokenKind.Identifier or BeaconTokenKind.Unit)
            {
                Advance();
                return (token.Text, token.Span);
            }

            if (token.Kind == BeaconTokenKind.Keyword)
            {
                Advance();
                return (token.Normalized, token.Span);
            }

            return null;
        }

        private bool CanStartExpr(BeaconToken token)
        {
            if (token.Kind is BeaconTokenKind.Number or BeaconTokenKind.Text
                or BeaconTokenKind.TripleText or BeaconTokenKind.Regex
                or BeaconTokenKind.Identifier or BeaconTokenKind.Unit)
                return true;

            if (token.Kind == BeaconTokenKind.Symbol && token.Text is "(" or "[" or "{" or "-" or "!")
                return true;

            return token.Kind == BeaconTokenKind.Keyword
                && token.Normalized is "not" or "yes" or "no" or "none" or "call"
                    or "event" or "server" or "shared";
        }

        private string DescribeToken(BeaconToken token) => token.Kind switch
        {
            BeaconTokenKind.EndOfFile => "the end of the file",
            BeaconTokenKind.Number => $"the number {token.Text}",
            BeaconTokenKind.Text or BeaconTokenKind.TripleText => "some text",
            BeaconTokenKind.Regex => "a pattern",
            BeaconTokenKind.Unit => $"the unit '{token.Text}'",
            BeaconTokenKind.Identifier => $"the name '{token.Text}'",
            BeaconTokenKind.Keyword => $"the keyword '{token.Text}'",
            _ => $"'{token.Text}'",
        };

        private string GetStringValue(BeaconToken token)
        {
            var builder = new System.Text.StringBuilder();
            bool sawHole = false;
            foreach (BeaconTextPart part in token.Parts)
            {
                switch (part)
                {
                    case BeaconTextLiteral literal:
                        builder.Append(literal.Value);
                        break;
                    case BeaconTextHole:
                        sawHole = true;
                        break;
                }
            }

            if (sawHole)
            {
                Error(token.Span, "plain text without interpolation here",
                    "interpolated text",
                    $"Remove the {{...}} holes from {token.Text}.");
            }

            return builder.ToString();
        }

        private BeaconImport? ParseImport()
        {
            BeaconToken importToken = Advance();
            if (!CheckKind(BeaconTokenKind.Text) && !CheckKind(BeaconTokenKind.TripleText))
            {
                BeaconToken found = Peek();
                Error(found.Span, "a quoted path after 'import'",
                    DescribeToken(found),
                    "Write import \"lib/econ.bcn\" as econ.");
                SynchronizeToStatement();
                return null;
            }

            BeaconToken pathToken = Advance();
            string path = GetStringValue(pathToken);
            if (!CheckKeyword("as"))
            {
                BeaconToken found = Peek();
                string? hint = found.Kind == BeaconTokenKind.Identifier
                    ? DidYouMean(found.Text, ["as"])
                    : null;
                Error(found.Span, "'as' plus an alias after the import path",
                    DescribeToken(found),
                    hint is null ? "Write import \"lib/econ.bcn\" as econ." : $"Did you mean 'as'? Write import \"{path}\" as econ.");
                SynchronizeToStatement();
                return null;
            }

            Advance();
            (string Name, SourceSpan Span)? alias = AcceptIdent();
            if (alias is null)
            {
                BeaconToken found = Peek();
                ErrorToken(found, "an alias after 'as'", "Write import \"lib/econ.bcn\" as econ.");
                SynchronizeToStatement();
                return null;
            }

            return new BeaconImport(
                SpanFrom(importToken, Peek(-1)), path, pathToken.Span, alias.Value.Name, alias.Value.Span);
        }

        private BeaconExtern? ParseExtern()
        {
            BeaconToken externToken = Advance();
            (string Name, SourceSpan Span)? name = AcceptIdent();
            if (name is null)
            {
                BeaconToken found = Peek();
                ErrorToken(found, "a name after 'extern'", "Write extern price_of from \"shop\".");
                SynchronizeToStatement();
                return null;
            }

            if (!CheckKeyword("from"))
            {
                BeaconToken found = Peek();
                Error(found.Span, "'from' plus a plugin id after the extern name",
                    DescribeToken(found),
                    $"Write extern {name.Value.Name} from \"shop\".");
                SynchronizeToStatement();
                return null;
            }

            Advance();
            if (!CheckKind(BeaconTokenKind.Text) && !CheckKind(BeaconTokenKind.TripleText))
            {
                BeaconToken found = Peek();
                Error(found.Span, "a quoted plugin id after 'from'",
                    DescribeToken(found),
                    $"Write extern {name.Value.Name} from \"shop\".");
                SynchronizeToStatement();
                return null;
            }

            BeaconToken pluginToken = Advance();
            return new BeaconExtern(
                SpanFrom(externToken, pluginToken),
                name.Value.Name, name.Value.Span,
                GetStringValue(pluginToken), pluginToken.Span);
        }

        private BeaconTopDecl? ParseTopDecl()
        {
            if (CheckKeyword("on"))
                return ParseOnBlock();

            if (CheckKeyword("every"))
                return ParseEveryBlock();

            if (CheckKeyword("in"))
                return ParseOnceBlock();

            if (CheckKeyword("export") && CheckKeyword("set", ahead: 1))
                return ParseExportValue();

            if (CheckKeyword("export") || CheckKeyword("function"))
                return ParseFunctionDef();

            if (CheckKeyword("command"))
                return ParseCommandBlock();

            if (CheckKeyword("import"))
            {
                BeaconImport? lateImport = ParseImport();
                if (lateImport is null)
                    return null;

                Error(lateImport.Span, "a prologue import before any declaration",
                    "an import after code",
                    "Move this import above the first on/every/function/command block.");
                return new TopStatement(lateImport.Span, new ExprStmt(lateImport.Span, new ErrorExpr(lateImport.Span)));
            }

            if (CheckKeyword("extern"))
            {
                BeaconExtern? lateExtern = ParseExtern();
                if (lateExtern is null)
                    return null;

                Error(lateExtern.Span, "a prologue extern before any declaration",
                    "an extern after code",
                    "Move this extern above the first on/every/function/command block.");
                return new TopStatement(lateExtern.Span, new ExprStmt(lateExtern.Span, new ErrorExpr(lateExtern.Span)));
            }

            BeaconStatement? statement = ParseStatement();
            return statement is null ? null : new TopStatement(statement.Span, statement);
        }

        private OnBlock? ParseOnBlock()
        {
            BeaconToken onToken = Advance();
            (string Name, SourceSpan Span)? eventName = AcceptEventName();
            if (eventName is null)
            {
                BeaconToken found = Peek();
                Error(found.Span, "an event name after 'on'",
                    DescribeToken(found),
                    "Write on chat, on join, or on tps; see the hook list for all twenty-four names.");
                SynchronizeToTopDecl();
                return null;
            }

            string? alias = null;
            SourceSpan? aliasSpan = null;
            if (CheckKeyword("as"))
            {
                Advance();
                (string Name, SourceSpan Span)? aliasIdent = AcceptIdent();
                if (aliasIdent is null)
                    ErrorToken(Peek(), "a name after 'as'", $"Write on {eventName.Value.Name} as e.");
                else
                {
                    alias = aliasIdent.Value.Name;
                    aliasSpan = aliasIdent.Value.Span;
                }
            }

            BeaconCooldown? cooldown = null;
            if (CheckKeyword("cooldown"))
                cooldown = ParseCooldown();

            BeaconExpr? when = null;
            if (CheckKeyword("when"))
            {
                Advance();
                if (!CanStartExpr(Peek()) || CheckKeyword("then") || CheckSymbol(":")
                    || CheckKeyword("end") || AtEnd)
                {
                    Error(Peek().Span, "a filter after 'when'",
                        DescribeToken(Peek()),
                        $"Write on {eventName.Value.Name} when message contains \"!help\".");
                }
                else
                    when = ParseExpr();
            }

            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("on");
            return new OnBlock(
                SpanFrom(onToken.Span, end.EndSpan ?? body.Span),
                eventName.Value.Name, eventName.Value.Span,
                alias, aliasSpan, cooldown, when, body,
                end.Label, end.EndSpan, end.Missing, "on");
        }

        private BeaconCooldown? ParseCooldown()
        {
            BeaconToken cooldownToken = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a count after 'cooldown'",
                    DescribeToken(Peek()),
                    "Write cooldown 300 seconds named \"label\".");
                SynchronizeToHeadTail();
                return null;
            }

            BeaconExpr count = ParseExpr();
            BeaconToken unitToken = Peek();
            if (!TryTakeTimeUnit(out string rawUnit, out string unit, out SourceSpan unitSpan))
            {
                Error(unitToken.Span, "a time unit after the cooldown count",
                    DescribeToken(unitToken),
                    "Use one of: second, minute, hour (plural forms also parse).");
            }

            if (!CheckKeyword("named"))
            {
                Error(Peek().Span, "'named' plus a label after the cooldown unit",
                    DescribeToken(Peek()),
                    $"Write cooldown 300 {(string.IsNullOrEmpty(rawUnit) ? "seconds" : rawUnit)} named \"label\".");
                SynchronizeToHeadTail();
                return new BeaconCooldown(
                    SpanFrom(cooldownToken.Span, unitSpan), count, unit, rawUnit, unitSpan,
                    string.Empty, unitSpan);
            }

            Advance();
            if (!CheckKind(BeaconTokenKind.Text) && !CheckKind(BeaconTokenKind.TripleText))
            {
                Error(Peek().Span, "a quoted label after 'named'",
                    DescribeToken(Peek()),
                    "Write cooldown 300 seconds named \"tps-warn\".");
                SynchronizeToHeadTail();
                return new BeaconCooldown(
                    SpanFrom(cooldownToken.Span, Peek().Span), count, unit, rawUnit, unitSpan,
                    string.Empty, unitSpan);
            }

            BeaconToken nameToken = Advance();
            return new BeaconCooldown(
                SpanFrom(cooldownToken.Span, nameToken.Span),
                count, unit, rawUnit, unitSpan,
                GetStringValue(nameToken), nameToken.Span);
        }

        private bool TryTakeTimeUnit(out string rawUnit, out string unit, out SourceSpan unitSpan)
        {
            BeaconToken unitToken = Peek();
            if (unitToken.Kind == BeaconTokenKind.Unit)
            {
                Advance();
                rawUnit = unitToken.Text;
                unit = unitToken.CanonicalUnit ?? rawUnit.ToLowerInvariant();
                unitSpan = unitToken.Span;
                return true;
            }

            if (unitToken.Kind == BeaconTokenKind.Identifier)
            {
                Advance();
                rawUnit = unitToken.Text;
                unit = rawUnit.ToLowerInvariant();
                unitSpan = unitToken.Span;
                return true;
            }

            rawUnit = string.Empty;
            unit = string.Empty;
            unitSpan = unitToken.Span;
            return false;
        }

        private EveryBlock? ParseEveryBlock()
        {
            BeaconToken everyToken = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "an interval after 'every'",
                    DescribeToken(Peek()),
                    "Write every 60 seconds.");
                SynchronizeToTopDecl();
                return null;
            }

            BeaconExpr interval = ParseExpr();
            BeaconToken unitToken = Peek();
            if (!TryTakeTimeUnit(out string rawUnit, out string unit, out SourceSpan unitSpan))
            {
                Error(unitToken.Span, "a time unit after the every count",
                    DescribeToken(unitToken),
                    "Use one of: second, minute, hour (plural forms also parse).");
                SynchronizeToTopDecl();
                return null;
            }

            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("every");
            return new EveryBlock(
                SpanFrom(everyToken.Span, end.EndSpan ?? body.Span),
                interval, unit, rawUnit, unitSpan, body,
                end.Label, end.EndSpan, end.Missing, "every");
        }

        private OnceBlock? ParseOnceBlock()
        {
            BeaconToken inToken = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a delay after 'in'",
                    DescribeToken(Peek()),
                    "Write in 30 seconds do.");
                SynchronizeToTopDecl();
                return null;
            }

            BeaconExpr delay = ParseExpr();
            BeaconToken unitToken = Peek();
            if (!TryTakeTimeUnit(out string rawUnit, out string unit, out SourceSpan unitSpan))
            {
                Error(unitToken.Span, "a time unit after the in count",
                    DescribeToken(unitToken),
                    "Use one of: second, minute, hour (plural forms also parse).");
                SynchronizeToTopDecl();
                return null;
            }

            if (!CheckKeyword("do"))
            {
                Error(Peek().Span, "'do' after the in delay",
                    DescribeToken(Peek()),
                    "Write in 30 seconds do.");
                SynchronizeToTopDecl();
                return null;
            }

            Advance();
            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("in");
            return new OnceBlock(
                SpanFrom(inToken.Span, end.EndSpan ?? body.Span),
                delay, unit, rawUnit, unitSpan, body,
                end.Label, end.EndSpan, end.Missing, "in");
        }

        private ExportValueDecl? ParseExportValue()
        {
            BeaconToken exportToken = Advance();
            BeaconToken setToken = Advance();
            (string Name, SourceSpan Span)? name = AcceptIdent();
            if (name is null)
            {
                Error(Peek().Span, "a name after 'export set'",
                    DescribeToken(Peek()),
                    "Write export set coins to {copper: 5}.");
                SynchronizeToTopDecl();
                return null;
            }

            if (!CheckKeyword("to"))
            {
                Error(Peek().Span, "'to' after the export name",
                    DescribeToken(Peek()),
                    $"Write export set {name.Value.Name} to ....");
                SynchronizeToTopDecl();
                return null;
            }

            Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a value after 'to'",
                    DescribeToken(Peek()),
                    $"Write export set {name.Value.Name} to ....");
                SynchronizeToTopDecl();
                return null;
            }

            BeaconExpr value = ParseExpr();
            _ = setToken;
            return new ExportValueDecl(
                SpanFrom(exportToken.Span, value.Span),
                name.Value.Name, name.Value.Span, value);
        }

        private FunctionDef? ParseFunctionDef()
        {
            BeaconToken first = Peek();
            bool isExport = false;
            SourceSpan? exportSpan = null;
            if (CheckKeyword("export"))
            {
                BeaconToken exportToken = Advance();
                isExport = true;
                exportSpan = exportToken.Span;
                if (!CheckKeyword("function"))
                {
                    Error(Peek().Span, "'function' after 'export'",
                        DescribeToken(Peek()),
                        "Write export function daily_report().");
                    SynchronizeToTopDecl();
                    return null;
                }
            }

            BeaconToken functionToken = Advance();
            BeaconToken startToken = isExport ? first : functionToken;
            (string Name, SourceSpan Span)? name = AcceptIdent();
            if (name is null)
            {
                Error(Peek().Span, "a function name after 'function'",
                    DescribeToken(Peek()),
                    "Write function greet(name).");
                SynchronizeToTopDecl();
                return null;
            }

            if (!CheckSymbol("("))
            {
                Error(Peek().Span, "'(' plus parameters after the function name",
                    DescribeToken(Peek()),
                    $"Write function {name.Value.Name}().");
                SynchronizeToTopDecl();
                return null;
            }

            Advance();
            var parameters = new List<BeaconParam>();
            if (!CheckSymbol(")"))
            {
                while (true)
                {
                    (string Name, SourceSpan Span)? parameter = AcceptIdent();
                    if (parameter is null)
                    {
                        Error(Peek().Span, "a parameter name",
                            DescribeToken(Peek()),
                            $"Write function {name.Value.Name}(name).");
                        break;
                    }

                    parameters.Add(new BeaconParam(parameter.Value.Name, parameter.Value.Span));
                    if (CheckSymbol(","))
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (!CheckSymbol(")"))
            {
                Error(Peek().Span, "')' after the parameter list",
                    DescribeToken(Peek()),
                    $"Write function {name.Value.Name}({string.Join(", ", parameters.Select(p => p.Name))}).");
            }
            else
                Advance();

            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("function");
            return new FunctionDef(
                SpanFrom(startToken.Span, end.EndSpan ?? body.Span),
                isExport, exportSpan, name.Value.Name, name.Value.Span,
                parameters, body, end.Label, end.EndSpan, end.Missing, "function");
        }

        private CommandBlock? ParseCommandBlock()
        {
            BeaconToken commandToken = Advance();
            if (!CheckKind(BeaconTokenKind.Text) && !CheckKind(BeaconTokenKind.TripleText))
            {
                Error(Peek().Span, "a quoted pattern after 'command'",
                    DescribeToken(Peek()),
                    "Write command \"/price <item>\".");
                SynchronizeToTopDecl();
                return null;
            }

            BeaconToken patternToken = Advance();
            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("command");
            return new CommandBlock(
                SpanFrom(commandToken.Span, end.EndSpan ?? body.Span),
                GetStringValue(patternToken), patternToken.Span, body,
                end.Label, end.EndSpan, end.Missing, "command");
        }

        private void ParseHeadTail()
        {
            if (CheckKeyword("then"))
                Advance();

            if (CheckSymbol(":"))
                Advance();
        }

        private (string? Label, SourceSpan? EndSpan, bool Missing) ParseEnd(string expected)
        {
            if (!CheckKeyword("end"))
            {
                BeaconToken found = Peek();
                if (found.Kind == BeaconTokenKind.EndOfFile)
                {
                    Error(found.Span, $"'end {expected}' to close the block",
                        "the end of the file",
                        $"Add 'end {expected}' on its own line.");
                }
                else
                {
                    Error(found.Span, $"'end {expected}' to close the block",
                        DescribeToken(found),
                        $"Add 'end {expected}' on its own line.");
                }

                return (null, null, true);
            }

            BeaconToken endToken = Advance();
            BeaconToken next = Peek();
            if (next.Span.Line == endToken.Span.Line)
            {
                if (next.Kind == BeaconTokenKind.Keyword)
                {
                    string normalized = next.Normalized;
                    if (string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase)
                        || IsEndLabelWord(normalized))
                    {
                        Advance();
                        return (next.Text, next.Span, false);
                    }
                }
                else if (next.Kind == BeaconTokenKind.Identifier)
                {
                    Advance();
                    return (next.Text, next.Span, false);
                }
            }

            return (null, null, true);
        }

        private static bool IsEndLabelWord(string normalized) => normalized is
            "on" or "every" or "function" or "command" or "lock"
            or "if" or "while" or "repeat" or "for" or "try" or "in";

        private BeaconBlock ParseBlock(bool stopOnElse, bool stopOnCatch, bool stopOnFinally = false)
        {
            var statements = new List<BeaconStatement>();
            BeaconToken start = Peek();
            while (!AtEnd)
            {
                if (CheckKeyword("end"))
                    break;

                if (stopOnElse && CheckKeyword("else"))
                    break;

                if (stopOnCatch && CheckKeyword("catch"))
                    break;

                if (stopOnFinally && CheckKeyword("finally"))
                    break;

                int before = _pos;
                BeaconStatement? statement = ParseStatement();
                if (statement is not null)
                    statements.Add(statement);
                else if (_pos == before)
                    Advance();
            }

            SourceSpan span = statements.Count > 0
                ? SpanFrom(statements[0].Span, statements[^1].Span)
                : Peek().Span;
            _ = start;
            return new BeaconBlock(span, statements);
        }

        private BeaconStatement? ParseStatement()
        {
            BeaconToken token = Peek();
            if (token.Kind == BeaconTokenKind.Keyword)
            {
                switch (token.Normalized)
                {
                    case "set":
                        return ParseSet();
                    case "say":
                        Advance();
                        return ParseSayLike((span, expr) => new SayStmt(span, expr));
                    case "whisper":
                        return ParseWhisper();
                    case "server":
                        Advance();
                        return ParseSayLike((span, expr) => new ServerStmt(span, expr));
                    case "disconnect":
                        return ParseDisconnect();
                    case "show":
                        Advance();
                        return ParseSayLike((span, expr) => new ShowStmt(span, expr));
                    case "log":
                        Advance();
                        return ParseLog();
                    case "wait":
                        return ParseWait();
                    case "stop":
                        return ParseStopBare();
                    case "stop event":
                        Advance();
                        return new StopStmt(token.Span, HasEvent: true);
                    case "cancel event":
                        Advance();
                        return new StopStmt(token.Span, HasEvent: true, IsCancelAlias: true);
                    case "cancel task":
                        return ParseCancelTask();
                    case "cancel":
                        return ParseCancelBare();
                    case "skip":
                        Advance();
                        return new SkipStmt(token.Span, BeaconSkipKind.Skip);
                    case "break":
                        Advance();
                        return new SkipStmt(token.Span, BeaconSkipKind.Break);
                    case "continue":
                        Advance();
                        return new SkipStmt(token.Span, BeaconSkipKind.Continue);
                    case "return":
                        return ParseReturn();
                    case "save":
                        return ParseSave();
                    case "lock shared":
                        return ParseLock();
                    case "lock":
                        return ParseLockBare();
                    case "start":
                        return ParseStart();
                    case "await":
                        return ParseAwait();
                    case "if":
                        return ParseIf();
                    case "while":
                        return ParseWhile();
                    case "repeat":
                        return ParseRepeat();
                    case "for each":
                        return ParseFor();
                    case "for":
                        return ParseForBare();
                    case "try":
                        return ParseTry();
                    case "on":
                    case "every":
                    case "in":
                    case "command":
                    case "function":
                    case "export":
                    case "import":
                    case "extern":
                        return ParseNestedDecl();
                    case "end":
                    case "else":
                    case "catch":
                    case "finally":
                        return null;
                    default:
                        break;
                }
            }

            if (token.Kind == BeaconTokenKind.Symbol && token.Text is "=" or "/")
            {
                ErrorToken(token,
                    token.Text == "=" ? "'set ... to ...' for assignment or 'is' for comparison"
                        : "'server' for server commands or 'mcc' for client commands",
                    token.Text == "=" ? "Write set x to 5 or test with if x is 5."
                        : "Write server \"/home\".");
                Advance();
                return null;
            }

            if (token.Kind == BeaconTokenKind.Identifier
                && string.Equals(token.Text, "log", StringComparison.Ordinal)
                && !CheckSymbol(".", 1) && !CheckSymbol("[", 1) && !CheckSymbol("(", 1)
                && CanStartExpr(Peek(1)))
            {
                Advance();
                return ParseLog();
            }

            if (!CanStartExpr(token))
            {
                if (token.Kind == BeaconTokenKind.EndOfFile)
                    return null;

                string? hint = token.Kind == BeaconTokenKind.Identifier
                    ? DidYouMean(token.Text, KeywordVocabulary)
                    : null;
                string suggestion = hint is null
                    ? "Start a statement with set, say, if, while, or a call."
                    : $"Did you mean '{hint}'? Start a statement with set, say, if, while, or a call.";
                if (token.Kind == BeaconTokenKind.Identifier && hint is not null)
                    Error(token.Span, $"'{hint}'", $"'{token.Text}'", suggestion);
                else
                    ErrorToken(token, "a statement", suggestion);

                Advance();
                return null;
            }

            BeaconToken start = Peek();
            BeaconExpr expr = ParseExpr();
            return new ExprStmt(SpanFrom(start.Span, Peek(-1).Span), expr);
        }

        private BeaconStatement? ParseNestedDecl()
        {
            BeaconToken start = Peek();
            BeaconTopDecl? decl = ParseTopDecl();
            if (decl is null)
                return null;

            return new NestedDeclStmt(decl.Span, decl);
        }

        private SetStmt? ParseSet()
        {
            BeaconToken setToken = Advance();
            (string Name, SourceSpan Span)? baseIdent = AcceptIdent() ?? AcceptBuiltinBase();
            if (baseIdent is null)
            {
                BeaconToken found = Peek();
                if (found.Kind == BeaconTokenKind.Symbol && found.Text == "=")
                {
                    Error(found.Span, "'set ... to ...' for assignment",
                        "'='",
                        "Write set x to 5 (never set x = 5 or x = 5).");
                    Advance();
                }
                else
                    ErrorToken(found, "a target after 'set'", "Write set name to \"Steve\".");

                SynchronizeToStatement();
                return null;
            }

            var parts = new List<BeaconTargetPart>();
            while (true)
            {
                if (CheckSymbol("."))
                {
                    BeaconToken dot = Advance();
                    (string Name, SourceSpan Span)? member = AcceptMemberName();
                    if (member is null)
                    {
                        Error(Peek().Span, "a name after '.'",
                            DescribeToken(Peek()),
                            "Write set quiz.running to yes.");
                        break;
                    }

                    parts.Add(new TargetMember(SpanFrom(dot.Span, member.Value.Span), member.Value.Name, member.Value.Span));
                }
                else if (CheckSymbol("["))
                {
                    BeaconToken open = Advance();
                    if (!CanStartExpr(Peek()))
                    {
                        Error(Peek().Span, "an index after '['",
                            DescribeToken(Peek()),
                            "Write set warns[player] to 1.");
                        break;
                    }

                    BeaconExpr index = ParseExpr();
                    if (!CheckSymbol("]"))
                    {
                        Error(Peek().Span, "']' after the index",
                            DescribeToken(Peek()),
                            "Write set warns[player] to 1.");
                    }
                    else
                        Advance();

                    parts.Add(new TargetIndex(SpanFrom(open.Span, Peek(-1).Span), index));
                }
                else
                    break;
            }

            if (CheckKind(BeaconTokenKind.Symbol) && Peek().Text == "=")
            {
                BeaconToken equals = Advance();
                Error(equals.Span, "'to' after the set target",
                    "'='",
                    $"Write set {baseIdent.Value.Name} to ... (never set {baseIdent.Value.Name} = ...).");
                if (CanStartExpr(Peek()))
                {
                    BeaconExpr recovery = ParseExpr();
                    return new SetStmt(
                        SpanFrom(setToken.Span, recovery.Span),
                        new BeaconTarget(SpanFrom(baseIdent.Value.Span, baseIdent.Value.Span), baseIdent.Value.Name, baseIdent.Value.Span, parts),
                        recovery);
                }

                SynchronizeToStatement();
                return null;
            }

            if (!CheckKeyword("to"))
            {
                BeaconToken found = Peek();
                Error(found.Span, "'to' after the set target",
                    DescribeToken(found),
                    $"Write set {baseIdent.Value.Name} to ....");
                SynchronizeToStatement();
                return null;
            }

            Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a value after 'to'",
                    DescribeToken(Peek()),
                    $"Write set {baseIdent.Value.Name} to ....");
                SynchronizeToStatement();
                return null;
            }

            BeaconExpr value = ParseExpr();
            return new SetStmt(
                SpanFrom(setToken.Span, value.Span),
                new BeaconTarget(SpanFrom(baseIdent.Value.Span, baseIdent.Value.Span), baseIdent.Value.Name, baseIdent.Value.Span, parts),
                value);
        }

        private BeaconStatement? ParseSayLike(Func<SourceSpan, BeaconExpr, BeaconStatement> build)
        {
            BeaconToken verb = Peek(-1);
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, $"text after '{verb.Normalized}'",
                    DescribeToken(Peek()),
                    verb.Normalized switch
                    {
                        "say" => "Write say \"Hello!\".",
                        "server" => "Write server \"/home\".",
                        _ => $"Write {verb.Normalized} \"...\".",
                    });
                return null;
            }

            BeaconExpr message = ParseExpr();
            return build(SpanFrom(verb.Span, message.Span), message);
        }

        private BeaconStatement? ParseLog()
        {
            BeaconToken verb = Peek(-1);
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "text after 'log'",
                    DescribeToken(Peek()),
                    "Write show \"...\" (log is accepted as show).");
                return null;
            }

            BeaconExpr message = ParseExpr();
            return new ShowStmt(SpanFrom(verb.Span, message.Span), message, IsLog: true);
        }

        private WhisperStmt? ParseWhisper()
        {
            BeaconToken whisper = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a player plus text after 'whisper'",
                    DescribeToken(Peek()),
                    "Write whisper player \"Meet at spawn?\".");
                return null;
            }

            BeaconExpr player = ParseExpr();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "text after the whisper target",
                    DescribeToken(Peek()),
                    "Write whisper player \"1. Be kind. 2. No griefing.\".");
                return null;
            }

            BeaconExpr message = ParseExpr();
            return new WhisperStmt(SpanFrom(whisper.Span, message.Span), player, message);
        }

        private WaitStmt? ParseWait()
        {
            BeaconToken wait = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a count after 'wait'",
                    DescribeToken(Peek()),
                    "Write wait 2 seconds.");
                return null;
            }

            BeaconExpr count = ParseExpr();
            BeaconToken unitToken = Peek();
            if (unitToken.Kind == BeaconTokenKind.Unit)
            {
                Advance();
                return new WaitStmt(
                    SpanFrom(wait.Span, unitToken.Span), count,
                    unitToken.CanonicalUnit ?? unitToken.Text.ToLowerInvariant(),
                    unitToken.Text, unitToken.Span);
            }

            if (unitToken.Kind == BeaconTokenKind.Identifier)
            {
                Advance();
                return new WaitStmt(
                    SpanFrom(wait.Span, unitToken.Span), count,
                    unitToken.Text.ToLowerInvariant(), unitToken.Text, unitToken.Span);
            }

            ErrorCode(unitToken.Span, BeaconDiagnosticCodes.UnknownUnit,
                $"Missing time unit after 'wait'. Use one of: {string.Join(", ", BeaconUnits.WaitUnits)}.",
                "Write 'wait 2 seconds'.");
            return new WaitStmt(SpanFrom(wait.Span, count.Span), count, string.Empty, string.Empty, count.Span);
        }

        private StopStmt ParseStopBare()
        {
            BeaconToken stop = Advance();
            if (CheckKeyword("event"))
            {
                BeaconToken eventToken = Advance();
                return new StopStmt(SpanFrom(stop.Span, eventToken.Span), HasEvent: true);
            }

            return new StopStmt(stop.Span, HasEvent: false);
        }

        private CancelTaskStmt? ParseCancelTask()
        {
            BeaconToken cancel = Advance();
            return ParseCancelTaskRest(cancel);
        }

        private CancelTaskStmt? ParseCancelTaskRest(BeaconToken cancel)
        {
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a task after 'cancel task'",
                    DescribeToken(Peek()),
                    "Write cancel task id.");
                return null;
            }

            BeaconExpr task = ParseExpr();
            return new CancelTaskStmt(SpanFrom(cancel.Span, task.Span), task);
        }

        private BeaconStatement? ParseCancelBare()
        {
            BeaconToken cancel = Advance();
            if (CheckKeyword("event"))
            {
                Advance();
                return new StopStmt(cancel.Span, HasEvent: true, IsCancelAlias: true);
            }

            if (CheckKeyword("task"))
            {
                Advance();
                return ParseCancelTaskRest(cancel);
            }

            Error(Peek().Span, "'event' or 'task' after 'cancel'",
                DescribeToken(Peek()),
                "Write cancel task id or stop event.");
            return null;
        }

        private DisconnectStmt ParseDisconnect()
        {
            BeaconToken marker = Advance();
            if (!CanStartExpr(Peek()))
                return new DisconnectStmt(marker.Span, null);

            BeaconExpr reason = ParseExpr();
            return new DisconnectStmt(SpanFrom(marker.Span, reason.Span), reason);
        }

        private ReturnStmt ParseReturn()
        {
            BeaconToken marker = Advance();
            if (!CanStartExpr(Peek()))
                return new ReturnStmt(marker.Span, null);

            BeaconExpr value = ParseExpr();
            return new ReturnStmt(SpanFrom(marker.Span, value.Span), value);
        }

        private SaveStmt? ParseSave()
        {
            BeaconToken save = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a key after 'save'",
                    DescribeToken(Peek()),
                    "Write save \"warns\" to warns.");
                return null;
            }

            BeaconExpr key = ParseExpr();
            if (!CheckKeyword("to"))
            {
                Error(Peek().Span, "'to' after the save key",
                    DescribeToken(Peek()),
                    "Write save \"warns\" to warns.");
                return null;
            }

            Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a value after 'to'",
                    DescribeToken(Peek()),
                    "Write save \"warns\" to warns.");
                return null;
            }

            BeaconExpr value = ParseExpr();
            return new SaveStmt(SpanFrom(save.Span, value.Span), key, value);
        }

        private LockStmt ParseLock()
        {
            BeaconToken lockToken = Advance();
            return ParseLockRest(lockToken);
        }

        private LockStmt ParseLockRest(BeaconToken lockToken)
        {
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("lock");
            return new LockStmt(
                SpanFrom(lockToken.Span, end.EndSpan ?? body.Span),
                body, end.Label, end.EndSpan, end.Missing, "lock");
        }

        private LockStmt? ParseLockBare()
        {
            BeaconToken lockToken = Advance();
            if (!CheckKeyword("shared"))
            {
                Error(Peek().Span, "'shared' after 'lock'",
                    DescribeToken(Peek()),
                    "Write lock shared ... end lock.");
                return null;
            }

            Advance();
            return ParseLockRest(lockToken);
        }

        private StartStmt? ParseStart()
        {
            BeaconToken start = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a task call after 'start'",
                    DescribeToken(Peek()),
                    "Write start patrol().");
                return null;
            }

            BeaconExpr task = ParsePostfix();
            return new StartStmt(SpanFrom(start.Span, task.Span), task);
        }

        private AwaitStmt? ParseAwait()
        {
            BeaconToken marker = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a task after 'await'",
                    DescribeToken(Peek()),
                    "Write await id.");
                return null;
            }

            BeaconExpr task = ParseExpr();
            return new AwaitStmt(SpanFrom(marker.Span, task.Span), task);
        }

        private IfStmt? ParseIf()
        {
            BeaconToken ifToken = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a condition after 'if'",
                    DescribeToken(Peek()),
                    "Write if health < 10 then.");
                SynchronizeToBlockEnd();
                return null;
            }

            BeaconExpr cond = ParseExpr();
            BeaconToken condEnd = Peek(-1);
            ParseHeadTail();
            BeaconBlock firstBody = ParseBlock(stopOnElse: true, stopOnCatch: false);
            var branches = new List<BeaconIfBranch>
            {
                new(cond, firstBody, SpanFrom(ifToken.Span, condEnd.Span)),
            };

            BeaconBlock? elseBody = null;
            while (CheckKeyword("else"))
            {
                BeaconToken elseToken = Advance();
                // `else if` on the SAME line continues the chain.
                // An `if` on a later line opens a nested if inside the else body: without this rule a nested if in an else branch would be unrepresentable (every `else`+`if` read as a chain and stole the inner `end if` as the chain closer).
                if (CheckKeyword("if") && Peek().Span.Line == elseToken.Span.Line)
                {
                    Advance();
                    if (!CanStartExpr(Peek()))
                    {
                        Error(Peek().Span, "a condition after 'else if'",
                            DescribeToken(Peek()),
                            "Write else if food < 6 then.");
                        SynchronizeToBlockEnd();
                        break;
                    }

                    BeaconExpr elseCond = ParseExpr();
                    BeaconToken elseCondEnd = Peek(-1);
                    ParseHeadTail();
                    BeaconBlock elseIfBody = ParseBlock(stopOnElse: true, stopOnCatch: false);
                    branches.Add(new BeaconIfBranch(elseCond, elseIfBody, SpanFrom(elseCond.Span, elseCondEnd.Span)));
                }
                else
                {
                    elseBody = ParseBlock(stopOnElse: false, stopOnCatch: false);
                    if (CheckKeyword("else"))
                    {
                        Error(Peek().Span, "'end if' to close the if block",
                            "'else' again",
                            "An if block takes at most one else; add 'end if'.");
                        Advance();
                        BeaconBlock skipped = ParseBlock(stopOnElse: false, stopOnCatch: false);
                        _ = skipped;
                    }

                    break;
                }
            }

            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("if");
            return new IfStmt(
                SpanFrom(ifToken.Span, end.EndSpan ?? (elseBody ?? firstBody).Span),
                branches, elseBody, end.Label, end.EndSpan, end.Missing, "if");
        }

        private WhileStmt? ParseWhile()
        {
            BeaconToken whileToken = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a condition after 'while'",
                    DescribeToken(Peek()),
                    "Write while food < 20.");
                SynchronizeToBlockEnd();
                return null;
            }

            BeaconExpr cond = ParseExpr();
            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("while");
            return new WhileStmt(
                SpanFrom(whileToken.Span, end.EndSpan ?? body.Span),
                cond, body, end.Label, end.EndSpan, end.Missing, "while");
        }

        private RepeatStmt? ParseRepeat()
        {
            BeaconToken repeat = Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a count after 'repeat'",
                    DescribeToken(Peek()),
                    "Write repeat 3 times.");
                SynchronizeToBlockEnd();
                return null;
            }

            BeaconExpr count = ParseExpr();
            if (!CheckKeyword("times"))
            {
                BeaconToken found = Peek();
                string? hint = found.Kind == BeaconTokenKind.Identifier
                    ? DidYouMean(found.Text, ["times"])
                    : null;
                Error(found.Span, "'times' after the repeat count",
                    DescribeToken(found),
                    hint is null ? "Write repeat 3 times." : $"Did you mean 'times'? Write repeat 3 times.");
                if (found.Kind == BeaconTokenKind.Identifier)
                    Advance();
            }
            else
                Advance();

            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("repeat");
            return new RepeatStmt(
                SpanFrom(repeat.Span, end.EndSpan ?? body.Span),
                count, body, end.Label, end.EndSpan, end.Missing, "repeat");
        }

        private ForStmt? ParseFor()
        {
            BeaconToken forToken = Advance();
            return ParseForEachRest(forToken);
        }

        private ForStmt? ParseForBare()
        {
            BeaconToken forToken = Advance();
            if (!CheckKeyword("each"))
            {
                Error(Peek().Span, "'each' after 'for'",
                    DescribeToken(Peek()),
                    "Write for each p in online_players.");
                SynchronizeToBlockEnd();
                return null;
            }

            Advance();
            return ParseForEachRest(forToken);
        }

        private ForStmt? ParseForEachRest(BeaconToken forToken)
        {
            (string Name, SourceSpan Span)? variable = AcceptIdent();
            if (variable is null)
            {
                Error(Peek().Span, "a variable after 'for each'",
                    DescribeToken(Peek()),
                    "Write for each p in online_players.");
                SynchronizeToBlockEnd();
                return null;
            }

            if (!CheckKeyword("in"))
            {
                Error(Peek().Span, "'in' plus a list after the loop variable",
                    DescribeToken(Peek()),
                    $"Write for each {variable.Value.Name} in online_players.");
                SynchronizeToBlockEnd();
                return null;
            }

            Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a list after 'in'",
                    DescribeToken(Peek()),
                    $"Write for each {variable.Value.Name} in online_players.");
                SynchronizeToBlockEnd();
                return null;
            }

            BeaconExpr iterable = ParseExpr();
            ParseHeadTail();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: false);
            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("for");
            return new ForStmt(
                SpanFrom(forToken.Span, end.EndSpan ?? body.Span),
                variable.Value.Name, variable.Value.Span, iterable, body,
                end.Label, end.EndSpan, end.Missing, "for");
        }

        private TryStmt? ParseTry()
        {
            BeaconToken tryToken = Advance();
            BeaconBlock body = ParseBlock(stopOnElse: false, stopOnCatch: true);
            if (!CheckKeyword("catch"))
            {
                Error(Peek().Span, "'catch' plus a name after the try block",
                    DescribeToken(Peek()),
                    "Write try ... catch err ... end try.");
                SynchronizeToBlockEnd();
                return null;
            }

            Advance();
            (string Name, SourceSpan Span)? catchVar = AcceptIdent();
            if (catchVar is null)
            {
                Error(Peek().Span, "a name after 'catch'",
                    DescribeToken(Peek()),
                    "Write catch err.");
                SynchronizeToBlockEnd();
                return null;
            }

            BeaconBlock catchBody = ParseBlock(stopOnElse: false, stopOnCatch: false, stopOnFinally: true);
            BeaconBlock? finallyBody = null;
            if (CheckKeyword("finally"))
            {
                Advance();
                finallyBody = ParseBlock(stopOnElse: false, stopOnCatch: false);
            }

            (string? Label, SourceSpan? EndSpan, bool Missing) end = ParseEnd("try");
            return new TryStmt(
                SpanFrom(tryToken.Span, end.EndSpan ?? catchBody.Span),
                body, catchVar.Value.Name, catchVar.Value.Span, catchBody,
                end.Label, end.EndSpan, end.Missing, "try", finallyBody);
        }

        private BeaconExpr ParseExpr() => ParseOr();

        private BeaconExpr ParseOr()
        {
            BeaconExpr left = ParseAnd();
            while (true)
            {
                BeaconToken token = Peek();
                bool isOr = (token.Kind == BeaconTokenKind.Keyword && token.Normalized == "or")
                    || (token.Kind == BeaconTokenKind.Symbol && token.Text == "||");
                if (!isOr)
                    return left;

                Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, "a value after 'or'",
                        DescribeToken(Peek()),
                        "Write saved(\"seen\") or {}.");
                    return left;
                }

                BeaconExpr right = ParseAnd();
                left = new OrExpr(SpanFrom(left.Span, right.Span), left, right);
            }
        }

        private BeaconExpr ParseAnd()
        {
            BeaconExpr left = ParseNot();
            while (true)
            {
                BeaconToken token = Peek();
                bool isAnd = (token.Kind == BeaconTokenKind.Keyword && token.Normalized == "and")
                    || (token.Kind == BeaconTokenKind.Symbol && token.Text == "&&");
                if (!isAnd)
                    return left;

                Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, "a value after 'and'",
                        DescribeToken(Peek()),
                        "Write a and b.");
                    return left;
                }

                BeaconExpr right = ParseNot();
                left = new AndExpr(SpanFrom(left.Span, right.Span), left, right);
            }
        }

        private BeaconExpr ParseNot()
        {
            BeaconToken token = Peek();
            bool isNot = (token.Kind == BeaconTokenKind.Keyword && token.Normalized == "not")
                || (token.Kind == BeaconTokenKind.Symbol && token.Text == "!");
            if (!isNot)
                return ParseCmp();

            Advance();
            if (!CanStartExpr(Peek()))
            {
                Error(Peek().Span, "a value after 'not'",
                    DescribeToken(Peek()),
                    "Write not tired.");
                return new NotExpr(token.Span, new ErrorExpr(Peek().Span), IsBang: token.Text == "!");
            }

            BeaconExpr operand = ParseNot();
            return new NotExpr(SpanFrom(token.Span, operand.Span), operand, IsBang: token.Text == "!");
        }

        private BeaconExpr ParseCmp()
        {
            BeaconExpr left = ParseAdd();
            BeaconToken token = Peek();
            if (token.Kind == BeaconTokenKind.Keyword)
            {
                switch (token.Normalized)
                {
                    case "is empty":
                        Advance();
                        return new ComparisonExpr(SpanFrom(left.Span, token.Span), BeaconComparisonOp.IsEmpty, left, null);
                    case "is not set":
                        Advance();
                        return new ComparisonExpr(SpanFrom(left.Span, token.Span), BeaconComparisonOp.IsNotSet, left, null);
                    case "is not":
                        Advance();
                        if (CheckKeyword("empty"))
                        {
                            BeaconToken empty = Advance();
                            return new ComparisonExpr(SpanFrom(left.Span, empty.Span), BeaconComparisonOp.IsNotEmpty, left, null);
                        }

                        if (!CanStartExpr(Peek()))
                        {
                            Error(Peek().Span, "a value after 'is not'",
                                DescribeToken(Peek()),
                                "Write x is not \"y\".");
                            return left;
                        }

                        {
                            BeaconExpr right = ParseAdd();
                            return new ComparisonExpr(SpanFrom(left.Span, right.Span), BeaconComparisonOp.IsNot, left, right);
                        }

                    case "is":
                        Advance();
                        if (CheckKeyword("empty"))
                        {
                            BeaconToken empty = Advance();
                            return new ComparisonExpr(SpanFrom(left.Span, empty.Span), BeaconComparisonOp.IsEmpty, left, null);
                        }

                        if (CheckKeyword("set"))
                        {
                            BeaconToken set = Advance();
                            return new ComparisonExpr(SpanFrom(left.Span, set.Span), BeaconComparisonOp.IsSet, left, null);
                        }

                        if (CheckKeyword("not"))
                        {
                            BeaconToken not = Advance();
                            if (CheckKeyword("empty"))
                            {
                                BeaconToken empty = Advance();
                                return new ComparisonExpr(SpanFrom(left.Span, empty.Span), BeaconComparisonOp.IsNotEmpty, left, null);
                            }

                            if (CheckKeyword("set"))
                            {
                                BeaconToken set = Advance();
                                return new ComparisonExpr(SpanFrom(left.Span, set.Span), BeaconComparisonOp.IsNotSet, left, null);
                            }

                            if (!CanStartExpr(Peek()))
                            {
                                Error(Peek().Span, "a value after 'is not'",
                                    DescribeToken(Peek()),
                                    "Write x is not \"y\".");
                                return left;
                            }

                            BeaconExpr notRight = ParseAdd();
                            return new ComparisonExpr(SpanFrom(left.Span, notRight.Span), BeaconComparisonOp.IsNot, left, notRight);
                        }

                        if (!CanStartExpr(Peek()))
                        {
                            Error(Peek().Span, "a value after 'is'",
                                DescribeToken(Peek()),
                                "Write x is \"y\" (or x is set / x is empty).");
                            return left;
                        }

                        {
                            BeaconExpr right = ParseAdd();
                            return new ComparisonExpr(SpanFrom(left.Span, right.Span), BeaconComparisonOp.Is, left, right);
                        }

                    case "contains":
                        Advance();
                        return ParseCmpRight(left, token, BeaconComparisonOp.Contains);
                    case "matches":
                        Advance();
                        return ParseCmpRight(left, token, BeaconComparisonOp.Matches);
                    case "starts with":
                        Advance();
                        return ParseCmpRight(left, token, BeaconComparisonOp.StartsWith);
                    case "ends with":
                        Advance();
                        return ParseCmpRight(left, token, BeaconComparisonOp.EndsWith);
                    case "starts":
                    case "ends":
                        {
                            string first = token.Normalized;
                            Advance();
                            if (!CheckKeyword("with"))
                            {
                                Error(Peek().Span, "'with' after 'starts'/'ends'",
                                    DescribeToken(Peek()),
                                    first == "starts" ? "Write message starts with \"!sell \"."
                                        : "Write name ends with \".txt\".");
                                return left;
                            }

                            Advance();
                            BeaconComparisonOp op = first == "starts"
                                ? BeaconComparisonOp.StartsWith
                                : BeaconComparisonOp.EndsWith;
                            return ParseCmpRight(left, token, op);
                        }

                    default:
                        return left;
                }
            }

            if (token.Kind == BeaconTokenKind.Symbol)
            {
                BeaconComparisonOp? op = token.Text switch
                {
                    "==" => BeaconComparisonOp.Equal,
                    "!=" => BeaconComparisonOp.NotEqual,
                    "<" => BeaconComparisonOp.Less,
                    ">" => BeaconComparisonOp.Greater,
                    "<=" => BeaconComparisonOp.LessEqual,
                    ">=" => BeaconComparisonOp.GreaterEqual,
                    _ => null,
                };
                if (op is null)
                    return left;

                Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, $"a value after '{token.Text}'",
                        DescribeToken(Peek()),
                        token.Text is "==" or "!=" ? "Comparisons spell out: write x is \"y\"."
                            : $"Write x {token.Text} 1.");
                    return left;
                }

                BeaconExpr right = ParseAdd();
                return new ComparisonExpr(SpanFrom(left.Span, right.Span), op.Value, left, right);
            }

            return left;
        }

        private BeaconExpr ParseCmpRight(BeaconExpr left, BeaconToken opToken, BeaconComparisonOp op)
        {
            if (!CanStartExpr(Peek()))
            {
                string word = op switch
                {
                    BeaconComparisonOp.Contains => "contains",
                    BeaconComparisonOp.Matches => "matches",
                    BeaconComparisonOp.StartsWith => "starts with",
                    BeaconComparisonOp.EndsWith => "ends with",
                    _ => opToken.Text,
                };
                Error(Peek().Span, $"a value after '{word}'",
                    DescribeToken(Peek()),
                    word == "matches" ? "Write text matches /pattern/."
                        : $"Write x {word} y.");
                return left;
            }

            BeaconExpr right = ParseAdd();
            return new ComparisonExpr(SpanFrom(left.Span, right.Span), op, left, right);
        }

        private BeaconExpr ParseAdd()
        {
            BeaconExpr left = ParseMul();
            while (Peek().Kind == BeaconTokenKind.Symbol && Peek().Text is "+" or "-")
            {
                BeaconToken op = Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, $"a value after '{op.Text}'",
                        DescribeToken(Peek()),
                        "Write a + b (text with text, numbers with numbers).");
                    return left;
                }

                BeaconExpr right = ParseMul();
                left = new AddExpr(SpanFrom(left.Span, right.Span), op.Text, left, right);
            }

            return left;
        }

        private BeaconExpr ParseMul()
        {
            BeaconExpr left = ParseUnary();
            while (Peek().Kind == BeaconTokenKind.Symbol && Peek().Text is "*" or "/" or "%")
            {
                BeaconToken op = Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, $"a value after '{op.Text}'",
                        DescribeToken(Peek()),
                        "Write a * b.");
                    return left;
                }

                BeaconExpr right = ParseUnary();
                left = new MulExpr(SpanFrom(left.Span, right.Span), op.Text, left, right);
            }

            return left;
        }

        private BeaconExpr ParseUnary()
        {
            if (Peek().Kind == BeaconTokenKind.Symbol && Peek().Text == "-")
            {
                BeaconToken minus = Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, "a value after '-'",
                        DescribeToken(Peek()),
                        "Write -40 or -x.");
                    return new NegateExpr(minus.Span, new ErrorExpr(Peek().Span));
                }

                BeaconExpr operand = ParseUnary();
                return new NegateExpr(SpanFrom(minus.Span, operand.Span), operand);
            }

            return ParsePostfix();
        }

        private BeaconExpr ParsePostfix()
        {
            BeaconExpr target = ParsePrimary();
            while (true)
            {
                if (CheckSymbol("."))
                {
                    Advance();
                    (string Name, SourceSpan Span)? member = AcceptMemberName();
                    if (member is null)
                    {
                        Error(Peek().Span, "a name after '.'",
                            DescribeToken(Peek()),
                            "Write quiz.running or e.message.");
                        return target;
                    }

                    target = new MemberExpr(SpanFrom(target.Span, member.Value.Span), target, member.Value.Name, member.Value.Span);
                }
                else if (CheckSymbol("["))
                {
                    BeaconToken open = Advance();
                    if (!CanStartExpr(Peek()))
                    {
                        Error(Peek().Span, "an index after '['",
                            DescribeToken(Peek()),
                            "Write warns[player].");
                        return target;
                    }

                    BeaconExpr index = ParseExpr();
                    if (!CheckSymbol("]"))
                    {
                        Error(Peek().Span, "']' after the index",
                            DescribeToken(Peek()),
                            "Write warns[player].");
                    }
                    else
                        Advance();

                    target = new IndexExpr(SpanFrom(target.Span, Peek(-1).Span), target, index);
                    _ = open;
                }
                else if (CheckSymbol("("))
                {
                    Advance();
                    var args = ParseArgumentList("Write f(x, y).", "')' after the arguments", "Write f(x).");

                    target = new CallExpr(SpanFrom(target.Span, Peek(-1).Span), target, args);
                }
                else
                    return target;
            }
        }

        private BeaconExpr ParsePrimary()
        {
            BeaconToken token = Peek();
            switch (token.Kind)
            {
                case BeaconTokenKind.Number:
                    Advance();
                    return new NumberLiteral(token.Span, token.NumberValue, token.Text);
                case BeaconTokenKind.Text:
                case BeaconTokenKind.TripleText:
                    Advance();
                    return ParseText(token);
                case BeaconTokenKind.Regex:
                    Advance();
                    return new RegexLiteral(token.Span, token.Pattern, token.Text);
                case BeaconTokenKind.Identifier:
                    Advance();
                    if (string.Equals(token.Text, "mcc", StringComparison.Ordinal)
                        && CanStartExpr(Peek())
                        && !CheckSymbol(".") && !CheckSymbol("[") && !CheckSymbol("("))
                    {
                        BeaconExpr argument = ParsePostfix();
                        return new DmcbkExpr(SpanFrom(token.Span, argument.Span), argument);
                    }

                    return new IdentExpr(token.Span, token.Text);
                case BeaconTokenKind.Unit:
                    Advance();
                    return new IdentExpr(token.Span, token.Text);
                case BeaconTokenKind.Symbol when token.Text == "(":
                    Advance();
                    if (!CanStartExpr(Peek()))
                    {
                        Error(Peek().Span, "an expression after '('",
                            DescribeToken(Peek()),
                            "Write (a + b).");
                        return new ParenExpr(token.Span, new ErrorExpr(Peek().Span));
                    }

                    {
                        BeaconExpr inner = ParseExpr();
                        if (!CheckSymbol(")"))
                        {
                            Error(Peek().Span, "')' after the expression",
                                DescribeToken(Peek()),
                                "Write (a + b).");
                        }
                        else
                            Advance();

                        return new ParenExpr(SpanFrom(token.Span, Peek(-1).Span), inner);
                    }

                case BeaconTokenKind.Symbol when token.Text == "[":
                    return ParseList();
                case BeaconTokenKind.Symbol when token.Text == "{":
                    return ParseMap();
                case BeaconTokenKind.Keyword:
                    return ParseKeywordPrimary();
                default:
                    ErrorToken(token, "a value", "Write a number, \"text\", a name, or (expr).");
                    Advance();
                    return new ErrorExpr(token.Span);
            }
        }

        private BeaconExpr ParseKeywordPrimary()
        {
            BeaconToken token = Peek();
            switch (token.Normalized)
            {
                case "yes":
                    Advance();
                    return new YesLiteral(token.Span);
                case "no":
                    Advance();
                    return new NoLiteral(token.Span);
                case "none":
                    Advance();
                    return new NoneLiteral(token.Span);
                case "call":
                    return ParseCallPrim();
                case "server":
                case "event":
                case "shared":
                    Advance();
                    return new IdentExpr(token.Span, token.Normalized);
                default:
                    ErrorToken(token, "a value",
                        "Write a number, \"text\", a name, or (expr).");
                    Advance();
                    return new ErrorExpr(token.Span);
            }
        }

        private List<BeaconExpr> ParseArgumentList(string missingArgHint, string parenExpected, string parenHint)
        {
            var args = new List<BeaconExpr>();
            if (!CheckSymbol(")"))
            {
                while (true)
                {
                    if (!CanStartExpr(Peek()))
                    {
                        Error(Peek().Span, "an argument",
                            DescribeToken(Peek()),
                            missingArgHint);
                        break;
                    }

                    args.Add(ParseExpr());
                    if (CheckSymbol(","))
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (!CheckSymbol(")"))
            {
                Error(Peek().Span, parenExpected,
                    DescribeToken(Peek()),
                    parenHint);
            }
            else
                Advance();

            return args;
        }

        private BeaconExpr ParseCallPrim()
        {
            BeaconToken call = Advance();
            if (!CheckKind(BeaconTokenKind.Text) && !CheckKind(BeaconTokenKind.TripleText))
            {
                Error(Peek().Span, "a quoted target after 'call'",
                    DescribeToken(Peek()),
                    "Write call \"shopkeeper.daily_report\"().");
                return new ErrorExpr(call.Span);
            }

            BeaconToken targetToken = Advance();
            if (!CheckSymbol("("))
            {
                Error(Peek().Span, "'(' plus arguments after the call target",
                    DescribeToken(Peek()),
                    $"Write call \"{GetStringValue(targetToken)}\"().");
                return new ErrorExpr(SpanFrom(call.Span, targetToken.Span));
            }

            Advance();
            var args = ParseArgumentList(
                "Write call \"shopkeeper.daily_report\"().",
                "')' after the call arguments",
                "Write call \"shopkeeper.daily_report\"().");

            return new CallPrimExpr(
                SpanFrom(call.Span, Peek(-1).Span),
                GetStringValue(targetToken), targetToken.Span, args);
        }

        private ListLiteral ParseList()
        {
            BeaconToken open = Advance();
            var items = new List<BeaconExpr>();
            if (CheckSymbol("]"))
            {
                BeaconToken close = Advance();
                return new ListLiteral(SpanFrom(open.Span, close.Span), items);
            }

            while (true)
            {
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, "a list item",
                        DescribeToken(Peek()),
                        "Write [1, \"two\"] (or [] for empty).");
                    break;
                }

                items.Add(ParseExpr());
                if (CheckSymbol(","))
                {
                    Advance();
                    if (CheckSymbol("]"))
                        break;

                    continue;
                }

                break;
            }

            if (!CheckSymbol("]"))
            {
                Error(Peek().Span, "']' after the list",
                    DescribeToken(Peek()),
                    "Write [1, \"two\"].");
            }
            else
                Advance();

            return new ListLiteral(SpanFrom(open.Span, Peek(-1).Span), items);
        }

        private MapLiteral ParseMap()
        {
            BeaconToken open = Advance();
            var entries = new List<BeaconMapEntry>();
            if (CheckSymbol("}"))
            {
                BeaconToken close = Advance();
                return new MapLiteral(SpanFrom(open.Span, close.Span), entries);
            }

            while (true)
            {
                BeaconToken keyToken = Peek();
                string? key = null;
                SourceSpan keySpan = keyToken.Span;
                if (keyToken.Kind == BeaconTokenKind.Identifier || keyToken.Kind == BeaconTokenKind.Unit)
                {
                    Advance();
                    key = keyToken.Text;
                    keySpan = keyToken.Span;
                }
                else if (keyToken.Kind is BeaconTokenKind.Text or BeaconTokenKind.TripleText)
                {
                    Advance();
                    key = GetStringValue(keyToken);
                    keySpan = keyToken.Span;
                }
                else
                {
                    Error(keyToken.Span, "a map key (a name or quoted text)",
                        DescribeToken(keyToken),
                        "Write {running: no, q: \"\"}.");
                    break;
                }

                if (!CheckSymbol(":"))
                {
                    Error(Peek().Span, "':' plus a value after the map key",
                        DescribeToken(Peek()),
                        $"Write {{{key}: ...}}.");
                    break;
                }

                Advance();
                if (!CanStartExpr(Peek()))
                {
                    Error(Peek().Span, $"a value after '{key}:'",
                        DescribeToken(Peek()),
                        $"Write {{{key}: ...}} (values are never empty).");
                    break;
                }

                BeaconExpr value = ParseExpr();
                entries.Add(new BeaconMapEntry(key, keySpan, value, SpanFrom(keySpan, value.Span)));
                if (CheckSymbol(","))
                {
                    Advance();
                    if (CheckSymbol("}"))
                        break;

                    continue;
                }

                break;
            }

            if (!CheckSymbol("}"))
            {
                Error(Peek().Span, "'}' after the map",
                    DescribeToken(Peek()),
                    "Write {running: no}.");
            }
            else
                Advance();

            return new MapLiteral(SpanFrom(open.Span, Peek(-1).Span), entries);
        }

        private BeaconExpr ParseText(BeaconToken token)
        {
            var parts = new List<BeaconTextNode>();
            foreach (BeaconTextPart part in token.Parts)
            {
                switch (part)
                {
                    case BeaconTextLiteral literal:
                        parts.Add(new TextChunk(literal.Span, literal.Value));
                        break;
                    case BeaconTextHole hole:
                        parts.Add(new TextHole(hole.Span, ParseHole(hole)));
                        break;
                }
            }

            return new TextLiteral(token.Span, parts, token.Kind == BeaconTokenKind.TripleText);
        }

        private BeaconExpr ParseHole(BeaconTextHole hole)
        {
            if (string.IsNullOrWhiteSpace(hole.Expression))
            {
                Error(hole.Span, "an expression inside '{...}'",
                    "an empty hole",
                    "Write \"Hello, {player}!\" (or {{ for a literal brace).");
                return new ErrorExpr(hole.Span);
            }

            BeaconLexResult lexed;
            try
            {
                lexed = BeaconLexer.Lex(hole.Span.File, hole.Expression);
            }
            catch (Exception)
            {
                Error(hole.Span, "an expression inside '{...}'",
                    "unreadable text",
                    "Write \"Hello, {player}!\".");
                return new ErrorExpr(hole.Span);
            }

            IReadOnlyList<BeaconToken> holeTokens = lexed.Tokens;
            var sub = new Parser(hole.Span.File, holeTokens, _major);
            BeaconExpr expr = sub.ParseExpr();
            _diagnostics.AddRange(sub._diagnostics.Select(d => d with
            {
                Span = hole.Span,
            }));
            if (!sub.AtEnd)
            {
                Error(hole.Span, "one expression inside '{...}'",
                    "extra text",
                    "Write \"Hello, {player}!\".");
            }

            return expr is ErrorExpr ? expr : expr with { Span = hole.Span };
        }

        private void SynchronizeToStatement()
        {
            while (!AtEnd && !CheckKeyword("end") && !CheckKeyword("else")
                && !CheckKeyword("catch") && !CheckKeyword("finally") && !IsTopDeclStart())
                Advance();
        }

        private void SynchronizeToBlockEnd()
        {
            while (!AtEnd && !CheckKeyword("end") && !CheckKeyword("else")
                && !CheckKeyword("catch") && !CheckKeyword("finally"))
                Advance();
        }

        private void SynchronizeToTopDecl()
        {
            while (!AtEnd && !IsTopDeclStart())
            {
                if (CheckKeyword("end"))
                    return;

                Advance();
            }
        }

        private void SynchronizeToHeadTail()
        {
            while (!AtEnd && !CheckKeyword("end") && !CheckKeyword("then")
                && !CheckSymbol(":") && !IsStatementStart())
                Advance();
        }

        private bool IsTopDeclStart()
        {
            BeaconToken token = Peek();
            return token.Kind == BeaconTokenKind.Keyword
                && token.Normalized is "on" or "every" or "in" or "function" or "export" or "command";
        }

        private bool IsStatementStart()
        {
            BeaconToken token = Peek();
            if (token.Kind == BeaconTokenKind.Keyword)
            {
                foreach (string keyword in StatementKeywords)
                {
                    if (string.Equals(token.Normalized, keyword, StringComparison.Ordinal))
                        return true;
                }

                return token.Normalized is "on" or "every" or "in" or "function" or "export" or "command";
            }

            return CanStartExpr(token);
        }
    }
}

/// <summary>A top-level declaration found inside a block; the static check reports it (B0001).</summary>
public sealed record NestedDeclStmt(SourceSpan Span, BeaconTopDecl Decl) : BeaconStatement(Span);

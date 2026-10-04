using System.Globalization;
using System.Text;

namespace DMCBK.Core.Beacon;

/// <summary>One lex run: span-tracked tokens plus comments, diagnostics, and the normalized source.</summary>
public sealed record BeaconLexResult(
    IReadOnlyList<BeaconToken> Tokens,
    IReadOnlyList<BeaconDiagnostic> Diagnostics,
    IReadOnlyList<LexedComment> Comments,
    string NormalizedSource);

/// <summary>
/// Beacon tokenizer.
/// Case-insensitive keywords, case-sensitive idents, multiword keywords as single tokens on single spaces, regex only in operand position, units with canonical names, paste-tolerant quotes.
/// Total: never throws on hostile input.
/// </summary>
public static class BeaconLexer
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "set", "to", "on", "every", "when", "as", "end", "if", "else", "while", "repeat",
        "times", "for", "each", "in", "try", "catch", "function", "return", "export",
        "command", "import", "extern", "from", "say", "whisper", "server", "disconnect", "show", "wait",
        "stop", "skip", "break", "continue", "save", "lock", "shared", "start", "await",
        "cancel", "call", "task", "event", "not", "and", "or", "is", "contains", "matches",
        "starts", "ends", "with", "empty", "yes", "no", "none", "then", "cooldown", "named",
        "wants", "finally", "do",
    };

    private static readonly HashSet<string> TwoWordKeywords = new(StringComparer.Ordinal)
    {
        "is not", "is empty", "starts with", "ends with", "for each",
        "else if", "stop event", "cancel event", "cancel task", "lock shared",
    };

    private const string ThreeWordKeyword = "is not set";

    /// <summary>Tokenizes <paramref name="source"/>; spans point at <paramref name="fileName"/>.</summary>
    public static BeaconLexResult Lex(string fileName, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);

        (string normalized, List<int> touchedLines) = NormalizeQuotes(source);
        var scanner = new Scanner(fileName, normalized);
        scanner.Run();

        var diagnostics = new List<BeaconDiagnostic>();
        foreach (int line in touchedLines.OrderBy(l => l))
        {
            string lineText = LineText(normalized, line);
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.PasteNormalization,
                BeaconSeverity.Note,
                $"Line {line} contains pasted smart quotes; they were read as ASCII quotes.",
                new SourceSpan(fileName, line, 1, lineText.Length),
                "Replace the smart quotes with plain \" and ' in your editor to silence this note."));
        }

        diagnostics.AddRange(scanner.Diagnostics);
        BeaconPipeline.SortByLocation(diagnostics);

        return new BeaconLexResult(scanner.Tokens, diagnostics, scanner.Comments, normalized);
    }

    private static (string Normalized, List<int> TouchedLines) NormalizeQuotes(string source)
    {
        var touched = new List<int>();
        bool dirty = false;
        foreach (char c in source)
        {
            if (c is '\u201C' or '\u201D' or '\u201E' or '\u2018' or '\u2019')
            {
                dirty = true;
                break;
            }
        }

        if (!dirty)
            return (source, touched);

        var builder = new StringBuilder(source.Length);
        int line = 1;
        var seen = new HashSet<int>();
        foreach (char c in source)
        {
            char mapped = c switch
            {
                '\u201C' or '\u201D' or '\u201E' => '"',
                '\u2018' or '\u2019' => '\'',
                _ => c,
            };
            if (mapped != c && seen.Add(line))
                touched.Add(line);

            builder.Append(mapped);
            if (c == '\n')
                line++;
        }

        return (builder.ToString(), touched);
    }

    private static string LineText(string source, int line)
    {
        string[] lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
        return line >= 1 && line <= lines.Length ? lines[line - 1].TrimEnd('\r') : string.Empty;
    }

    private sealed class Scanner
    {
        private readonly string _file;
        private readonly string _src;
        private int _pos;
        private int _line = 1;
        private int _col = 1;
        private bool _atLineStart = true;
        private bool _afterWhitespace = true;

        internal readonly List<BeaconToken> Tokens = [];
        internal readonly List<BeaconDiagnostic> Diagnostics = [];
        internal readonly List<LexedComment> Comments = [];

        internal Scanner(string file, string src)
        {
            _file = file;
            _src = src;
        }

        internal void Run()
        {
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '\r' || c == '\n')
                    TakeNewline();
                else if (c == ' ' || c == '\t' || c == '\v' || c == '\f')
                {
                    _pos++;
                    _col++;
                    _afterWhitespace = true;
                }
                else if (c == '#')
                    TakeLineComment();
                else if (c == '/' && Peek(1) == '*')
                    TakeBlockComment();
                else if (c == '/' && Peek(1) == '/' && _afterWhitespace)
                    TakeLineComment();
                else if (c == '/')
                    TakeSlash();
                else if (c == '"')
                    TakeText();
                else if (IsAsciiDigit(c))
                    TakeNumber();
                else if (IsAsciiLetter(c))
                    TakeWord();
                else if (c == '=')
                    TakeEquals();
                else if (c == '!' || c == '<' || c == '>' || c == '&' || c == '|')
                    TakePrefixOperator(c);
                else
                {
                    Add(BeaconTokenKind.Symbol, c.ToString(), 1);
                    _pos++;
                    _col++;
                    _afterWhitespace = false;
                    _atLineStart = false;
                }
            }

            Tokens.Add(new BeaconToken(
                BeaconTokenKind.EndOfFile,
                string.Empty,
                new SourceSpan(_file, _line, _col, 0)));
        }

        private void TakeNewline()
        {
            if (_src[_pos] == '\r' && Peek(1) == '\n')
                _pos += 2;
            else
                _pos++;

            _line++;
            _col = 1;
            _atLineStart = true;
            _afterWhitespace = true;
        }

        private char Peek(int ahead)
        {
            int index = _pos + ahead;
            return index < _src.Length ? _src[index] : '\0';
        }

        private SourceSpan SpanAt(int length) => new(_file, _line, _col, length);

        private void Add(BeaconTokenKind kind, string text, int length)
        {
            Tokens.Add(new BeaconToken(kind, text, SpanAt(length)));
        }

        private void EmitRegex(int start, int line, int col, string pattern)
        {
            Tokens.Add(new BeaconToken(
                BeaconTokenKind.Regex,
                _src[start.._pos],
                new SourceSpan(_file, line, col, _pos - start))
            {
                Pattern = pattern,
            });
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private void EmitText(BeaconTokenKind kind, int start, int line, int col, List<BeaconTextPart> parts)
        {
            Tokens.Add(new BeaconToken(
                kind,
                _src[start.._pos],
                new SourceSpan(_file, line, col, _pos - start))
            {
                Parts = parts,
            });
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private BeaconToken? PreviousSignificant() =>
            Tokens.Count == 0 ? null : Tokens[^1];

        private bool InOperandPosition()
        {
            BeaconToken? previous = PreviousSignificant();
            if (previous is null)
                return true;

            if (previous.Kind == BeaconTokenKind.Keyword
                && previous.Normalized is "matches" or "when" or "return")
                return true;

            return previous.Kind == BeaconTokenKind.Symbol
                && previous.Text is "(" or "," or "[" or ":";
        }

        private void TakeLineComment()
        {
            int start = _pos;
            int line = _line;
            int col = _col;
            while (_pos < _src.Length && _src[_pos] != '\r' && _src[_pos] != '\n')
            {
                _pos++;
                _col++;
            }

            Comments.Add(new LexedComment(
                _src[start.._pos],
                new SourceSpan(_file, line, col, _pos - start)));
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private void TakeBlockComment()
        {
            int line = _line;
            int col = _col;
            _pos += 2;
            _col += 2;
            while (_pos < _src.Length)
            {
                if (_src[_pos] == '*' && Peek(1) == '/')
                {
                    _pos += 2;
                    _col += 2;
                    _afterWhitespace = false;
                    _atLineStart = false;
                    return;
                }

                if (_src[_pos] == '\r' || _src[_pos] == '\n')
                    TakeNewline();
                else
                {
                    _pos++;
                    _col++;
                }
            }

            Diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnclosedBlockComment,
                BeaconSeverity.Warning,
                "Unclosed block comment; it was closed at the end of the file.",
                new SourceSpan(_file, line, col, 2),
                "Add '*/' where the comment should end."));
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private void TakeSlash()
        {
            if (_atLineStart)
            {
                int line = _line;
                string rest = LineText(_src, line).TrimStart();
                string path = rest.StartsWith("/", StringComparison.Ordinal) ? rest[1..].Trim() : rest;
                Diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.StrictLeadingSlash,
                    BeaconSeverity.Error,
                    "A statement line must not start with '/'. Use 'server' for server commands or 'mcc' for client commands.",
                    SpanAt(1),
                    $"server \"/{path}\""));
                Add(BeaconTokenKind.Symbol, "/", 1);
                _pos++;
                _col++;
                _afterWhitespace = false;
                _atLineStart = false;
                return;
            }

            if (InOperandPosition() && Peek(1) != '/' && Peek(1) != '*')
            {
                TakeRegex();
                return;
            }

            Add(BeaconTokenKind.Symbol, "/", 1);
            _pos++;
            _col++;
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private void TakeRegex()
        {
            int start = _pos;
            int line = _line;
            int col = _col;
            _pos++;
            _col++;
            var pattern = new StringBuilder();
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '\\' && _pos + 1 < _src.Length)
                {
                    pattern.Append(c);
                    pattern.Append(_src[_pos + 1]);
                    _pos += 2;
                    _col += 2;
                }
                else if (c == '/')
                {
                    _pos++;
                    _col++;
                    EmitRegex(start, line, col, pattern.ToString());
                    return;
                }
                else if (c == '\r' || c == '\n')
                {
                    pattern.Append(c);
                    TakeNewline();
                }
                else
                {
                    pattern.Append(c);
                    _pos++;
                    _col++;
                }
            }

            Diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                "Unclosed regex; add the closing '/'.",
                new SourceSpan(_file, line, col, _pos - start),
                "Close the pattern with '/' (write '\\/' for a literal slash)."));
            EmitRegex(start, line, col, pattern.ToString());
        }

        private void TakeText()
        {
            bool triple = Peek(1) == '"' && Peek(2) == '"';
            int start = _pos;
            int line = _line;
            int col = _col;
            _pos += triple ? 3 : 1;
            _col += triple ? 3 : 1;

            var parts = new List<BeaconTextPart>();
            var literal = new StringBuilder();
            int literalLine = _line;
            int literalCol = _col;

            void FlushLiteral()
            {
                if (literal.Length > 0)
                {
                    parts.Add(new BeaconTextLiteral(
                        literal.ToString(),
                        new SourceSpan(_file, literalLine, literalCol, 0)));
                    literal.Clear();
                }

                literalLine = _line;
                literalCol = _col;
            }

            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (!triple && (c == '\r' || c == '\n'))
                {
                    FlushLiteral();
                    Diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        "Unclosed text; add the closing quote on the same line (triple quotes span lines).",
                        new SourceSpan(_file, line, col, _pos - start),
                        "Close the string with \" or rewrite it with \"\"\"...\"\"\"."));
                    EmitText(BeaconTokenKind.Text, start, line, col, parts);
                    return;
                }

                if (triple && c == '"' && Peek(1) == '"' && Peek(2) == '"')
                {
                    FlushLiteral();
                    _pos += 3;
                    _col += 3;
                    EmitText(BeaconTokenKind.TripleText, start, line, col, parts);
                    return;
                }

                if (!triple && c == '"')
                {
                    FlushLiteral();
                    _pos++;
                    _col++;
                    EmitText(BeaconTokenKind.Text, start, line, col, parts);
                    return;
                }

                if (c == '\\' && _pos + 1 < _src.Length && (triple || _src[_pos + 1] != '\r' && _src[_pos + 1] != '\n'))
                {
                    // Named escapes for the characters a script cannot otherwise spell (a real newline would end a single-line string; a literal backslash-n used to collapse to just 'n').
                    // Anything else keeps the old rule of dropping the backslash, so existing text is unaffected.
                    char escaped = _src[_pos + 1] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => _src[_pos + 1],
                    };
                    literal.Append(escaped);
                    _pos += 2;
                    _col += 2;
                }
                else if (c == '{' && Peek(1) == '{')
                {
                    literal.Append('{');
                    _pos += 2;
                    _col += 2;
                }
                else if (c == '}' && Peek(1) == '}')
                {
                    literal.Append('}');
                    _pos += 2;
                    _col += 2;
                }
                else if (c == '{')
                {
                    FlushLiteral();
                    if (!TakeHole(parts))
                    {
                        EmitText(triple ? BeaconTokenKind.TripleText : BeaconTokenKind.Text, start, line, col, parts);
                        return;
                    }

                    literalLine = _line;
                    literalCol = _col;
                }
                else if (c == '\r' || c == '\n')
                {
                    literal.Append(c);
                    TakeNewline();
                }
                else
                {
                    literal.Append(c);
                    _pos++;
                    _col++;
                }
            }

            FlushLiteral();
            Diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                triple ? "Unclosed multiline text; add the closing triple quote." : "Unclosed text; add the closing quote.",
                new SourceSpan(_file, line, col, _pos - start),
                triple ? "Close the string with \"\"\"." : "Close the string with \"."));
            EmitText(triple ? BeaconTokenKind.TripleText : BeaconTokenKind.Text, start, line, col, parts);
        }

        private bool TakeHole(List<BeaconTextPart> parts)
        {
            // At '{'; captures raw source to the matching close brace with nesting for maps.
            _pos++;
            _col++;
            int innerLine = _line;
            int innerCol = _col;
            int innerStart = _pos;
            int depth = 1;
            while (_pos < _src.Length && depth > 0)
            {
                char c = _src[_pos];
                if (c == '{')
                {
                    depth++;
                    _pos++;
                    _col++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        break;

                    _pos++;
                    _col++;
                }
                else if (c == '\r' || c == '\n')
                {
                    // Holes stay on one line; a newline ends the hole and the string.
                    Diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        "Unclosed interpolation hole; add the closing '}'.",
                        new SourceSpan(_file, innerLine, innerCol, _pos - innerStart),
                        "Close the hole with '}' or write '{{' for a literal brace."));
                    return false;
                }
                else
                {
                    _pos++;
                    _col++;
                }
            }

            if (depth > 0)
            {
                Diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    "Unclosed interpolation hole; add the closing '}'.",
                    new SourceSpan(_file, innerLine, innerCol, _pos - innerStart),
                    "Close the hole with '}' or write '{{' for a literal brace."));
                return false;
            }

            string expression = _src[innerStart.._pos].Trim();
            parts.Add(new BeaconTextHole(
                expression,
                new SourceSpan(_file, innerLine, innerCol, _pos - innerStart)));
            _pos++;
            _col++;
            return true;
        }

        private void TakeNumber()
        {
            int start = _pos;
            int line = _line;
            int col = _col;
            while (_pos < _src.Length && IsAsciiDigit(_src[_pos]))
            {
                _pos++;
                _col++;
            }

            if (_pos < _src.Length && _src[_pos] == '.' && _pos + 1 < _src.Length && IsAsciiDigit(_src[_pos + 1]))
            {
                _pos++;
                _col++;
                while (_pos < _src.Length && IsAsciiDigit(_src[_pos]))
                {
                    _pos++;
                    _col++;
                }
            }

            string text = _src[start.._pos];
            double value = double.Parse(text, CultureInfo.InvariantCulture);
            Tokens.Add(new BeaconToken(
                BeaconTokenKind.Number,
                text,
                new SourceSpan(_file, line, col, _pos - start))
            {
                NumberValue = value,
            });
            _afterWhitespace = false;
            _atLineStart = false;
        }

        private void TakeWord()
        {
            int start = _pos;
            int line = _line;
            int col = _col;
            while (_pos < _src.Length && IsWordChar(_src[_pos]))
            {
                _pos++;
                _col++;
            }

            string word = _src[start.._pos];
            string lower = word.ToLowerInvariant();

            if (TryMatchMultiword(lower, out string? normalized, out int end))
            {
                string text = _src[start..end];
                int consumed = end - _pos;
                _pos = end;
                _col += consumed;
                Tokens.Add(new BeaconToken(
                    BeaconTokenKind.Keyword,
                    text,
                    new SourceSpan(_file, line, col, end - start))
                {
                    Normalized = normalized ?? lower,
                });
                _afterWhitespace = false;
                _atLineStart = false;
                return;
            }

            if (BeaconUnits.TryGetCanonical(word, out string? canonical))
            {
                Tokens.Add(new BeaconToken(
                    BeaconTokenKind.Unit,
                    word,
                    new SourceSpan(_file, line, col, _pos - start))
                {
                    Normalized = word,
                    CanonicalUnit = canonical,
                });
            }
            else if (ReservedWords.Contains(lower))
            {
                Tokens.Add(new BeaconToken(
                    BeaconTokenKind.Keyword,
                    word,
                    new SourceSpan(_file, line, col, _pos - start))
                {
                    Normalized = lower,
                });
            }
            else
            {
                Tokens.Add(new BeaconToken(
                    BeaconTokenKind.Identifier,
                    word,
                    new SourceSpan(_file, line, col, _pos - start)));
            }

            _afterWhitespace = false;
            _atLineStart = false;
        }

        private bool TryMatchMultiword(string first, out string? normalized, out int end)
        {
            normalized = null;
            end = _pos;
            bool canStart = first is "is" or "starts" or "ends" or "for" or "else" or "stop" or "cancel" or "lock";
            if (!canStart || _pos >= _src.Length || _src[_pos] != ' ')
                return false;

            if (!TryReadWord(_pos + 1, out int secondEnd))
                return false;

            string second = _src[(_pos + 1)..secondEnd].ToLowerInvariant();
            if (first == "is" && second == "not" && secondEnd < _src.Length && _src[secondEnd] == ' '
                && TryReadWord(secondEnd + 1, out int thirdEnd)
                && _src[(secondEnd + 1)..thirdEnd].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                normalized = ThreeWordKeyword;
                end = thirdEnd;
                return true;
            }

            string candidate = first + " " + second;
            if (TwoWordKeywords.Contains(candidate))
            {
                normalized = candidate;
                end = secondEnd;
                return true;
            }

            return false;
        }

        private bool TryReadWord(int index, out int end)
        {
            end = index;
            if (index >= _src.Length || !IsAsciiLetter(_src[index]))
                return false;

            while (end < _src.Length && IsWordChar(_src[end]))
                end++;

            return true;
        }

        private void TakeEquals()
        {
            if (Peek(1) == '=')
            {
                Add(BeaconTokenKind.Symbol, "==", 2);
                _pos += 2;
                _col += 2;
            }
            else
            {
                string lineText = LineText(_src, _line);
                string trimmed = lineText.Trim();
                string suggestion = SuggestEqualsFix(trimmed);
                Diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.StrictEquals,
                    BeaconSeverity.Error,
                    "The '=' sign never assigns or compares in Beacon. Use 'set ... to ...' for assignment or 'is' for comparison.",
                    SpanAt(1),
                    suggestion));
                Add(BeaconTokenKind.Symbol, "=", 1);
                _pos++;
                _col++;
            }

            _afterWhitespace = false;
            _atLineStart = false;
        }

        private static string SuggestEqualsFix(string trimmedLine)
        {
            int equals = trimmedLine.IndexOf('=');
            if (equals < 0)
                return "Use 'set ... to ...' for assignment or 'is' for comparison.";

            string fixedLine = trimmedLine[..equals] + "to" + trimmedLine[(equals + 1)..];
            if (trimmedLine.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                // Collapse the doubled space left behind: "set x = 5" becomes "set x to 5".
                return fixedLine.Replace("  ", " ", StringComparison.Ordinal);

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmedLine, @"^\w+\s*="))
                return "set " + fixedLine.Replace("  ", " ", StringComparison.Ordinal);

            return trimmedLine[..equals] + "is" + trimmedLine[(equals + 1)..];
        }

        private void TakePrefixOperator(char c)
        {
            if (c == '!' && Peek(1) == '=')
            {
                Add(BeaconTokenKind.Symbol, "!=", 2);
                _pos += 2;
                _col += 2;
            }
            else if (c == '<' && Peek(1) == '=')
            {
                Add(BeaconTokenKind.Symbol, "<=", 2);
                _pos += 2;
                _col += 2;
            }
            else if (c == '>' && Peek(1) == '=')
            {
                Add(BeaconTokenKind.Symbol, ">=", 2);
                _pos += 2;
                _col += 2;
            }
            else if (c == '&' && Peek(1) == '&')
            {
                Add(BeaconTokenKind.Symbol, "&&", 2);
                _pos += 2;
                _col += 2;
            }
            else if (c == '|' && Peek(1) == '|')
            {
                Add(BeaconTokenKind.Symbol, "||", 2);
                _pos += 2;
                _col += 2;
            }
            else
            {
                Add(BeaconTokenKind.Symbol, c.ToString(), 1);
                _pos++;
                _col++;
            }

            _afterWhitespace = false;
            _atLineStart = false;
        }

        private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

        private static bool IsWordChar(char c) => IsAsciiLetter(c) || IsAsciiDigit(c) || c == '_';
    }
}

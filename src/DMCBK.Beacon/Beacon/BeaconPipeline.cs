namespace DMCBK.Core.Beacon;

/// <summary>
/// Shared front-door pipeline: lexing plus mandatory header parsing.
/// Consolidates the four lex plus first-code-line plus header copies (engine lint, fix preview, import loader, lint closure) so the front door can never drift between entry points.
/// </summary>
internal static class BeaconPipeline
{
    /// <summary>
    /// Lexes <paramref name="source"/> and parses its header, reporting the first code line for late-manifest detection.
    /// </summary>
    internal static BeaconHeaderResult LexAndParseHeader(
        string fileName, string source, out BeaconLexResult lexed, out int? firstCodeLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);
        lexed = BeaconLexer.Lex(fileName, source);
        firstCodeLine = FirstCodeLine(lexed);
        return BeaconHeader.Parse(fileName, lexed.NormalizedSource, firstCodeLine, lexed.Comments);
    }

    /// <summary>Finds the line of the first non-EOF token, if any.</summary>
    internal static int? FirstCodeLine(BeaconLexResult lexed)
    {
        ArgumentNullException.ThrowIfNull(lexed);
        foreach (BeaconToken token in lexed.Tokens)
        {
            if (token.Kind != BeaconTokenKind.EndOfFile)
                return token.Span.Line;
        }

        return null;
    }

    /// <summary>Sorts diagnostics by line then column, in place.</summary>
    internal static void SortByLocation(List<BeaconDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        diagnostics.Sort((a, b) =>
        {
            int line = a.Span.Line.CompareTo(b.Span.Line);
            return line != 0 ? line : a.Span.Column.CompareTo(b.Span.Column);
        });
    }
}

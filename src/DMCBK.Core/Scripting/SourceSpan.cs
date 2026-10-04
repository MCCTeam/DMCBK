namespace DMCBK.Core.Beacon;

/// <summary>
/// A file-anchored text range.
/// The pipeline tracks this through lexing, desugaring, and imports so every diagnostic can render <c>file:line:col</c> plus an excerpt against the user's own source.
/// A reference type so desugared spans can chain back to their origin (a struct cannot).
/// </summary>
public sealed record SourceSpan
{
    /// <summary>Builds a span; <paramref name="line"/> and <paramref name="column"/> are 1-based.</summary>
    public SourceSpan(string file, int line, int column, int length, SourceSpan? desugaredFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 0);
        File = file;
        Line = line;
        Column = column;
        Length = length;
        DesugaredFrom = desugaredFrom;
    }

    /// <summary>The source file name as the script author sees it.</summary>
    public string File { get; init; }

    /// <summary>1-based line number.</summary>
    public int Line { get; init; }

    /// <summary>1-based column number.</summary>
    public int Column { get; init; }

    /// <summary>Length in characters; zero marks a point position.</summary>
    public int Length { get; init; }

    /// <summary>
    /// Pre-desugar span when this span was produced by desugaring; null when this span already points at original source.
    /// Chains compose: each link points one step closer to the user's text.
    /// </summary>
    public SourceSpan? DesugaredFrom { get; init; }

    /// <summary>Exclusive end column.</summary>
    public int EndColumn => Column + Length;

    /// <summary>True for a zero-length point position.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Walks the desugar chain to the span in the user's original source.</summary>
    public SourceSpan Origin
    {
        get
        {
            SourceSpan current = this;
            while (current.DesugaredFrom is { } parent)
                current = parent;

            return current;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{File ?? "<unknown>"}:{Line}:{Column}";
}

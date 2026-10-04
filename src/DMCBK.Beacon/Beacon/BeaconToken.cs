namespace DMCBK.Core.Beacon;

/// <summary>Token kinds produced by <see cref="BeaconLexer"/>; every token carries a <see cref="SourceSpan"/>.</summary>
public enum BeaconTokenKind
{
    /// <summary>Case-sensitive variable or builtin name.</summary>
    Identifier,
    /// <summary>Digits with an optional fraction; no hex or exponents.</summary>
    Number,
    /// <summary>Double-quoted text with interpolation holes.</summary>
    Text,
    /// <summary>Triple-quoted multiline text with interpolation holes.</summary>
    TripleText,
    /// <summary>Slash-delimited pattern; lexed only in operand position.</summary>
    Regex,
    /// <summary>Case-insensitive reserved word, including multiword keywords lexed as one.</summary>
    Keyword,
    /// <summary>Known time-unit spelling with a canonical singular name.</summary>
    Unit,
    /// <summary>Punctuation or operator.</summary>
    Symbol,
    /// <summary>Stream terminator appended by the lexer.</summary>
    EndOfFile,
}

/// <summary>One parsed piece of a text token: literal characters or an interpolation hole.</summary>
public abstract record BeaconTextPart;

/// <summary>Literal characters of a text token with the source range they came from.</summary>
public sealed record BeaconTextLiteral(string Value, SourceSpan Span) : BeaconTextPart;

/// <summary>An interpolation hole with its raw inner expression source.</summary>
public sealed record BeaconTextHole(string Expression, SourceSpan Span) : BeaconTextPart;

/// <summary>A span-tracked lexical token; the stream is append-only for the parser.</summary>
public sealed record BeaconToken(BeaconTokenKind Kind, string Text, SourceSpan Span)
{
    /// <summary>Lowercased form for keywords; the raw lexeme otherwise.</summary>
    public string Normalized { get; init; } = Text;

    /// <summary>Canonical singular unit name for <see cref="BeaconTokenKind.Unit"/>; null otherwise.</summary>
    public string? CanonicalUnit { get; init; }

    /// <summary>Parsed value for <see cref="BeaconTokenKind.Number"/>; zero otherwise.</summary>
    public double NumberValue { get; init; }

    /// <summary>Inner pattern for <see cref="BeaconTokenKind.Regex"/>; empty otherwise.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Literal and hole parts for text tokens; empty otherwise.</summary>
    public IReadOnlyList<BeaconTextPart> Parts { get; init; } = [];
}

/// <summary>A skipped comment with its source range (manifest lines are re-read by <see cref="BeaconHeader"/>).</summary>
public sealed record LexedComment(string Text, SourceSpan Span);

/// <summary>
/// Unit spelling tables shared by the lexer and the lint-time unit check.
/// Canonical names are singular (<c>millisecond</c>, <c>second</c>, <c>minute</c>, <c>hour</c>); <c>sec</c>, <c>s</c>, and <c>min</c> are aliases.
/// Matching is case-insensitive.
/// </summary>
public static class BeaconUnits
{
    /// <summary>Units accepted after <c>wait</c>.</summary>
    public static IReadOnlyList<string> WaitUnits { get; } = ["millisecond", "second", "minute"];

    /// <summary>Units accepted after <c>every</c>.</summary>
    public static IReadOnlyList<string> EveryUnits { get; } = ["second", "minute", "hour"];

    /// <summary>Units accepted after <c>cooldown</c>.</summary>
    public static IReadOnlyList<string> CooldownUnits { get; } = ["second", "minute", "hour"];

    /// <summary>Units accepted after <c>in</c> (one-shot timers share the <c>every</c> range).</summary>
    public static IReadOnlyList<string> OnceUnits { get; } = ["second", "minute", "hour"];

    private static readonly Dictionary<string, string> CanonicalBySpelling = new(StringComparer.OrdinalIgnoreCase)
    {
        ["millisecond"] = "millisecond",
        ["milliseconds"] = "millisecond",
        ["second"] = "second",
        ["seconds"] = "second",
        ["sec"] = "second",
        ["s"] = "second",
        ["minute"] = "minute",
        ["minutes"] = "minute",
        ["min"] = "minute",
        ["hour"] = "hour",
        ["hours"] = "hour",
    };

    /// <summary>Resolves any known spelling to its canonical name; false for unknown words.</summary>
    public static bool TryGetCanonical(string spelling, out string canonical)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        if (CanonicalBySpelling.TryGetValue(spelling, out string? found))
        {
            canonical = found;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="canonical"/> is allowed after <c>wait</c>.</summary>
    public static bool AllowedAfterWait(string canonical) => WaitUnits.Contains(canonical, StringComparer.Ordinal);

    /// <summary>True when <paramref name="canonical"/> is allowed after <c>every</c>.</summary>
    public static bool AllowedAfterEvery(string canonical) => EveryUnits.Contains(canonical, StringComparer.Ordinal);

    /// <summary>True when <paramref name="canonical"/> is allowed after <c>cooldown</c>.</summary>
    public static bool AllowedAfterCooldown(string canonical) => CooldownUnits.Contains(canonical, StringComparer.Ordinal);

    /// <summary>True when <paramref name="canonical"/> is allowed after <c>in</c>.</summary>
    public static bool AllowedAfterOnce(string canonical) => OnceUnits.Contains(canonical, StringComparer.Ordinal);
}

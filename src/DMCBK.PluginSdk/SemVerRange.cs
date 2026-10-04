using System.Globalization;

namespace DMCBK.PluginSdk;

/// <summary>
/// A version range in npm syntax, the notation every plugin ecosystem already uses.
/// One parser serves the manifest's <c>mcc</c> and <c>umpk</c> gates and plugin-to-plugin <c>[requires]</c>.
/// <para>Supported forms, which is all this host promises to read:</para>
/// <list type="bullet">
///   <item><description><c>*</c>, an empty string, <c>x</c> or <c>X</c>: any version.</description></item>
///   <item><description><c>1.2.3</c> or <c>=1.2.3</c>: exactly that version.</description></item>
/// <item><description>
/// <c>1.2</c>, <c>1</c>, <c>1.2.x</c>, <c>1.x</c>: the partial-version range, so <c>1.2</c> means <c>&gt;=1.2.0 &lt;1.3.0</c>.
/// </description></item>
///   <item><description><c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c> with a full or partial version.</description></item>
///   <item><description><c>^1.2.3</c>: up to the next left-most non-zero component.</description></item>
///   <item><description><c>~1.2.3</c>: up to the next minor.</description></item>
///   <item><description>Space-separated comparators are ANDed; <c>||</c> separates ORed sets.</description></item>
/// </list>
/// <para>
/// Comparators use full SemVer precedence. Build metadata never affects ordering. Prereleases are admitted by explicit bounds on the same core triple or by caller policy.
/// </para>
/// </summary>
public sealed class SemVerRange
{
    private readonly Comparator[][] _sets;

    private SemVerRange(string text, Comparator[][] sets)
    {
        Text = text;
        _sets = sets;
    }

    /// <summary>The range exactly as it was written in the manifest.</summary>
    public string Text { get; }

    /// <summary>True when the range admits every version (<c>*</c> or an empty string).</summary>
    public bool IsAny => _sets.Length == 1 && _sets[0].Length == 0;

    /// <summary>A range that admits every version.</summary>
    public static SemVerRange Any { get; } = new("*", [[]]);

    /// <summary>
    /// Parses a range.
    /// Returns false with the range left at null when a comparator is not one of the supported forms; the caller turns that into a manifest error naming the offending text.
    /// </summary>
    public static bool TryParse(string? text, out SemVerRange? range)
    {
        range = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            range = Any;
            return true;
        }

        string[] alternatives = value.Split("||", StringSplitOptions.TrimEntries);
        var sets = new Comparator[alternatives.Length][];
        for (int i = 0; i < alternatives.Length; i++)
        {
            if (!TryParseSet(alternatives[i], out Comparator[]? set))
                return false;

            sets[i] = set;
        }

        range = new SemVerRange(value, sets);
        return true;
    }

    /// <summary>Parses a range, throwing <see cref="FormatException"/> when the text is not one.</summary>
    public static SemVerRange Parse(string text)
        => TryParse(text, out SemVerRange? range) && range is not null
            ? range
            : throw new FormatException(PluginStrings.RangeInvalid(text));

    /// <summary>
    /// True when a version satisfies all comparators in one alternative. Prereleases require an explicit bound on the same core triple unless enabled by the caller.
    /// <paramref name="includePrerelease"/> enables prerelease candidates without changing comparator precedence.
    /// </summary>
    public bool Satisfies(SemVer version, bool includePrerelease = false)
    {
        foreach (Comparator[] set in _sets)
        {
            bool all = true;
            foreach (Comparator comparator in set)
            {
                if (!comparator.Matches(version))
                {
                    all = false;
                    break;
                }
            }

            if (all && (!version.IsPrerelease || includePrerelease
                || set.Any(c => c.Bound.IsPrerelease && c.Bound.CompareCore(version) == 0)))
                return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="version"/> parses and falls in the range; false when it does not parse.</summary>
    public bool Satisfies(string? version)
        => SemVer.TryParse(version, out SemVer parsed) && Satisfies(parsed);

    /// <inheritdoc/>
    public override string ToString() => Text;

    private static bool TryParseSet(string text, out Comparator[] set)
    {
        set = [];
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;

        var comparators = new List<Comparator>();
        foreach (string token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseComparator(token, comparators))
                return false;
        }

        set = [.. comparators];
        return true;
    }

    private static bool TryParseComparator(string token, List<Comparator> into)
    {
        (Op op, string rest) = SplitOperator(token);
        if (rest.Length == 0)
            return false;

        if (op == Op.Eq && IsWildcard(rest))
            // "*" as a whole comparator: no constraint at all.
            return true;

        if (!TryParsePartial(rest, out Partial partial))
            return false;

        if (partial.Specified == 0)
            // Every component was a wildcard, so the comparator constrains nothing whatever the operator.
            return true;

        switch (op)
        {
            case Op.Gte:
            case Op.Lt:
                into.Add(new Comparator(op, partial.ToLowerBound()));
                return true;

            case Op.Gt:
                // npm reads ">1.2" as ">=1.3.0": the whole 1.2 line is below the bound, not just 1.2.0.
                into.Add(partial.IsComplete
                    ? new Comparator(Op.Gt, partial.ToLowerBound())
                    : new Comparator(Op.Gte, partial.PartialUpperBound()));
                return true;

            case Op.Lte:
                // And "<=1.2" as "<1.3.0", for the same reason read from the other end.
                into.Add(partial.IsComplete
                    ? new Comparator(Op.Lte, partial.ToLowerBound())
                    : new Comparator(Op.Lt, partial.PartialUpperBound()));
                return true;

            case Op.Caret:
                into.Add(new Comparator(Op.Gte, partial.ToLowerBound()));
                into.Add(new Comparator(Op.Lt, partial.CaretUpperBound()));
                return true;

            case Op.Tilde:
                into.Add(new Comparator(Op.Gte, partial.ToLowerBound()));
                into.Add(new Comparator(Op.Lt, partial.TildeUpperBound()));
                return true;

            default:
                if (partial.IsComplete)
                {
                    into.Add(new Comparator(Op.Eq, partial.ToLowerBound()));
                    return true;
                }

                // A partial version with no operator is the same as "x" in the missing place: "1.2" is ">=1.2.0 <1.3.0", "1" is ">=1.0.0 <2.0.0".
                into.Add(new Comparator(Op.Gte, partial.ToLowerBound()));
                into.Add(new Comparator(Op.Lt, partial.PartialUpperBound()));
                return true;
        }
    }

    private static (Op Op, string Version) SplitOperator(string token)
    {
        if (token.StartsWith(">=", StringComparison.Ordinal))
            return (Op.Gte, token[2..].Trim());

        if (token.StartsWith("<=", StringComparison.Ordinal))
            return (Op.Lte, token[2..].Trim());

        if (token.StartsWith('>'))
            return (Op.Gt, token[1..].Trim());

        if (token.StartsWith('<'))
            return (Op.Lt, token[1..].Trim());

        if (token.StartsWith('^'))
            return (Op.Caret, token[1..].Trim());

        if (token.StartsWith('~'))
            return (Op.Tilde, token[1..].Trim());

        if (token.StartsWith('='))
            return (Op.Eq, token[1..].Trim());

        return (Op.Eq, token.Trim());
    }

    private static bool IsWildcard(string text)
        => text is "*" or "x" or "X";

    /// <summary>Parses a full or partial version: <c>1</c>, <c>1.2</c>, <c>1.2.3</c>, <c>1.x</c>, <c>1.2.*</c>.</summary>
    private static bool TryParsePartial(string text, out Partial partial)
    {
        partial = default;
        string value = text;
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        string? prerelease = null;
        string? build = null;
        // Retain full SemVer bounds, including prerelease precedence.
        int cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            if (!SemVer.TryParse(value, out SemVer full)) return false;
            prerelease = full.Prerelease;
            build = full.Build;
            value = value[..cut];
        }

        if (value.Length == 0)
            return false;

        string[] parts = value.Split('.');
        if (parts.Length > 3)
            return false;

        Span<int> numbers = [0, 0, 0];
        int specified = 0;
        bool wildcardSeen = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (IsWildcard(parts[i]))
            {
                wildcardSeen = true;
                continue;
            }

            if (wildcardSeen
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                return false;

            numbers[i] = number;
            specified = i + 1;
        }

        partial = new Partial(numbers[0], numbers[1], numbers[2], specified, prerelease, build);
        return true;
    }

    private enum Op
    {
        Eq,
        Gt,
        Gte,
        Lt,
        Lte,
        Caret,
        Tilde,
    }

    /// <summary>A parsed version with a count of how many components the author actually wrote.</summary>
    private readonly record struct Partial(int Major, int Minor, int Patch, int Specified, string? Prerelease, string? Build)
    {
        public bool IsComplete => Specified >= 3;

        public SemVer ToLowerBound() => new(Major, Minor, Patch, Prerelease, Build);

        /// <summary>The exclusive upper bound of a bare partial version: <c>1.2</c> stops at <c>1.3.0</c>.</summary>
        public SemVer PartialUpperBound()
            => Specified <= 1 ? new SemVer(Major + 1, 0, 0) : new SemVer(Major, Minor + 1, 0);

        /// <summary>Caret: up to the next left-most non-zero component. <c>^0.2.3</c> stops at <c>0.3.0</c>.</summary>
        public SemVer CaretUpperBound()
        {
            if (Major != 0)
                return new SemVer(Major + 1, 0, 0);

            // "^0" is "<1.0.0"; "^0.0" is "<0.1.0"; "^0.0.3" is "<0.0.4".
            if (Specified <= 1)
                return new SemVer(1, 0, 0);

            if (Minor != 0 || Specified == 2)
                return new SemVer(0, Minor + 1, 0);

            return new SemVer(0, 0, Patch + 1);
        }

        /// <summary>Tilde: up to the next minor, except that a bare major stops at the next major.</summary>
        public SemVer TildeUpperBound()
            => Specified <= 1 ? new SemVer(Major + 1, 0, 0) : new SemVer(Major, Minor + 1, 0);
    }

    private readonly record struct Comparator(Op Op, SemVer Bound)
    {
        public bool Matches(SemVer version)
        {
            int result = version.CompareTo(Bound);
            return Op switch
            {
                Op.Eq => result == 0,
                Op.Gt => result > 0,
                Op.Gte => result >= 0,
                Op.Lt => result < 0,
                Op.Lte => result <= 0,
                _ => false,
            };
        }
    }
}

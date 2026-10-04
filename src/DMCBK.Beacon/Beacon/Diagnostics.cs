namespace DMCBK.Core.Beacon;

/// <summary>
/// Lint-consumable diagnostic helpers for safety data: stable <c>file:line:col</c> locations, source excerpts with carets, enriched catch values, and truncated scope dumps.
/// <see cref="BeaconErrorRenderer"/> (in <c>BeaconErrors.cs</c>) composes these into the single Elm-style view; both lint frontends consume the same primitives so interactive and headless output can never disagree.
/// </summary>
public static class BeaconDiagnostics
{
    /// <summary>Renders <c>file:line:col</c> from the span's user-text origin (through desugar chains).</summary>
    public static string FormatLocation(SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        SourceSpan origin = span.Origin;
        return $"{origin.File}:{origin.Line}:{origin.Column}";
    }

    /// <summary>
    /// Extracts the source line plus a caret line for <paramref name="span"/> (user-text origin).
    /// Returns false (empty strings) when <paramref name="source"/> is null or the line is missing.
    /// </summary>
    public static bool TryExcerpt(string? source, SourceSpan span, out string excerpt, out string caret)
    {
        ArgumentNullException.ThrowIfNull(span);
        excerpt = string.Empty;
        caret = string.Empty;
        if (source is null)
            return false;

        SourceSpan origin = span.Origin;
        string[] lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (origin.Line < 1 || origin.Line > lines.Length)
            return false;

        string text = lines[origin.Line - 1].TrimEnd('\r');
        excerpt = "  " + text;
        int column = Math.Max(1, origin.Column);
        int marker = Math.Max(1, Math.Min(origin.Length, 32));
        caret = "  " + new string(' ', column - 1) + "^" + new string('~', marker - 1);
        return true;
    }

    /// <summary>Converts a runtime failure to its lint diagnostic (severity error, code preserved).</summary>
    public static BeaconDiagnostic ToDiagnostic(this BeaconRuntimeException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new BeaconDiagnostic(ex.Code, BeaconSeverity.Error, ex.Message, ex.Span, ex.Suggestion);
    }

    /// <summary>
    /// Converts a runtime failure to its catch value: a map with <c>message</c> text, plus <c>code</c> text and <c>line</c> number for safety triage (<c>err.code</c>, <c>err.line</c>).
    /// Additive keys only; <c>message</c> is untouched.
    /// </summary>
    public static BeaconValue ToCatchValue(this BeaconRuntimeException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["message"] = BeaconValue.Text(ex.Message),
            ["code"] = BeaconValue.Text(ex.Code),
            ["line"] = BeaconValue.Number(ex.Span.Origin.Line),
        });
    }

    /// <summary>
    /// Summarizes a scope for abort dumps: up to <paramref name="maxEntries"/> visible names as <c>name=value</c>, each value truncated to <paramref name="maxValueChars"/> display chars.
    /// Never throws and never carries secrets: values render through display text only.
    /// </summary>
    public static string SummarizeScope(BeaconScope? scope, int maxEntries = 8, int maxValueChars = 80)
    {
        if (scope is null)
            return "(no locals)";

        List<string> parts = [];
        foreach (string name in scope.VisibleNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (parts.Count >= maxEntries)
            {
                parts.Add("...");
                break;
            }

            if (scope.TryLookup(name, out BeaconValue? value) && value is not null)
                parts.Add($"{name}={TruncateValue(BeaconInterpreter.ToDisplayText(value), maxValueChars)}");
        }

        return parts.Count == 0 ? "(no locals)" : string.Join("; ", parts);
    }

    /// <summary>Truncates display text with an ellipsis marker; pure and total.</summary>
    public static string TruncateValue(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars < 0)
            return string.Empty;

        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }

    /// <summary>
    /// Formats a call stack for abort dumps, innermost first.
    /// Empty stacks render explicitly so dumps never end in silence.
    /// </summary>
    public static string FormatCallStack(IReadOnlyList<string> stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (stack.Count == 0)
            return "(no active calls)";

        return string.Join(" <- ", stack.Reverse().Select(frame => frame + "()"));
    }
}

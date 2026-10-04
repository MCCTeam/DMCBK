namespace DMCBK.Core.Beacon;

/// <summary>
/// One formatted file: whether the source changed, the formatted text, a preview diff, and whether lint errors remain afterwards (formatting is layout, not correctness, so a file can be perfectly formatted and still fail lint).
/// </summary>
/// <param name="Changed">True when the formatted text differs from the input.</param>
/// <param name="Formatted">The formatted source.</param>
/// <param name="Diff">The preview diff (empty when nothing changed).</param>
/// <param name="HadErrors">True when lint errors remain in the formatted source.</param>
public sealed record BeaconFormatResult(bool Changed, string Formatted, string Diff, bool HadErrors);

/// <summary>
/// The full-file formatter: the <see cref="BeaconFix"/> safe subset (bare <c>end</c> label completion, the forgiven <c>cancel event</c> spelling, two-space indent normalization, pasted smart-quote normalization) plus line hygiene no parse needs (trailing whitespace trim, CRLF to LF, exactly one trailing newline).
/// Meaning never moves: the structural pass is the same preview <c>lint --fix</c> applies with its re-lint guard, and the hygiene pass only touches trivia the lexer already ignores.
/// Formatting twice is a no-op: the output is a fixed point.
/// </summary>
public static class BeaconFormat
{
    /// <summary>
    /// Formats <paramref name="source"/> in memory without touching disk.
    /// Never throws on script content: an unparseable source still gets the hygiene pass (trailing whitespace, line endings, final newline) and reports its lint errors.
    /// </summary>
    public static BeaconFormatResult FormatSource(string fileName, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);

        string[] before = SplitLines(source);
        string working = source;
        BeaconFixPreview preview;
        try
        {
            preview = BeaconFix.Preview(fileName, working);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            preview = EmptyPreview(working);
        }

        if (preview.HasFixes)
            working = preview.FixedSource;

        string[] lines = SplitLines(working);
        var cleaned = new List<string>(lines.Length);
        foreach (string line in lines)
            cleaned.Add(line.TrimEnd());

        while (cleaned.Count > 0 && cleaned[^1].Length == 0)
            cleaned.RemoveAt(cleaned.Count - 1);

        working = cleaned.Count == 0 ? string.Empty : string.Join("\n", cleaned) + "\n";

        bool changed = !string.Equals(working, source, StringComparison.Ordinal);

        string diff = changed ? RenderDiff(fileName, before, SplitLines(working)) : string.Empty;
        return new BeaconFormatResult(changed, working, diff, HasLintErrors(fileName, working));
    }

    private static BeaconFixPreview EmptyPreview(string source)
        => new(false, source, [], string.Empty);

    private static string[] SplitLines(string source)
        => source.Split(["\r\n", "\n"], StringSplitOptions.None);

    private static bool HasLintErrors(string fileName, string source)
    {
        try
        {
            BeaconLintReport report = BeaconLint.LintSource(fileName, source);
            return report.Diagnostics.Any(d => d.Severity == BeaconSeverity.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static string RenderDiff(string fileName, string[] before, string[] after)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- ").Append(fileName).Append('\n');
        sb.Append("+++ ").Append(fileName).Append(" (formatted)\n");
        int count = Math.Max(before.Length, after.Length);
        for (int i = 0; i < count; i++)
        {
            string? oldLine = i < before.Length ? before[i] : null;
            string? newLine = i < after.Length ? after[i] : null;
            if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
                continue;

            sb.Append("@@ ").Append(fileName).Append(':').Append(i + 1).Append(" @@\n");
            if (oldLine is not null)
                sb.Append("- ").Append(oldLine).Append('\n');

            if (newLine is not null)
                sb.Append("+ ").Append(newLine).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}

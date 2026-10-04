using System.Globalization;
using System.Text.RegularExpressions;

namespace DMCBK.Core.Beacon;

/// <summary>One manifest capability with the source range of its name.</summary>
public sealed record BeaconManifestEntry(string Capability, SourceSpan Span);

/// <summary>One declared per-script setting with its default and comment.</summary>
/// <param name="Name">The setting name.</param>
/// <param name="Default">The default value (text, number, or yes/no).</param>
/// <param name="Comment">The trailing comment, or empty.</param>
/// <param name="Span">The source range of the declaration.</param>
public sealed record BeaconSettingDecl(string Name, BeaconValue Default, string Comment, SourceSpan Span);

/// <summary>Parsed header plus manifest: version gate, needs/wants caplists, settings, and front-door diagnostics.</summary>
public sealed record BeaconHeaderResult(
    bool Ok,
    int Major,
    IReadOnlyList<BeaconManifestEntry> Needs,
    IReadOnlyList<BeaconManifestEntry> Wants,
    IReadOnlyList<BeaconSettingDecl> Settings,
    IReadOnlyList<BeaconDiagnostic> Diagnostics);

/// <summary>
/// The Beacon front door: mandatory <c># beacon MAJOR</c> first line (digits only, fail-closed) plus <c># needs:</c> / <c># wants:</c> caplists.
/// A manifest line after the first declaration is an ordinary comment plus a too-late warning; malformed caplists are parse errors.
/// </summary>
public static class BeaconHeader
{
    private static readonly Regex HeaderPattern = new(
        @"^\s*#\s*beacon\s+(?<major>\S+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ManifestPattern = new(
        @"^\s*#\s*(?<kind>needs|wants)\s*:\s*(?<caps>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SettingPattern = new(
        @"^\s*#\s*setting\s+(?<name>[A-Za-z][A-Za-z0-9_]*)\s*=\s*(?<value>.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SettingNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CapabilityPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses the header and manifest of <paramref name="source"/>.
    /// <paramref name="firstCodeLine"/> is the 1-based line of the first lexed token, if any; manifest lines on or after it are flagged too late and ignored.
    /// <paramref name="comments"/> are the lexer's recorded line comments; when given, manifest candidates come from them, so trailing manifests count and <c>#</c> inside strings is safe.
    /// </summary>
    public static BeaconHeaderResult Parse(
        string fileName,
        string source,
        int? firstCodeLine = null,
        IReadOnlyList<LexedComment>? comments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);

        string[] lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
        string firstLine = lines.Length == 0 ? string.Empty : lines[0].TrimEnd('\r');
        var headerSpan = new SourceSpan(fileName, 1, 1, firstLine.Length);

        Match header = HeaderPattern.Match(firstLine);
        if (!header.Success)
        {
            return Fail(0, new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"Missing mandatory '# beacon {BeaconEngine.SupportedBeaconMajor}' header as the first line.",
                headerSpan,
                $"Add '# beacon {BeaconEngine.SupportedBeaconMajor}' as the very first line of {fileName}."));
        }

        string majorText = header.Groups["major"].Value;
        if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
        {
            return Fail(0, new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"Beacon major version must be digits only, got '{majorText}'.",
                headerSpan,
                $"Write the header as '# beacon {BeaconEngine.SupportedBeaconMajor}' (Appendix A: MAJOR is digits only)."));
        }

        if (major != BeaconEngine.SupportedBeaconMajor)
        {
            return Fail(major, new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"Unsupported Beacon major version {major}; this build reads major {BeaconEngine.SupportedBeaconMajor}.",
                headerSpan,
                major > BeaconEngine.SupportedBeaconMajor
                    ? $"Downgrade the script header to '# beacon {BeaconEngine.SupportedBeaconMajor}' or upgrade MCC past this build."
                    : $"Upgrade the script header to '# beacon {BeaconEngine.SupportedBeaconMajor}' (see the Beacon migrator notes)."));
        }

        var needs = new List<BeaconManifestEntry>();
        var wants = new List<BeaconManifestEntry>();
        var settings = new List<BeaconSettingDecl>();
        var diagnostics = new List<BeaconDiagnostic>();

        foreach (ManifestCandidate candidate in FindCandidates(lines, comments))
        {
            bool wantsKind = candidate.Kind.Equals("wants", StringComparison.OrdinalIgnoreCase);
            string kind = wantsKind ? "wants" : "needs";
            if (TryLateManifest(fileName, candidate.Line, candidate.Column, candidate.Length, firstCodeLine, diagnostics))
                continue;

            if (candidate.Caps.Length == 0)
            {
                diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"Manifest '# {kind}:' names no capabilities.",
                    new SourceSpan(fileName, candidate.Line, candidate.Column, candidate.Length),
                    $"Write '# {kind}: chat.send' (capability = ident (. ident)*)."));
                continue;
            }

            foreach (Match cap in Regex.Matches(candidate.Caps, @"\S+"))
            {
                if (cap.Value.StartsWith("#", StringComparison.Ordinal))
                    break;

                if (!CapabilityPattern.IsMatch(cap.Value))
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        $"Bad capability '{cap.Value}' in '# {kind}:'; use ident (. ident)* like 'chat.send'.",
                        new SourceSpan(fileName, candidate.Line, candidate.CapsColumn + cap.Index, cap.Length),
                        $"Write '# {kind}: chat.send'."));
                    continue;
                }

                var entry = new BeaconManifestEntry(
                    cap.Value,
                    new SourceSpan(fileName, candidate.Line, candidate.CapsColumn + cap.Index, cap.Length));
                if (wantsKind)
                    wants.Add(entry);
                else
                    needs.Add(entry);
            }
        }

        foreach (SettingCandidate candidate in FindSettingCandidates(lines, comments))
        {
            if (TryLateManifest(fileName, candidate.Line, candidate.Column, candidate.Length, firstCodeLine, diagnostics))
                continue;

            Match setting = SettingPattern.Match(candidate.Text);
            if (!setting.Success)
            {
                diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"Bad setting line '{candidate.Text.Trim()}'; settings read '# setting name = default'.",
                    new SourceSpan(fileName, candidate.Line, candidate.Column, candidate.Length),
                    "Write '# setting thirst = 5 ; seconds between sips'."));
                continue;
            }

            string name = setting.Groups["name"].Value;
            SplitSettingValue(setting.Groups["value"].Value, out string raw, out string comment);
            if (!TryParseSettingDefault(raw, out BeaconValue? defaultValue) || defaultValue is null)
            {
                diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"Setting '{name}' names no default; give it text, a number, or yes/no.",
                    new SourceSpan(fileName, candidate.Line, candidate.Column, candidate.Length),
                    $"Write '# setting {name} = 5'."));
                continue;
            }

            settings.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            settings.Add(new BeaconSettingDecl(
                name, defaultValue, comment,
                new SourceSpan(fileName, candidate.Line, candidate.Column, candidate.Length)));
        }

        return new BeaconHeaderResult(true, major, needs, wants, settings, diagnostics);

        BeaconHeaderResult Fail(int parsedMajor, BeaconDiagnostic diagnostic) =>
            new(false, parsedMajor, [], [], [], [diagnostic]);
    }

    private sealed record ManifestCandidate(
        int Line,
        int Column,
        int Length,
        string Kind,
        string Caps,
        int CapsColumn);

    private sealed record SettingCandidate(int Line, int Column, int Length, string Text);

    private static bool TryLateManifest(
        string fileName, int line, int column, int length, int? firstCodeLine, List<BeaconDiagnostic> diagnostics)
    {
        if (!firstCodeLine.HasValue || line < firstCodeLine.Value)
            return false;

        diagnostics.Add(new BeaconDiagnostic(
            BeaconDiagnosticCodes.LateManifest,
            BeaconSeverity.Warning,
            $"Manifest line after the first declaration is an ordinary comment; move it above line {firstCodeLine.Value}.",
            new SourceSpan(fileName, line, column, length),
            "Move this line above the first code line so the loader enforces it."));
        return true;
    }

    private static void SplitSettingValue(string text, out string raw, out string comment)
    {
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                inQuotes = !inQuotes;
            else if (c == ';' && !inQuotes)
            {
                raw = text[..i].Trim();
                comment = text[(i + 1)..].Trim();
                return;
            }
        }

        raw = text.Trim();
        comment = string.Empty;
    }

    private static bool TryParseSettingDefault(string raw, out BeaconValue? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string text = raw.Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            value = BeaconValue.Text(text[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\"));
            return true;
        }

        if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
        {
            value = BeaconValue.YesNo(true);
            return true;
        }

        if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
        {
            value = BeaconValue.YesNo(false);
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && !double.IsNaN(number) && !double.IsInfinity(number))
        {
            value = BeaconValue.Number(number);
            return true;
        }

        value = BeaconValue.Text(text);
        return true;
    }

    private static IEnumerable<SettingCandidate> FindSettingCandidates(
        string[] lines, IReadOnlyList<LexedComment>? comments)
    {
        if (comments is not null)
        {
            foreach (LexedComment comment in HashComments(comments))
            {
                if (IsSettingLine(comment.Text))
                {
                    yield return new SettingCandidate(
                        comment.Span.Line, comment.Span.Column, comment.Span.Length, comment.Text);
                }
            }

            yield break;
        }

        for (int index = 1; index < lines.Length; index++)
        {
            string lineText = lines[index].TrimEnd('\r');
            string stripped = lineText.TrimStart();
            if (!stripped.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (IsSettingLine(stripped))
            {
                int hash = lineText.IndexOf('#');
                yield return new SettingCandidate(index + 1, hash + 1, lineText.Length - hash, lineText[hash..]);
            }
        }
    }

    private static bool IsSettingLine(string commentText)
    {
        string rest = commentText.TrimStart('#', ' ', '\t');
        if (!rest.StartsWith("setting", StringComparison.OrdinalIgnoreCase))
            return false;

        return rest.Length == "setting".Length || char.IsWhiteSpace(rest["setting".Length]);
    }

    private static IEnumerable<LexedComment> HashComments(IReadOnlyList<LexedComment> comments)
    {
        foreach (LexedComment comment in comments)
        {
            if (comment.Text.StartsWith("#", StringComparison.Ordinal))
                yield return comment;
        }
    }

    private static IEnumerable<ManifestCandidate> FindCandidates(string[] lines, IReadOnlyList<LexedComment>? comments)
    {
        if (comments is not null)
        {
            foreach (LexedComment comment in HashComments(comments))
            {
                Match manifest = ManifestPattern.Match(comment.Text);
                if (!manifest.Success)
                    continue;

                yield return new ManifestCandidate(
                    comment.Span.Line,
                    comment.Span.Column,
                    comment.Span.Length,
                    manifest.Groups["kind"].Value,
                    manifest.Groups["caps"].Value.Trim(),
                    comment.Span.Column + manifest.Groups["caps"].Index);
            }

            yield break;
        }

        for (int index = 1; index < lines.Length; index++)
        {
            string lineText = lines[index].TrimEnd('\r');
            Match manifest = ManifestPattern.Match(lineText);
            if (!manifest.Success)
                continue;

            int hash = lineText.IndexOf('#');
            yield return new ManifestCandidate(
                index + 1,
                hash + 1,
                lineText.Length - hash,
                manifest.Groups["kind"].Value,
                manifest.Groups["caps"].Value.Trim(),
                manifest.Groups["caps"].Index + 1);
        }
    }
}

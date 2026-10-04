namespace DMCBK.Core.Beacon;

/// <summary>
/// Typed lint batch request with explicit file paths or inline source.
/// Both frontends (in-client <c>/scripts lint</c> and the headless <c>lint</c> one-shot) build this and run <see cref="BeaconLintBatch.Execute"/> so the exit-code contract and the diagnostics can never disagree.
/// </summary>
/// <param name="Files">Input files, in order.</param>
/// <param name="Format"><c>text</c> (Elm view) or <c>json</c> (agent-scriptable document).</param>
/// <param name="TargetLib">Flag builtins newer than this <c>beacon.lib</c> version; null targets current.</param>
/// <param name="Strict">Escalate unresolvable <c>extern</c>/<c>call</c> and missing providers to errors.</param>
/// <param name="Fix">Preview the safe fix subset, print the diff first, then apply it.</param>
/// <param name="UseStdin">Lint piped source instead of files.</param>
/// <param name="StdinName">File name the piped source lints under (spans, JSON <c>file</c>).</param>
public sealed record BeaconLintRequest(
    IReadOnlyList<string> Files,
    string Format,
    int? TargetLib,
    bool Strict,
    bool Fix,
    bool UseStdin,
    string? StdinName);

/// <summary>
/// The shared headless lint runner: offline execution over <see cref="BeaconLint"/>.
/// Console-free (no <c>System.Console</c>): callers render <c>stdout</c> and <c>stderr</c> themselves, which is what keeps the in-client and headless frontends structurally identical.
/// Exit codes: 0 clean (warnings allowed), 1 errors, 2 usage.
/// </summary>
public static class BeaconLintBatch
{


    /// <summary>
    /// Runs lint offline over files (or piped source with <c>UseStdin</c>).
    /// Returns the exit code (0 clean, 1 errors, 2 usage); human chatter goes to <paramref name="stderr"/>, the <c>--format json</c> document (or the text view) to <paramref name="stdout"/>.
    /// <c>--fix</c> prints each diff before rewriting, then lints the fixed files.
    /// </summary>
    public static int Execute(
        BeaconLintRequest args, string? stdinText, out string stdout, out string stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        stdout = string.Empty;
        stderr = string.Empty;

        var options = new BeaconLintOptions(Strict: args.Strict, TargetLib: args.TargetLib);
        var outText = new System.Text.StringBuilder();
        var errText = new System.Text.StringBuilder();

        if (args.UseStdin)
        {
            if (stdinText is null)
            {
                stderr = "lint: --stdin needs piped source text.";
                return BeaconLint.ExitCodes.Usage;
            }

            string name = (string.IsNullOrWhiteSpace(args.StdinName) ? "stdin.bcn" : args.StdinName);
            BeaconLintReport report = BeaconLint.LintSource(name, stdinText, options);
            var reports = new List<BeaconLintReport> { report };
            stdout = Render(reports, null, args.Format, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [name] = stdinText,
            });
            return report.Ok ? BeaconLint.ExitCodes.Clean : BeaconLint.ExitCodes.Errors;
        }

        if (args.Files.Count == 0)
        {
            stderr = "lint: no input files. Usage: lint <file...> [--format text|json] [--target-lib N] [--strict] [--fix] [--stdin] [--stdin-name <name>]";
            return BeaconLint.ExitCodes.Usage;
        }

        var fixRecords = new List<BeaconLintFixRecord>();
        if (args.Fix)
        {
            foreach (string path in args.Files)
            {
                string source;
                try
                {
                    source = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errText.Append($"lint: cannot read '{path}': {ex.Message}\n");
                    stdout = outText.ToString();
                    stderr = errText.ToString().TrimEnd('\n');
                    return BeaconLint.ExitCodes.Usage;
                }

                BeaconFixPreview preview = BeaconFix.Preview(path, source);
                if (preview.HasFixes)
                {
                    outText.Append(preview.Diff).Append('\n');
                    try
                    {
                        File.WriteAllText(path, preview.FixedSource);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errText.Append($"lint: cannot write fixed '{path}': {ex.Message}\n");
                        stdout = outText.ToString();
                        stderr = errText.ToString().TrimEnd('\n');
                        return BeaconLint.ExitCodes.Usage;
                    }

                    outText.Append($"applied {preview.Edits.Count} fix(es) to '{path}'\n");
                    fixRecords.Add(new BeaconLintFixRecord(path, true, preview.Diff));
                }
                else
                    fixRecords.Add(new BeaconLintFixRecord(path, false, string.Empty));
            }
        }

        IReadOnlyList<BeaconLintReport> linted;
        try
        {
            linted = BeaconLint.LintFiles(args.Files, options);
        }
        catch (FileNotFoundException ex)
        {
            errText.Append($"lint: {ex.Message}\n");
            stdout = outText.ToString();
            stderr = errText.ToString().TrimEnd('\n');
            return BeaconLint.ExitCodes.Usage;
        }

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in args.Files)
        {
            try
            {
                sources[path] = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already linted above; the report carries the failure.
                // Excerpts just omit.
            }
        }

        string rendered = Render(
            linted, args.Fix ? fixRecords : null, args.Format, sources);
        outText.Append(rendered);
        stdout = outText.ToString().TrimEnd('\n');
        stderr = errText.ToString().TrimEnd('\n');
        return linted.All(r => r.Ok) ? BeaconLint.ExitCodes.Clean : BeaconLint.ExitCodes.Errors;
    }

    private static string Render(
        IReadOnlyList<BeaconLintReport> reports,
        IReadOnlyList<BeaconLintFixRecord>? fixes,
        string format,
        IReadOnlyDictionary<string, string> sources)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return BeaconLint.ToJson(reports, fixes);

        var sb = new System.Text.StringBuilder();
        foreach (BeaconLintReport report in reports)
        {
            if (report.Diagnostics.Count == 0)
            {
                sb.Append(report.Path).Append(": ok\n");
                continue;
            }

            foreach (BeaconDiagnostic diagnostic in report.Diagnostics)
            {
                sources.TryGetValue(report.Path, out string? source);
                if (sources.Count == 1 && sources.Values.FirstOrDefault() is { } only
                    && !sources.ContainsKey(report.Path))
                    source = only;

                sb.Append(BeaconErrorRenderer.Render(diagnostic, source, includeTrace: true)).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }
}

using System.Text.RegularExpressions;
using DMCBK.Core.Beacon;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Conformance harness: walks <c>TestData/accept/*.mcc</c> expecting zero error diagnostics and <c>TestData/reject/*.mcc</c> expecting a named rejection.
/// A reject file declares its expected code in a <c># expect: Bxxxx</c> comment; without one, any error diagnostic counts as the rejection.
/// Every failure is reported as <c>file:line: message</c> so a red run points at the fixture, not the runner.
/// </summary>
internal sealed record ConformanceRunResult(
    bool Passed,
    int AcceptCount,
    int RejectCount,
    IReadOnlyList<string> Failures);

/// <summary>Owns the accept/reject directory walk; parsing stays in <see cref="BeaconEngine"/>.</summary>
internal static class ConformanceRunner
{
    private static readonly Regex ExpectPattern = new(
        @"^\s*#\s*expect:\s*(?<code>B\d{4})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Runs both directories through <paramref name="engine"/> and reports mismatches with file:line.</summary>
    public static ConformanceRunResult Run(BeaconEngine engine, string acceptDir, string rejectDir)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectDir);

        var failures = new List<string>();
        int acceptCount = 0;
        int rejectCount = 0;

        foreach (string file in OrderedScripts(acceptDir))
        {
            acceptCount++;
            string scriptId = "accept/" + Path.GetFileName(file);
            engine.LoadSource(scriptId, Path.GetFileName(file), File.ReadAllText(file));
            IReadOnlyList<BeaconDiagnostic> errors = Errors(engine.Lint(scriptId));
            if (errors.Count > 0)
            {
                BeaconDiagnostic first = errors[0];
                failures.Add($"{first.Span}: expected parse-ok but got {first.Code} {first.Message}");
            }
        }

        foreach (string file in OrderedScripts(rejectDir))
        {
            rejectCount++;
            string fileName = Path.GetFileName(file);
            string source = File.ReadAllText(file);
            string scriptId = "reject/" + fileName;
            engine.LoadSource(scriptId, fileName, source);
            IReadOnlyList<BeaconDiagnostic> errors = Errors(engine.Lint(scriptId));
            (string? expected, int expectLine) = ReadExpectedCode(source);
            if (expected is null)
            {
                if (errors.Count == 0)
                    failures.Add($"{fileName}:1: expected rejection but the file parsed clean.");
            }
            else if (!errors.Any(d => string.Equals(d.Code, expected, StringComparison.Ordinal)))
            {
                string got = errors.Count == 0 ? "clean" : string.Join(", ", errors.Select(d => d.Code));
                failures.Add($"{fileName}:{expectLine}: expected rejection {expected} but got {got}.");
            }
        }

        return new ConformanceRunResult(failures.Count == 0, acceptCount, rejectCount, failures);
    }

    private static IReadOnlyList<BeaconDiagnostic> Errors(IReadOnlyList<BeaconDiagnostic> diagnostics)
        => diagnostics.Where(d => d.Severity == BeaconSeverity.Error).ToList();

    private static IEnumerable<string> OrderedScripts(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.mcc", SearchOption.TopDirectoryOnly).OrderBy(f => f)
            : [];

    private static (string? Code, int Line) ReadExpectedCode(string source)
    {
        string[] lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = ExpectPattern.Match(lines[i]);
            if (match.Success)
                return (match.Groups["code"].Value.ToUpperInvariant(), i + 1);
        }

        return (null, 0);
    }
}

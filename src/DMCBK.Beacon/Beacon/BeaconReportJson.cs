namespace DMCBK.Core.Beacon;

/// <summary>
/// Shared JSON projection for headless lint/run reports.
/// Consolidates the two ToJson copies so diagnostic documents can never disagree between frontends.
/// </summary>
public static class BeaconReportJson
{
    /// <summary>Maps a severity to its JSON name.</summary>
    public static string SeverityName(BeaconSeverity severity) => severity switch
    {
        BeaconSeverity.Error => "error",
        BeaconSeverity.Warning => "warning",
        _ => "note",
    };

    /// <summary>Projects one diagnostic to its JSON object shape.</summary>
    public static object ToDiagnosticJson(BeaconDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        SourceSpan origin = diagnostic.Span.Origin;
        return new
        {
            code = diagnostic.Code,
            severity = SeverityName(diagnostic.Severity),
            file = origin.File,
            line = origin.Line,
            col = origin.Column,
            message = diagnostic.Message,
            suggestion = diagnostic.Suggestion,
        };
    }

    /// <summary>Tallies one diagnostic into the summary counters.</summary>
    public static void Tally(BeaconSeverity severity, ref int errors, ref int warnings, ref int notes)
    {
        switch (severity)
        {
            case BeaconSeverity.Error: errors++; break;
            case BeaconSeverity.Warning: warnings++; break;
            default: notes++; break;
        }
    }
}

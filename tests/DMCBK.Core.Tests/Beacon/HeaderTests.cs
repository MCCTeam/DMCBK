using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Header plus manifest gate: mandatory first-line header, digits-only major, fail-closed versions, needs/wants caplists, and the too-late manifest flag.
/// </summary>
public sealed class HeaderTests
{
    private static BeaconEngine NewEngine() => new(new BeaconTestHost());

    private static IReadOnlyList<BeaconDiagnostic> LintSource(string source)
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.mcc", source);
        return engine.Lint("probe");
    }

    private static IReadOnlyList<BeaconDiagnostic> LintErrors(string source)
        => LintSource(source).Where(d => d.Severity == BeaconSeverity.Error).ToList();

    [Fact]
    public void MissingHeader_FailsClosedWithParseCode()
    {
        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource("say \"no header\"\n");

        BeaconDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.Parse, diagnostic.Code);
        Assert.Equal(BeaconSeverity.Error, diagnostic.Severity);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void MinorVersion_IsRejectedPointingAtHeaderRule()
    {
        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource("# beacon 1.2\nsay \"hi\"\n");

        BeaconDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.Parse, diagnostic.Code);
        Assert.Contains("1.2", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void NewerMajor_FailsClosed()
    {
        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource("# beacon 2\nsay \"hi\"\n");

        BeaconDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.Parse, diagnostic.Code);
        Assert.Equal(BeaconSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void ValidHeader_LintsClean()
    {
        Assert.Empty(LintSource("# beacon 1\nsay \"hello\"\n"));
    }

    [Fact]
    public void Header_ToleratesSurroundingWhitespaceAndCase()
    {
        Assert.Empty(LintSource("  # BEACON 1  \nsay \"hi\"\n"));
    }

    [Fact]
    public void Manifest_NeedsAndWants_ParseWithCapabilities()
    {
        const string source = "# beacon 1\n# needs: chat.send inventory.read\n# wants: net.fetch\nsay \"hi\"\n";

        BeaconHeaderResult header = BeaconHeader.Parse("probe.mcc", source, firstCodeLine: 4);

        Assert.True(header.Ok);
        Assert.Empty(header.Diagnostics);
        Assert.Equal(["chat.send", "inventory.read"], header.Needs.Select(n => n.Capability).ToList());
        Assert.Equal(["net.fetch"], header.Wants.Select(w => w.Capability).ToList());
        Assert.Empty(LintSource(source));
    }

    [Fact]
    public void Manifest_Capability_SpansPointAtOwningLine()
    {
        BeaconHeaderResult header = BeaconHeader.Parse("probe.mcc", "# beacon 1\n# needs: chat.send\n", firstCodeLine: 3);

        BeaconManifestEntry entry = Assert.Single(header.Needs);
        Assert.Equal(2, entry.Span.Line);
        Assert.True(entry.Span.Column > 1);
    }

    [Fact]
    public void Manifest_AfterFirstDeclaration_IsLateWarningAndIgnored()
    {
        const string source = "# beacon 1\nsay \"hi\"\n# needs: chat.send\n";

        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == BeaconSeverity.Error);
        BeaconDiagnostic late = Assert.Single(diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.LateManifest, late.Code);
        Assert.Equal(BeaconSeverity.Warning, late.Severity);

        BeaconHeaderResult header = BeaconHeader.Parse("probe.mcc", source, firstCodeLine: 2);
        Assert.Empty(header.Needs);
    }

    [Fact]
    public void Manifest_TrailingSameLineComment_IsLate()
    {
        const string source = "# beacon 1\nsay \"hi\" # needs: chat.send\n";

        Assert.Contains(
            LintSource(source),
            d => string.Equals(d.Code, BeaconDiagnosticCodes.LateManifest, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# beacon 1\n# needs: chat..send\nsay \"hi\"\n")]
    [InlineData("# beacon 1\n# needs:\nsay \"hi\"\n")]
    [InlineData("# beacon 1\n# wants: 123bad\nsay \"hi\"\n")]
    public void Manifest_Malformed_Caplist_IsParseError(string source)
    {
        Assert.Contains(
            LintErrors(source),
            d => string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal));
    }

    [Fact]
    public void SlashNeedsLine_IsOrdinaryComment()
    {
        const string source = "# beacon 1\n// needs: chat.send\nsay \"hi\"\n";

        BeaconHeaderResult header = BeaconHeader.Parse("probe.mcc", source, firstCodeLine: 3);

        Assert.Empty(header.Needs);
        Assert.Empty(header.Wants);
        Assert.Empty(LintSource(source));
    }

    [Fact]
    public void Manifest_Entries_PreserveOrder()
    {
        BeaconHeaderResult header = BeaconHeader.Parse(
            "probe.mcc",
            "# beacon 1\n# needs: b.cap a.cap\n",
            firstCodeLine: 3);

        Assert.Equal(["b.cap", "a.cap"], header.Needs.Select(n => n.Capability).ToList());
    }

    [Theory]
    [InlineData("# beacon 1\nwait 1 millisecond\n")]
    [InlineData("# beacon 1\nwait 2 milliseconds\n")]
    [InlineData("# beacon 1\nwait 3 seconds\n")]
    [InlineData("# beacon 1\nwait 4 sec\n")]
    [InlineData("# beacon 1\nwait 5 s\n")]
    [InlineData("# beacon 1\nwait 6 minutes\n")]
    [InlineData("# beacon 1\nwait 7 min\n")]
    [InlineData("# beacon 1\nevery 60 seconds\nend every\n")]
    [InlineData("# beacon 1\nevery 5 minutes\nend every\n")]
    [InlineData("# beacon 1\nevery 2 hours\nend every\n")]
    [InlineData("# beacon 1\non tps cooldown 300 seconds named \"tps-warn\"\nend on\n")]
    [InlineData("# beacon 1\non tps cooldown 5 hours named \"tps-warn\"\nend on\n")]
    public void UnitMatrix_ValidForms_LintClean(string source)
    {
        Assert.Empty(LintErrors(source));
    }

    [Theory]
    [InlineData("# beacon 1\nwait 2 fortnights\n", "second")]
    [InlineData("# beacon 1\nwait 2 hours\n", "second")]
    [InlineData("# beacon 1\nevery 5 milliseconds\nend every\n", "hour")]
    [InlineData("# beacon 1\nwait 2\nsay \"hi\"\n", "minute")]
    public void UnitMatrix_BadUnit_NamesAllowedUnits(string source, string allowedWord)
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors(source);

        BeaconDiagnostic diagnostic = Assert.Single(errors);
        Assert.Equal(BeaconDiagnosticCodes.UnknownUnit, diagnostic.Code);
        Assert.Contains(allowedWord, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void EqualsAssignment_RejectedThroughLint()
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("# beacon 1\nset x = 5\n");

        BeaconDiagnostic diagnostic = Assert.Single(errors,
            d => string.Equals(d.Code, BeaconDiagnosticCodes.StrictEquals, StringComparison.Ordinal));
        Assert.Contains("set x to 5", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void LeadingSlash_RejectedThroughLint()
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("# beacon 1\n/home\n");

        BeaconDiagnostic diagnostic = Assert.Single(errors,
            d => string.Equals(d.Code, BeaconDiagnosticCodes.StrictLeadingSlash, StringComparison.Ordinal));
        Assert.Contains("server", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderFailure_HidesLaterLexicalErrors_FailClosed()
    {
        // No header: the only finding is the B0001 gate, even with an `=` below it.
        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource("set x = 5\n");

        BeaconDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.Parse, diagnostic.Code);
    }
}

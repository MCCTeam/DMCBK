using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Pins the conformance harness: empty sets run clean, the shipped accept/reject fixtures pass, and mismatches are reported with file:line.
/// The reject side proves the engine's header gate emits the registered <c>B0001</c> code.
/// </summary>
public sealed class ConformanceRunnerTests
{
    private static string TestDataDir(string set)
        => Path.Combine(AppContext.BaseDirectory, "Beacon", "TestData", set);

    private static BeaconEngine NewEngine() => new(new BeaconTestHost());

    [Fact]
    public void EmptySets_RunCleanAndRejectNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-conformance", Guid.NewGuid().ToString("N"));
        string accept = Path.Combine(root, "accept");
        string reject = Path.Combine(root, "reject");
        Directory.CreateDirectory(accept);
        Directory.CreateDirectory(reject);

        ConformanceRunResult result = ConformanceRunner.Run(NewEngine(), accept, reject);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.AcceptCount);
        Assert.Equal(0, result.RejectCount);
    }

    [Fact]
    public void ShippedFixtures_RunClean()
    {
        ConformanceRunResult result = ConformanceRunner.Run(
            NewEngine(), TestDataDir("accept"), TestDataDir("reject"));

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
        Assert.True(result.AcceptCount >= 1, "Expected at least one accept fixture.");
        Assert.True(result.RejectCount >= 1, "Expected at least one reject fixture.");
    }

    [Fact]
    public void AcceptMismatch_IsReportedWithFileAndLine()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-conformance", Guid.NewGuid().ToString("N"));
        string accept = Path.Combine(root, "accept");
        string reject = Path.Combine(root, "reject");
        Directory.CreateDirectory(accept);
        Directory.CreateDirectory(reject);
        File.WriteAllText(Path.Combine(accept, "headerless.mcc"), "say \"no header\"\n");

        ConformanceRunResult result = ConformanceRunner.Run(NewEngine(), accept, reject);

        Assert.False(result.Passed);
        string failure = Assert.Single(result.Failures);
        Assert.Contains("headerless.mcc:1:1", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_MissingHeader_ReportsRegisteredParseCode()
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.mcc", "say \"no header\"\n");

        BeaconDiagnostic diagnostic = Assert.Single(engine.Lint("probe"));

        Assert.Equal(BeaconDiagnosticCodes.Parse, diagnostic.Code);
        Assert.Equal(BeaconSeverity.Error, diagnostic.Severity);
        Assert.Contains(
            BeaconDiagnosticCodes.All,
            d => string.Equals(d.Code, diagnostic.Code, StringComparison.Ordinal));
        Assert.NotNull(diagnostic.Suggestion);
    }
}

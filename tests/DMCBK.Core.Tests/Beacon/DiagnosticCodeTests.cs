using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Pins the stable-code contract shared by in-client and headless lint.
/// Every registered code matches <c>^B\d{4}$</c>, codes are unique, and all four families (parse, manifests, names/events, strictness) are represented, so new codes only add, never reshape the scheme.
/// </summary>
public sealed class DiagnosticCodeTests
{
    [Fact]
    public void AllCodes_MatchBeaconFormat()
    {
        foreach (BeaconDiagnosticDescriptor descriptor in BeaconDiagnosticCodes.All)
            Assert.Matches("^B\\d{4}$", descriptor.Code);
    }

    [Fact]
    public void AllCodes_AreUnique()
    {
        IReadOnlyList<string> codes = BeaconDiagnosticCodes.All.Select(d => d.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Registry_CoversAllFourFamilies()
    {
        IReadOnlyList<string> codes = BeaconDiagnosticCodes.All.Select(d => d.Code).ToList();
        Assert.Contains(BeaconDiagnosticCodes.Parse, codes);
        Assert.Contains(codes, c => c.StartsWith("B1", StringComparison.Ordinal));
        Assert.Contains(codes, c => c.StartsWith("B2", StringComparison.Ordinal));
        Assert.Contains(codes, c => c.StartsWith("B3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BeaconDiagnosticCodes.Parse, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.UnclosedBlockComment, BeaconSeverity.Warning)]
    [InlineData(BeaconDiagnosticCodes.PasteNormalization, BeaconSeverity.Note)]
    [InlineData(BeaconDiagnosticCodes.ManifestNeedsMismatch, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.ManifestWantsUnavailable, BeaconSeverity.Warning)]
    [InlineData(BeaconDiagnosticCodes.LateManifest, BeaconSeverity.Warning)]
    [InlineData(BeaconDiagnosticCodes.UnknownEvent, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.UnknownName, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.StrictBooleanCondition, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.StrictMixedOperands, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.StrictEquals, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.StrictLeadingSlash, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.UnknownUnit, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.BudgetExhausted, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.ChatThrottled, BeaconSeverity.Warning)]
    [InlineData(BeaconDiagnosticCodes.FileJail, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.NetGate, BeaconSeverity.Error)]
    [InlineData(BeaconDiagnosticCodes.ReadBounds, BeaconSeverity.Error)]
    public void KnownCode_CarriesItsDefaultSeverity(string code, BeaconSeverity severity)
    {
        BeaconDiagnosticDescriptor descriptor = Assert.Single(
            BeaconDiagnosticCodes.All,
            d => string.Equals(d.Code, code, StringComparison.Ordinal));
        Assert.Equal(severity, descriptor.DefaultSeverity);
    }
}

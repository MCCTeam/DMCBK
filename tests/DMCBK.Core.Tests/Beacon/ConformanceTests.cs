using System.Text.RegularExpressions;
using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Conformance: the shipped accept and reject suite stays green with the one expected shop_buy warning.
/// Every full <c># beacon 1</c> documented snippet parses so docs cannot drift from the parser without a red test.
/// </summary>
public sealed class ConformanceTests
{
    private static string TestDataDir(string set)
        => Path.Combine(AppContext.BaseDirectory, "Beacon", "TestData", set);

    private static BeaconEngine NewEngine() => new(new BeaconTestHost());

    private static IReadOnlyList<BeaconDiagnostic> LintSource(string fileName, string source)
    {
        var engine = NewEngine();
        engine.LoadSource("probe", fileName, source);
        return engine.Lint("probe");
    }

    [Fact]
    public void ShippedFixtures_CoverAllShapes_AndStayGreen()
    {
        ConformanceRunResult result = ConformanceRunner.Run(
            NewEngine(), TestDataDir("accept"), TestDataDir("reject"));

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
        Assert.True(result.AcceptCount >= 25, $"Expected at least 25 accept fixtures, got {result.AcceptCount}.");
        Assert.True(result.RejectCount >= 15, $"Expected at least 15 reject fixtures, got {result.RejectCount}.");
    }

    [Fact]
    public void ShopBuy_UnknownHook_IsSingleWarning_NotError()
    {
        string file = Path.Combine(TestDataDir("accept"), "sdk_shopbuy.bcn");
        Assert.True(File.Exists(file), "Expected sdk_shopbuy.bcn accept fixture.");
        string source = File.ReadAllText(file);

        IReadOnlyList<BeaconDiagnostic> diagnostics = LintSource("sdk_shopbuy.bcn", source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == BeaconSeverity.Error);
        BeaconDiagnostic warning = Assert.Single(
            diagnostics, d => d.Severity == BeaconSeverity.Warning);
        Assert.Equal(BeaconDiagnosticCodes.UnknownEvent, warning.Code);
        Assert.Contains("shop_buy", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalSnippets_WithBeaconHeader_ParseWithoutErrors()
    {
        string proposal = FindProposal();
        string text = File.ReadAllText(proposal);
        var blocks = Regex.Matches(text, "```(?:[a-zA-Z]*)\r?\n(?<body>.*?)```", RegexOptions.Singleline);
        var failures = new List<string>();
        int fullScripts = 0;

        int index = 0;
        foreach (Match block in blocks)
        {
            index++;
            string body = block.Groups["body"].Value;
            string[] lines = body.Split(["\r\n", "\n"], StringSplitOptions.None);
            string? firstCode = lines.FirstOrDefault(l => l.Trim().Length > 0);
            if (firstCode is null || !Regex.IsMatch(firstCode, @"^\s*#\s*beacon\s+\d", RegexOptions.IgnoreCase))
                continue;

            fullScripts++;
            IReadOnlyList<BeaconDiagnostic> errors = LintSource($"proposal-{index}.bcn", body)
                .Where(d => d.Severity == BeaconSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                BeaconDiagnostic first = errors[0];
                failures.Add($"proposal block {index}: expected parse-ok but got {first.Code} {first.Message} at {first.Span}");
            }
        }

        Assert.True(fullScripts >= 9, $"Expected at least 9 full proposal scripts, found {fullScripts}.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindProposal()
    {
        return Path.Combine(AppContext.BaseDirectory, "Beacon", "TestData", "beacon-script-proposal.md");
    }
}

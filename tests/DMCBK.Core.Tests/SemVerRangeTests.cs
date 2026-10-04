using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The npm-syntax range parser.
/// The cases below are the ones a manifest actually writes plus the boundaries that decide whether a plugin loads: the caret's "next left-most non-zero component" rule, the tilde's next-minor rule, partial versions, AND by space, OR by <c>||</c>, and the rule that a prerelease is compared by its core triple alone.
/// </summary>
public sealed class SemVerRangeTests
{
    [Theory]
    [InlineData("*", "0.0.1", true)]
    [InlineData("*", "99.99.99", true)]
    [InlineData("", "1.2.3", true)]
    [InlineData("x", "1.2.3", true)]

    // Exact.
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.3.0", false)]

    // Partial versions behave as the x-range they abbreviate.
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("1.2", "1.2.9", true)]
    [InlineData("1.2", "1.3.0", false)]
    [InlineData("1", "1.9.9", true)]
    [InlineData("1", "2.0.0", false)]
    [InlineData("1.2.x", "1.2.7", true)]
    [InlineData("1.2.x", "1.3.0", false)]
    [InlineData("1.x", "1.7.0", true)]

    // Comparators.
    [InlineData(">=2.0.0", "2.0.0", true)]
    [InlineData(">=2.0.0", "1.9.9", false)]
    [InlineData(">2.0.0", "2.0.0", false)]
    [InlineData(">2.0.0", "2.0.1", true)]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData(">=0.9", "0.9.0", true)]
    [InlineData(">=0.9", "0.8.9", false)]
    [InlineData(">=0.9", "1.4.0", true)]

    // Caret: up to the next left-most non-zero component.
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("^1.2", "1.9.9", true)]
    [InlineData("^1.2", "2.0.0", false)]
    [InlineData("^0", "0.9.0", true)]
    [InlineData("^0", "1.0.0", false)]

    // Tilde: up to the next minor.
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1.2.3", "1.2.2", false)]
    [InlineData("~1.2", "1.2.9", true)]
    [InlineData("~1.2", "1.3.0", false)]
    [InlineData("~1", "1.9.0", true)]
    [InlineData("~1", "2.0.0", false)]

    // AND by space, OR by ||.
    [InlineData(">=1.4 <2", "1.4.0", true)]
    [InlineData(">=1.4 <2", "1.9.9", true)]
    [InlineData(">=1.4 <2", "2.0.0", false)]
    [InlineData(">=1.4 <2", "1.3.9", false)]
    [InlineData("^1.0.0 || ^2.0.0", "1.5.0", true)]
    [InlineData("^1.0.0 || ^2.0.0", "2.5.0", true)]
    [InlineData("^1.0.0 || ^2.0.0", "3.0.0", false)]
    public void Satisfies(string range, string version, bool expected)
    {
        Assert.True(SemVerRange.TryParse(range, out SemVerRange? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Satisfies(version));
    }

    [Fact]
    public void PrereleaseIsOrderedAndBuildMetadataDoesNotChangeOrdering()
    {
        // Prereleases compare below their stable version; build metadata does not affect precedence.
        SemVerRange range = SemVerRange.Parse(">=2.0.0");

        Assert.False(range.Satisfies("2.0.0-dev+e893691e"));
        Assert.True(range.Satisfies("2.0.0+e893691e"));
        Assert.False(range.Satisfies("1.9.9-dev"));
    }

    [Fact]
    public void RangeCarryingAPrereleaseTag_ComparesByCore()
    {
        Assert.True(SemVerRange.Parse(">=2.0.0-alpha").Satisfies("2.0.0"));
        Assert.True(SemVerRange.Parse("^2.0.0-alpha").Satisfies("2.4.0"));
    }

    [Theory]
    [InlineData("not-a-range")]
    [InlineData(">=")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("@1.2.3")]
    [InlineData(">=1.0 <>2.0")]
    public void Rejects_Malformed(string range)
    {
        Assert.False(SemVerRange.TryParse(range, out SemVerRange? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_Throws_OnMalformed()
        => Assert.Throws<FormatException>(() => SemVerRange.Parse("not-a-range"));

    [Fact]
    public void Any_And_Text_AreReported()
    {
        Assert.True(SemVerRange.Parse("*").IsAny);
        Assert.True(SemVerRange.Parse("").IsAny);
        Assert.False(SemVerRange.Parse(">=1.0.0").IsAny);
        Assert.Equal(">=1.0.0", SemVerRange.Parse(">=1.0.0").Text);
    }

    [Fact]
    public void UnparsableVersion_NeverSatisfies()
        => Assert.False(SemVerRange.Parse(">=1.0.0").Satisfies("who knows"));
}

using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The semver parser the manifest's <c>mcc</c>, <c>umpk</c> and plugin dependency gates all read through.
/// Two behaviours matter beyond plain parsing: full semver precedence for ordering, and a separate core-triple comparison that range checks use so a local <c>2.0.0-dev+sha</c> build is not treated as older than the release it is.
/// </summary>
public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.0.1", 0, 0, 1)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("2.1", 2, 1, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("  1.2.3  ", 1, 2, 3)]
    public void Parses_CoreTriple(string text, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(text, out SemVer version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Fact]
    public void Parses_PrereleaseAndBuild()
    {
        Assert.True(SemVer.TryParse("2.0.0-dev.3+e893691e", out SemVer version));
        Assert.Equal("dev.3", version.Prerelease);
        Assert.Equal("e893691e", version.Build);
        Assert.True(version.IsPrerelease);
        Assert.Equal("2.0.0", version.Core);
        Assert.Equal("2.0.0-dev.3+e893691e", version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-al pha")]
    [InlineData("1.2.3+")]
    public void Rejects_Malformed(string? text)
        => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Parse_Throws_OnMalformed()
        => Assert.Throws<FormatException>(() => SemVer.Parse("not a version"));

    [Fact]
    public void Ordering_FollowsSemverPrecedence()
    {
        SemVer[] ascending =
        [
            SemVer.Parse("1.0.0-alpha"),
            SemVer.Parse("1.0.0-alpha.1"),
            SemVer.Parse("1.0.0-alpha.beta"),
            SemVer.Parse("1.0.0-beta"),
            SemVer.Parse("1.0.0-beta.2"),
            SemVer.Parse("1.0.0-beta.11"),
            SemVer.Parse("1.0.0-rc.1"),
            SemVer.Parse("1.0.0"),
            SemVer.Parse("1.0.1"),
            SemVer.Parse("1.1.0"),
            SemVer.Parse("2.0.0"),
        ];

        for (int i = 1; i < ascending.Length; i++)
            Assert.True(ascending[i - 1] < ascending[i], $"{ascending[i - 1]} should sort below {ascending[i]}");
    }

    [Fact]
    public void BuildMetadata_IsIgnored_ByComparison()
    {
        SemVer left = SemVer.Parse("2.0.0+aaaa");
        SemVer right = SemVer.Parse("2.0.0+bbbb");

        Assert.Equal(left, right);
        Assert.Equal(0, left.CompareTo(right));
    }

    [Fact]
    public void CompareCore_IgnoresPrerelease()
    {
        // A dev build of 2.0.0 IS 2.0.0 as far as any range is concerned, even though full precedence sorts it below the release.
        SemVer dev = SemVer.Parse("2.0.0-dev+sha");
        SemVer release = SemVer.Parse("2.0.0");

        Assert.Equal(0, dev.CompareCore(release));
        Assert.True(dev < release);
    }
}

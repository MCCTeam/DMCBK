using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>api-version</c> rule: same major, host minor at least the declared one, a bare major read as minor 0.
/// The cases are written against the constants rather than the literal 2.1, so a later minor bump does not need this file edited to keep telling the truth.
/// </summary>
public sealed class PluginApiVersionTests
{
    [Fact]
    public void Current_IsMajorDotMinor()
        => Assert.Equal($"{PluginApiVersion.Major}.{PluginApiVersion.Minor}", PluginApiVersion.Current);

    [Fact]
    public void Current_IsCompatibleWithItself()
        => Assert.True(PluginApiVersion.IsCompatible(PluginApiVersion.Current));

    [Fact]
    public void BareMajor_IsReadAsMinorZero()
    {
        Assert.True(PluginApiVersion.TryParse(PluginApiVersion.Major.ToString(), out int major, out int minor));
        Assert.Equal(PluginApiVersion.Major, major);
        Assert.Equal(0, minor);

        // Which is the point of the rule: every 2.0-era manifest keeps loading on a 2.1 host.
        Assert.True(PluginApiVersion.IsCompatible(PluginApiVersion.Major.ToString()));
    }

    [Fact]
    public void EarlierMinor_Loads_LaterMinor_IsRefused()
    {
        Assert.True(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major}.{PluginApiVersion.Minor}"));
        Assert.True(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major}.0"));
        Assert.False(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major}.{PluginApiVersion.Minor + 1}"));
    }

    [Fact]
    public void DifferentMajor_IsRefused()
    {
        Assert.False(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major - 1}.0"));
        Assert.False(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major + 1}.0"));

        // Even a lower minor of a higher major: a major bump is a breaking change, not a superset.
        Assert.False(PluginApiVersion.IsCompatible($"{PluginApiVersion.Major + 1}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two")]
    [InlineData("2.x")]
    public void Unparsable_IsRefused(string? declared)
    {
        Assert.False(PluginApiVersion.TryParse(declared, out _, out _));
        Assert.False(PluginApiVersion.IsCompatible(declared));
    }

    [Fact]
    public void TryParseMajor_StillWorks_ForCallersThatOnlyWantTheMajor()
    {
        Assert.True(PluginApiVersion.TryParseMajor("2.7", out int major));
        Assert.Equal(2, major);
    }
}

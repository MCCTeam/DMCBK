using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Pins <see cref="BookLimits.ForProtocol"/> against legacy's per-era table (MinecraftClient/Inventory/BookContent.cs:17-37): three protocol bands (pre-1.17, 1.17 through 1.21.1, 1.21.2+), the exact boundary protocols, and the flat 100-page cap that holds across every band.
/// </summary>
public sealed class BookLimitsTests
{
    // Protocol 340 = 1.12.2 (below the 1.17 band).
    [Fact]
    public void BelowMc1_17_UsesTheLegacyBudget()
    {
        BookLimits limits = BookLimits.ForProtocol(340);
        Assert.Equal(100, limits.MaxPages);
        Assert.Equal(32767, limits.MaxPageLength);
        Assert.Equal(16, limits.MaxTitleLength);
    }

    // Protocol 755 = 1.17, the first protocol in the raised-cap band.
    [Fact]
    public void AtMc1_17Boundary_UsesTheRaisedBudget()
    {
        BookLimits limits = BookLimits.ForProtocol(755);
        Assert.Equal(100, limits.MaxPages);
        Assert.Equal(8192, limits.MaxPageLength);
        Assert.Equal(128, limits.MaxTitleLength);
    }

    // Protocol 764 = 1.20.2, comfortably inside the 1.17-1.21.1 band.
    [Fact]
    public void BetweenMc1_17AndMc1_21_2_StaysOnTheRaisedBudget()
    {
        BookLimits limits = BookLimits.ForProtocol(764);
        Assert.Equal(8192, limits.MaxPageLength);
        Assert.Equal(128, limits.MaxTitleLength);
    }

    // Protocol 768 = 1.21.2, the first protocol in the tightened structured-component band.
    [Fact]
    public void AtMc1_21_2Boundary_UsesTheTightenedBudget()
    {
        BookLimits limits = BookLimits.ForProtocol(768);
        Assert.Equal(100, limits.MaxPages);
        Assert.Equal(1024, limits.MaxPageLength);
        Assert.Equal(32, limits.MaxTitleLength);
    }

    // Protocol 771 = 1.21.5, above the 1.21.2 boundary; the tightened budget still applies.
    [Fact]
    public void AboveMc1_21_2_StaysOnTheTightenedBudget()
    {
        BookLimits limits = BookLimits.ForProtocol(771);
        Assert.Equal(1024, limits.MaxPageLength);
        Assert.Equal(32, limits.MaxTitleLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(340)]
    [InlineData(755)]
    [InlineData(768)]
    [InlineData(771)]
    public void MaxPages_IsFlatAcrossEveryBand(int protocolVersion)
        => Assert.Equal(100, BookLimits.ForProtocol(protocolVersion).MaxPages);
}

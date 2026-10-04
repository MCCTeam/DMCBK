namespace DMCBK.Core;

/// <summary>
/// Server-enforced written/writable book limits by protocol era: page count, per-page character length, and title length.
/// Ported from legacy's <c>MinecraftClient.Inventory.BookLimits.ForProtocol</c> (MinecraftClient/Inventory/BookContent.cs:17-37): 1.17 raised the page/title caps considerably (the pre-1.17 wire used a much smaller NBT string budget), and 1.21.2's structured-component rewrite tightened them back down.
/// <see cref="MaxPages"/> is a flat 100 across every era; only the per-page and title lengths vary.
/// </summary>
/// <param name="MaxPages">The maximum number of pages a book can hold.</param>
/// <param name="MaxPageLength">The maximum character length of a single page.</param>
/// <param name="MaxTitleLength">The maximum character length of a signed book's title.</param>
public sealed record BookLimits(int MaxPages, int MaxPageLength, int MaxTitleLength)
{
    // Protocol numbers ported from legacy's Protocol18Handler.MC_1_17_Version / MC_1_21_2_Version.
    private const int Mc1_17Protocol = 755;
    private const int Mc1_21_2Protocol = 768;

    /// <summary>Resolves the book limits a server speaking <paramref name="protocolVersion"/> enforces.</summary>
    public static BookLimits ForProtocol(int protocolVersion)
    {
        int maxPageLength = protocolVersion switch
        {
            >= Mc1_21_2Protocol => 1024,
            >= Mc1_17Protocol => 8192,
            _ => 32767,
        };

        int maxTitleLength = protocolVersion switch
        {
            >= Mc1_21_2Protocol => 32,
            >= Mc1_17Protocol => 128,
            _ => 16,
        };

        return new BookLimits(100, maxPageLength, maxTitleLength);
    }
}

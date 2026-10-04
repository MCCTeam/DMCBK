using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Localization;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The advancement frame carried end to end into the <c>achievement</c> listing.
/// Vanilla writes the frame as the <c>AdvancementType</c> ordinal (task = 0, challenge = 1, goal = 2, <c>advancements/AdvancementType.java:10-13</c>) inside the optional display block, and omits that block entirely for generated recipe advancements, so "no frame" is a real state and must not read as Task.
/// Checked against the shipped 1.21.11 data pack: of its 1584 advancements, 125 carry a display block (90 task, 25 challenge, 10 goal) and 1459 carry none.
/// </summary>
public sealed class AchievementFrameTests
{
    [Theory]
    [InlineData(0, "Task")]
    [InlineData(1, "Challenge")]
    [InlineData(2, "Goal")]
    public void FrameName_UsesTheLegacyWords(int frame, string expected)
        => Assert.Equal(expected, AchievementCommand.FrameName(frame));

    /// <summary>
    /// A non-displayed advancement (every generated recipe one) has no frame, and the listing must not invent Task for it.
    /// Legacy did exactly that, defaulting the type before reading the display flag (<c>Protocol18.cs:3600</c>), which made every recipe line claim a frame the server never sent.
    /// </summary>
    [Fact]
    public void FrameName_IsEmptyWhenTheServerSentNoFrame()
        => Assert.Equal(string.Empty, AchievementCommand.FrameName(null));

    /// <summary>
    /// A frame number outside vanilla's three prints as the number.
    /// A future or modded value is still a fact the server sent; naming it would be a guess, and swallowing it would hide it.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(42)]
    public void FrameName_KeepsAnUnknownFrameAsItsNumber(int frame)
        => Assert.Equal(frame.ToString(System.Globalization.CultureInfo.InvariantCulture), AchievementCommand.FrameName(frame));

    /// <summary>
    /// The frame lands in the trailing bracket of the listing line, which is where legacy printed it (<c>Commands/AchievementCommand.cs:96-97</c>), and an absent frame leaves that bracket empty rather than filling it with a word.
    /// </summary>
    [Fact]
    public void ListingLine_PutsTheFrameInTheTrailingBracket()
    {
        Assert.Equal(
            "[TODO] A Balanced Diet (minecraft:husbandry/balanced_diet) [Challenge]",
            McStrings.Format(
                "cmd.achievement.entry_titled",
                McStrings.Get("cmd.achievement.todo"),
                "A Balanced Diet",
                "minecraft:husbandry/balanced_diet",
                AchievementCommand.FrameName(1)));

        Assert.Equal(
            "[TODO] minecraft:recipes/building_blocks/oak_stairs []",
            McStrings.Format(
                "cmd.achievement.entry",
                McStrings.Get("cmd.achievement.todo"),
                "minecraft:recipes/building_blocks/oak_stairs",
                AchievementCommand.FrameName(null)));
    }
}

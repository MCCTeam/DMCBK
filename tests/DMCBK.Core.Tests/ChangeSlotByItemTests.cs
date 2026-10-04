using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>changeslot &lt;item&gt;</c>: select a hotbar slot by what is in it, cycling through the matches.
/// <para>
/// The selection packet addresses one of nine hotbar slots, so this is hotbar-only by construction; an item in the backpack has no slot to select and saying so is more useful than "not found".
/// Cycling is derived from the CURRENTLY selected slot rather than from a remembered index, so it cannot go stale when the hotbar is rearranged, when something else moves the selection, or across a reconnect.
/// </para>
/// </summary>
public sealed class ChangeSlotByItemTests
{
    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost", 25565)
            .UseHostInterface(new SilentHost())
            .Build();

    private sealed class SilentHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }

    private static ItemStackInfo Stack(string id)
        => new(id, 1, false, null, 0, 0, [], []);

    private static (int Hotbar, ItemStackInfo Stack)[] Matches(params int[] hotbarSlots)
        => [.. hotbarSlots.Select(s => (s, Stack("minecraft:compass")))];

    #region The cycling rule

    [Fact]
    public void WithTwoMatches_RepeatingTheCommandAlternates()
    {
        // Matches at hotbar 1 and 3, hand on 0: 1, then 3, then back to 1.
        // This is the reported ask, that running it again picks "the next one".
        (int Hotbar, ItemStackInfo Stack)[] matches = Matches(1, 3);

        Assert.Equal(1, ChangeSlotCommand.NextAfter(matches, held: 0).Hotbar);
        Assert.Equal(3, ChangeSlotCommand.NextAfter(matches, held: 1).Hotbar);
        Assert.Equal(1, ChangeSlotCommand.NextAfter(matches, held: 3).Hotbar);
    }

    [Fact]
    public void FromAfterTheLastMatch_ItWrapsToTheFirst()
    {
        Assert.Equal(2, ChangeSlotCommand.NextAfter(Matches(2, 5), held: 8).Hotbar);
    }

    [Fact]
    public void WithOneMatch_ItSelectsThatSlotEveryTime()
    {
        // Including when the hand is already on it: "switch to my compass" with one compass has one answer, and re-selecting it is not a failure.
        (int Hotbar, ItemStackInfo Stack)[] one = Matches(4);

        Assert.Equal(4, ChangeSlotCommand.NextAfter(one, held: 0).Hotbar);
        Assert.Equal(4, ChangeSlotCommand.NextAfter(one, held: 4).Hotbar);
        Assert.Equal(4, ChangeSlotCommand.NextAfter(one, held: 8).Hotbar);
    }

    [Fact]
    public void EveryStartingHandPosition_LandsOnAMatch()
    {
        // The rule must be total: from any of the nine slots it picks one of the matches, never nothing.
        (int Hotbar, ItemStackInfo Stack)[] matches = Matches(0, 4, 7);

        for (int held = 0; held < 9; held++)
        {
            int chosen = ChangeSlotCommand.NextAfter(matches, held).Hotbar;
            Assert.Contains(chosen, new[] { 0, 4, 7 });
        }
    }

    [Fact]
    public void CyclingVisitsEveryMatchBeforeRepeating()
    {
        // Three matches, driven the way repeated invocations drive it: the hand follows the choice.
        (int Hotbar, ItemStackInfo Stack)[] matches = Matches(0, 4, 7);

        int held = 8;
        var visited = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            held = ChangeSlotCommand.NextAfter(matches, held).Hotbar;
            visited.Add(held);
        }

        Assert.Equal([0, 4, 7], visited);
    }

    #endregion
    #region A slot number that is not a slot

    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("99")]
    public void ANumberOutsideTheHotbar_IsTreatedAsASlotNumber(string typed)
    {
        // The slot argument is bounded 1..9, and a bare integer is a legal resource location, so these reach the ITEM node as minecraft:0 and friends.
        // Answering "No minecraft:0 in the hotbar" would be true and useless.
        Assert.True(Identifier.TryParse(typed, out Identifier id));
        Assert.True(ChangeSlotCommand.LooksLikeASlotNumber(id));
    }

    [Theory]
    [InlineData("compass")]
    [InlineData("minecraft:compass")]
    [InlineData("light_gray_wool")]
    [InlineData("mod:1thing")]
    public void ARealItemId_IsNot(string typed)
    {
        Assert.True(Identifier.TryParse(typed, out Identifier id));
        Assert.False(ChangeSlotCommand.LooksLikeASlotNumber(id));
    }

    #endregion
    #region Grammar

    [Theory]
    [InlineData("changeslot 1")]
    [InlineData("changeslot 9")]
    [InlineData("changeslot compass")]
    [InlineData("changeslot minecraft:compass")]
    [InlineData("changeslot light_gray_wool")]
    public async Task EveryAcceptedForm_Parses(string command)
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(command);

        Assert.DoesNotContain("<--[HERE]", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(CmdStatus.NotRun, result.Status);
    }

    [Fact]
    public async Task ANumericSlotStillWinsOverTheItemNode()
    {
        // Registration order is load-bearing: with the item node first, "changeslot 3" would have become "select whatever holds minecraft:3".
        // The numeric form must reach the inventory gate as a SLOT.
        await using Client client = BuildClient(); // no session, inventory on
        CmdResult result = await client.Commands.DispatchAsync("changeslot 3");

        // The slot path checks the session after the range; the item path would have needed the inventory snapshot first.
        // Either way the message must not be about an item called "3".
        Assert.DoesNotContain("minecraft:3", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItIsBehindTheInventoryGate()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost", 25565)
            .UseHostInterface(new SilentHost())
            .UseFeatures(new Umpk.Client.ClientFeatures { Inventory = false })
            .Build();

        CmdResult result = await client.Commands.DispatchAsync("changeslot compass");

        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Fact]
    public void TheUsageNamesBothForms()
        => Assert.Equal("changeslot <1-9>|<item>", new ChangeSlotCommand().CmdUsage);
    #endregion
}

using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk.Client.Actions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The lower-severity relative of the T4 "reports a success it did not achieve" family: four commands that reported an OUTCOME for a send they never confirmed.
/// They were not lying about the send; they told the user the world had changed when all the client knew was that bytes had left.
///
/// <para>
/// Two of the four have an observation available (<c>dig</c> reads the block back, <c>respawn</c> waits for the clientbound respawn); those classification cases moved to UMPK (D1.2 for dig, D1.4 for respawn) since the observation itself is now UMPK's <see cref="DigOutcome"/> / <see cref="RespawnOutcome"/>.
/// What is left here is the MESSAGE mapping (<c>Respawn_ALivingPlayerIsToldWhyNothingHappened</c>) and the two commands with no observation at all, which still say what they did instead of an effect (<c>useblock</c>, <c>useitem</c>).
/// Every test here is written so that undoing its fix makes it fail.
/// </para>
/// </summary>
public sealed class HonestOutcomeTests
{
    // --------------------------------------------------------------------------------------------- useblock / useitem.
    // No observation exists, so the wording is the fix. ---------------------------------------------------------------------------------------------

    [Fact]
    public void UseBlock_ReportsTheSendInsteadOfAnEffect()
    {
        string message = CommandStrings.UseBlockSent(10, 64, -3);

        // "Used the block." claimed an effect.
        // A use-on-block is whatever the target block decides it is (a container, a lever, a crop, a placement, or nothing at all) and vanilla sends nothing back that distinguishes them, so the send is the ceiling and the text says so.
        Assert.Contains("Sent a use interaction", message, StringComparison.Ordinal);
        Assert.Contains("10 64 -3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Used the block", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseItem_ReportsTheSendInsteadOfAnEffect()
    {
        string inAir = CommandStrings.UseItemSent(UseItemCommand.HandName(Hand.Off));
        string atBlock = CommandStrings.UseItemAtSent(UseItemCommand.HandName(Hand.Main), 10, 64, -3);

        Assert.Contains("Sent a use interaction", inAir, StringComparison.Ordinal);
        Assert.Contains("off hand", inAir, StringComparison.Ordinal);
        Assert.DoesNotContain("Used the held item", inAir, StringComparison.Ordinal);

        Assert.Contains("Sent a use interaction", atBlock, StringComparison.Ordinal);
        Assert.Contains("main hand", atBlock, StringComparison.Ordinal);
        Assert.Contains("10 64 -3", atBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Used the held item", atBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void UseInteractions_DeliberatelyStillCompleteOnTheWrite()
    {
        // Recorded so the route is not mistaken for an oversight, and so a later stream does not "finish the job" by inventing a confirmation these two cannot have.
        // Unlike the dig, there is no server reply that says what a use did, so these keep returning Task and their callers keep describing a send.
        Assert.Equal(typeof(Task), typeof(WorldApi).GetMethod(nameof(WorldApi.PlaceBlockAsync))!.ReturnType);
        Assert.Equal(typeof(Task), typeof(WorldApi).GetMethod(nameof(WorldApi.UseItemAsync))!.ReturnType);
    }

    // --------------------------------------------------------------------------------------------- respawn.
    // The classification (Confirmed/NotDead/Unconfirmed) is UMPK's RespawnOutcome now and is pinned there; what stays here is the message mapping for the most common branch. ---------------------------------------------------------------------------------------------

    [Fact]
    public void Respawn_ALivingPlayerIsToldWhyNothingHappened()
    {
        // The most common way to run this command: vanilla's perform_respawn handler returns without doing anything when the player is alive, and sends nothing back to say so.
        // "Respawn request sent." was printed anyway.
        (bool ok, string message) = RespawnCommand.Describe(RespawnOutcome.NotDead);

        Assert.False(ok);
        Assert.Contains("not dead", message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------- The dispatcher end: none of the four claims an outcome without a session. ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("dig 10 64 -3")]
    [InlineData("useblock 10 64 -3")]
    [InlineData("useitem")]
    [InlineData("respawn")]
    public async Task NoSession_ClaimsNoOutcome(string line)
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.NotEqual(CmdStatus.Done, result.Status);
        foreach (string claim in new[] { "Broke ", "Used the block", "Used the held item", "Respawned." })
            Assert.DoesNotContain(claim, result.Message ?? string.Empty, StringComparison.Ordinal);
    }
}

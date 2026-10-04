using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk;
using Umpk.Client;
using Umpk.Game.Inventory;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The "reports a success it did not achieve" family.
/// The version capability truth tables and the dialog-click classification that used to live here moved to UMPK, which now owns <c>ClientActionCapabilities</c> and <c>DialogActions.ClassifyClientSideAction</c> directly.
/// What stays is: the through-the-dispatcher cases that exercise MCC's own facade/command wiring end to end, the message-mapping tests over UMPK's typed outcomes, and the spawn-signal family.
/// </summary>
public sealed class FalseSuccessReportingTests
{
    // --------------------------------------------------------------------------------------------- A version that cannot send the anvil rename is told so, not congratulated.
    // The capability truth table itself (which versions can/cannot) is pinned in UMPK; this is the dispatcher-level case. ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NameItem_WithNoSession_DoesNotClaimARename()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();

        CmdResult result = await client.Commands.DispatchAsync("nameitem T4ANVIL");

        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.DoesNotContain("Renamed the item", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------- recipebook craft: the form is picked BY VERSION (RecipeCraftPlan.Plan, now UMPK's), and a version that cannot place says so.
    // What is left here is the message mapping and the through-the-dispatcher case; RecipeCraftPlan.Plan's own version-by-version truth table is pinned in UMPK. ---------------------------------------------------------------------------------------------

    [Fact]
    public void RecipeCraftRefusal_NeedsNetworkId_NamesTheVersionAndWhatWasTyped()
    {
        var placement = new RecipePlacementSupport(RecipePlacementForm.NetworkId, "1.21.6", 771);

        string message = RecipeBookCommand.DescribeRefusal(RecipeCraftRefusal.NeedsNetworkId, placement, "minecraft:torch");

        Assert.Contains("numeric network id", message, StringComparison.Ordinal);
        Assert.Contains("Minecraft 1.21.6", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Placed recipe", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecipeCraftRefusal_VersionCannotPlace_NamesTheVersion()
    {
        var placement = new RecipePlacementSupport(RecipePlacementForm.None, string.Empty, 340);

        string message = RecipeBookCommand.DescribeRefusal(RecipeCraftRefusal.VersionCannotPlace, placement, "minecraft:torch");

        Assert.Contains("no sendable place-recipe packet", message, StringComparison.Ordinal);
        Assert.Contains("protocol 340", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecipeCraft_OnAVersionThatCannotPlace_ReportsFailureThroughTheDispatcher()
    {
        // End to end through the real dispatcher with a bound (idle) 1.12.2 session, so the gate under test actually executes.
        // 1.12.2 identifies a recipe by a numeric crafting-manager id neither modelled record carries, so place_recipe is a marker there and nothing can go out.
        await using Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
        await using UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        CmdResult result = await client.Commands.DispatchAsync("recipebook craft minecraft:torch");

        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.DoesNotContain("Placed recipe", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", result.Message ?? string.Empty, StringComparison.Ordinal);

        // And it does not send the reader after a crafting menu that would not have helped.
        Assert.DoesNotContain("Open a crafting menu", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------- A dialog button press reports what it actually did.
    // The classification itself (DialogActions.ClassifyClientSideAction) moved to UMPK; DialogCommand.DescribeClick's message mapping over that classification stays MCC's. ---------------------------------------------------------------------------------------------

    // The "press" verb was never legacy wording ("Pressed dialog button [0] T4RUNBTN.").
    // Legacy never used it: DialogManager.Execute reports the thing that actually left the client, dialog_action_custom_sent ("Dialog custom action sent: {0}", DialogManager.cs:248) or dialog_action_command_sent ("Dialog command sent: {0}", DialogManager.cs:235).
    // What this test guards is unchanged: only a real send is a success.
    [Fact]
    public void DialogClick_OnlyASendIsReportedAsAPress()
    {
        var button = new DialogButtonInfo("T4RUNBTN", null, nameof(Umpk.Game.Dialogs.DialogActionKind.RunCommand), "/me T4RUNCLICK", null);

        (bool notPerformedOk, string notPerformed) =
            DialogCommand.DescribeClick(0, button, Umpk.Client.Actions.DialogClickOutcome.ActionNotPerformed);
        Assert.False(notPerformedOk);
        Assert.Equal("Unsupported dialog action: RunCommand", notPerformed);

        // The two honest successes: an action that reached the server, and a command this client ran.
        var custom = new DialogButtonInfo(
            "T4CUSTOMBTN", null, nameof(Umpk.Game.Dialogs.DialogActionKind.Custom), null, "example:t4custom");
        (bool sentOk, string sent) = DialogCommand.DescribeClick(0, custom, Umpk.Client.Actions.DialogClickOutcome.ActionSent);
        Assert.True(sentOk);
        Assert.Equal("Dialog custom action sent: example:t4custom", sent);

        (bool ranOk, string ran) = DialogCommand.DescribeClick(0, button, Umpk.Client.Actions.DialogClickOutcome.CommandSent);
        Assert.True(ranOk);
        Assert.Equal("Dialog command sent: /me T4RUNCLICK", ran);

        (bool noneOk, string none) = DialogCommand.DescribeClick(0, button, Umpk.Client.Actions.DialogClickOutcome.NoButton);
        Assert.False(noneOk);
        Assert.Equal("Unknown dialog action index: 0", none);
    }

    // --------------------------------------------------------------------------------------------- A position and game mode are reported only once the server has supplied them. ---------------------------------------------------------------------------------------------

    [Fact]
    public void PlayerStatus_DefaultsAreNotMarkedAsAReading()
    {
        // These are exactly the values the sample printed on a creative server whose spawn was (5.5, 66, 9.5).
        var unspawned = new PlayerStatus(
            20f, 20, 0f, 0, 0f, 0, "Survival", false, default, true, HasSpawned: false);

        Assert.False(unspawned.HasSpawned);
        Assert.Equal(0.0, unspawned.Position.X);
    }

    [Fact]
    public async Task WaitForSpawn_WithNoSession_ReturnsFalseInsteadOfHanging()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();

        Assert.False(await client.Game.Player.WaitForSpawnAsync());
    }

    [Fact]
    public async Task SpawnSignal_CompletesOnlyWhenTheServerPlacesUs()
    {
        var session = new GameSession();

        // No session bound: nothing to wait on at all.
        Assert.Null(session.Spawned);

        session.ArmSpawn();
        Task<bool>? spawned = session.Spawned;
        Assert.NotNull(spawned);

        // StartAsync returns here, at login-success, with the join packet and the initial teleport still in flight.
        // A read taken now is reading defaults.
        Assert.False(spawned.IsCompleted);

        session.MarkSpawned();
        Assert.True(await spawned);
    }

    [Fact]
    public async Task SpawnSignal_ResolvesFalseWhenTheSessionEndsFirst()
    {
        var session = new GameSession();
        session.ArmSpawn();
        Task<bool> spawned = session.Spawned!;

        session.Unbind();

        // False rather than a hang: a host learns the session ended without ever being placed.
        Assert.False(await spawned);
        Assert.Null(session.Spawned);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An UMPK client bound to 1.12.2 and never connected: no socket, and a version that genuinely has neither the anvil rename nor the book edit through the network-id place-recipe form.
    /// The version is load-bearing, not incidental: gating on "no session yet" instead of on the version would pass on any build at all.
    /// </summary>
    private static UmpkClient IdleUmpkClient() => ClientFor("1.12.2");

    /// <summary>
    /// A built-but-unconnected UMPK client for one version.
    /// <c>Capabilities</c> is resolved from the version at construction, so it answers before connect, which is what lets a bound-but-idle session be tested without a server.
    /// Physics and pathfinding are off because this does not need them.
    /// </summary>
    private static UmpkClient ClientFor(string versionName)
        => new UmpkClientBuilder()
            .UseVersion(Version(versionName))
            .UseProfile(new GameProfile(Guid.NewGuid(), "Tester"))
            .ConfigureFeatures(f =>
            {
                f.Physics = false;
                f.Pathfinding = false;
            })
            .Build();

    private static Umpk.Protocol.Java.JavaVersion Version(string name)
    {
        Assert.True(Umpk.Data.Java.JavaVersions.TryGetByName(name, out Umpk.Protocol.Java.JavaVersion? version), $"Unknown version '{name}'.");
        return version!;
    }
}

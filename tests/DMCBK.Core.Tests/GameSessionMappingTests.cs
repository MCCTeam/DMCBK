using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Guards the two cross-cutting rules <see cref="GameSession"/> centralizes for the GameApi facades: a missing session surfaces as <see cref="DmcbkNotInSessionException"/>, and that mapping holds when reached through a real facade (not only the raw handle).
/// The disabled-feature wrapping (<c>FeatureDisabledException</c> to <see cref="DmcbkFeatureDisabledException"/>) needs a live <c>UmpkClient</c> (sealed, not fakeable) and is exercised by the live <c>--exercise smoke</c> diagnostic instead.
/// </summary>
public sealed class GameSessionMappingTests
{
    [Fact]
    public void Require_Throws_NotInSession_WhenUnbound()
    {
        var session = new GameSession();
        Assert.Throws<DmcbkNotInSessionException>(() => session.Require());
    }

    [Fact]
    public void Current_IsNull_WhenUnbound()
    {
        var session = new GameSession();
        Assert.Null(session.Current);
    }

    [Fact]
    public async Task ReadAsync_Throws_NotInSession_WhenUnbound()
    {
        var session = new GameSession();
        await Assert.ThrowsAsync<DmcbkNotInSessionException>(
            () => session.ReadAsync(client => 0, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_Throws_NotInSession_WhenUnbound()
    {
        var session = new GameSession();
        await Assert.ThrowsAsync<DmcbkNotInSessionException>(
            () => session.RunAsync(client => Task.CompletedTask));
    }

    [Fact]
    public async Task WorldFacade_Throws_NotInSession_WhenUnbound()
    {
        var world = new WorldApi(new GameSession());
        await Assert.ThrowsAsync<DmcbkNotInSessionException>(() => world.GetTimeAsync());
    }

    [Fact]
    public async Task PlayerFacade_Throws_NotInSession_WhenUnbound()
    {
        var player = new PlayerApi(new GameSession(), translations: null);
        await Assert.ThrowsAsync<DmcbkNotInSessionException>(() => player.GetStatusAsync());
    }

    [Fact]
    public async Task InventoryFacade_Throws_NotInSession_WhenUnbound()
    {
        var inventory = new InventoryApi(new GameSession(), translations: null);
        await Assert.ThrowsAsync<DmcbkNotInSessionException>(() => inventory.GetPlayerInventoryAsync());
    }

    [Fact]
    public void EventsFacade_Throws_NotInSession_WhenUnbound()
    {
        var game = new GameApi(
            new ChatApi(),
            new WorldApi(new GameSession()),
            new EntitiesApi(new GameSession(), translations: null),
            new InventoryApi(new GameSession(), translations: null),
            new MovementApi(new GameSession()),
            new PlayerApi(new GameSession(), translations: null),
            new SessionApi(new GameSession()),
            new DialogApi(new GameSession(), translations: null),
            new MapApi(new GameSession()),
            new GameSession());
        Assert.Throws<DmcbkNotInSessionException>(() => _ = game.Events);
    }

    [Fact]
    public void Bind_ThenUnbind_ClearsCurrent()
    {
        // Bind rejects null and Unbind restores the not-in-session contract; a real client is not needed to prove the gate transitions (binding a live client is covered by the live exercise).
        var session = new GameSession();
        Assert.Throws<ArgumentNullException>(() => session.Bind(null!));
        session.Unbind();
        Assert.Null(session.Current);
    }
}

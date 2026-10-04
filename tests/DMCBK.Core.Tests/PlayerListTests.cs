using DMCBK.Core;
using DMCBK.Core.Commands.Impl;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>list</c> command shows what a vanilla client's tab overlay would show.
/// <para>
/// Reported live on a Velocity network: <c>list</c> answered "PlayerList: , testomir2".
/// The proxy keeps an unlisted, unnamed tab-list entry alongside the real player, and the command joined every entry it was given, listed or not.
/// Vanilla never draws those: its overlay iterates <c>getListedOnlinePlayers()</c> (1.21.11-client-decompiled PlayerTabOverlay.java:95), which is a different collection from <c>getOnlinePlayers()</c>.
/// </para>
/// </summary>
public sealed class PlayerListTests
{
    private static TabListEntryInfo Entry(
        string name, bool listed = true, int order = 0, string gameMode = "Survival")
        => new(Guid.NewGuid(), name, gameMode, 0, null, listed, order);

    private static TabListSnapshot Snapshot(params TabListEntryInfo[] entries)
        => new(entries, null, null);

    [Fact]
    public void UnlistedEntries_AreNotPlayers()
    {
        // The exact reported shape: a proxy's unlisted, unnamed entry ahead of the real player.
        List<string> names = ListCommand.ListedNames(Snapshot(
            Entry(string.Empty, listed: false),
            Entry("testomir2")));

        Assert.Equal(["testomir2"], names);
    }

    [Fact]
    public void AListedEntryWithNoName_IsStillNotPrinted()
    {
        // Belt and braces: a server that lists a blank-named entry would put the same stray comma back.
        Assert.Equal(
            ["real"],
            ListCommand.ListedNames(Snapshot(Entry("   "), Entry("real"), Entry(string.Empty))));
    }

    [Fact]
    public void NamesFollowVanillasComparator()
    {
        // Descending list order first, then spectators last, then name case-insensitively (PlayerTabOverlay.java:54-57).
        List<string> names = ListCommand.ListedNames(Snapshot(
            Entry("charlie"),
            Entry("Bravo"),
            Entry("watcher", gameMode: "Spectator"),
            Entry("alpha"),
            Entry("pinned", order: 5)));

        Assert.Equal(["pinned", "alpha", "Bravo", "charlie", "watcher"], names);
    }

    [Fact]
    public void AnEmptyTabList_YieldsNoNames()
    {
        // Legacy printed the header with nothing after it rather than a "no players" sentence, and that wording is unchanged; this only proves the filter does not throw on the empty case.
        Assert.Empty(ListCommand.ListedNames(Snapshot()));
        Assert.Empty(ListCommand.ListedNames(Snapshot(Entry("ghost", listed: false))));
    }

    [Fact]
    public void EveryEntryListed_KeepsEveryone()
    {
        // The ordinary single-server case must not lose anyone to the filter.
        List<string> names = ListCommand.ListedNames(Snapshot(
            Entry("one"), Entry("two"), Entry("three")));

        Assert.Equal(3, names.Count);
    }
}

using System.Globalization;
using System.Resources;
using DMCBK.Core.Localization;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Guards the MCC user-interface string table.
/// The corpus is generated from the legacy <c>Translations.resx</c> by <c>tools/gen_translations.py</c> and embedded in DMCBK.Core, so the two things that can silently break are the embed itself (a wrong <c>LogicalName</c> makes every lookup miss and <see cref="McStrings.Get"/> returns the key, which reads as plausible output) and the wording drifting away from the legacy client it is supposed to be indistinguishable from.
/// </summary>
public sealed class McStringsTests
{
    [Fact]
    public void Get_ResolvesAKnownKey_SoTheResourceIsActuallyEmbedded()
    {
        // Not Assert.NotNull: a miss returns the key itself, which is not null.
        // The assertion has to be that the VALUE came back.
        Assert.Equal("Weather change: It is raining now.", McStrings.Get("bot.alerts.start_rain"));
    }

    [Fact]
    public void Get_ResolvesEveryKeyTheGeneratorEmitted()
    {
        var resources = new ResourceManager(
            "DMCBK.Core.Localization.Resources.McStrings", typeof(McStrings).Assembly);

        using ResourceSet? set = resources.GetResourceSet(CultureInfo.InvariantCulture, true, true);
        Assert.NotNull(set);

        int count = 0;
        foreach (System.Collections.DictionaryEntry entry in set!)
        {
            string key = (string)entry.Key;
            Assert.NotEqual(key, McStrings.Get(key));
            count++;
        }

        // The legacy corpus size.
        // A drop means the generator lost entries; a jump that comes with no new key in the LEGACY resx means someone hand-edited the generated one instead of regenerating it.
        // Bump this deliberately, with the keys, when the corpus genuinely grows: 975 at the port, plus mcc.switching_servers and mcc.switching_servers_busy for the proxy backend-switch notice, plus the nine mcc.auth_method_* / mcc.yggdrasil_* keys for the first-run guided login prompt, plus the twelve cmd.book.* keys that replaced "Book edit packet sent." and its neighbours with sentences that say what happened and what to do next, plus the command-UX pass: 20 cmd.help.* and cmd.category.* keys for the grouped index and the per-command page, 5 cmd.error.* for the rewritten parse failures, 2 for /set's confirmation and the shared empty state, 32 man.* for the manual's own UI, and 14 man.page.* holding the manual pages themselves (which is what makes them translate through this corpus rather than through a second pipeline).
        // Plus 4 cmd.entityCmd.* for the entity listing's table header, count line and empty state, 6 cmd.health.* for the status line and its bar label, and 9 cmd.achievement.* for the advancement table's columns, progress line and footer hints, plus the Beacon scripting surface: 4 beacon.time.* for localized time.ago rendering, man.summary.scripts for the manual index, and man.page.scripts holding the scripts page itself, plus cmd.entityCmd.not_in_front for the /entity attack facing gate and cmd.entityCmd.not_visible for its line-of-sight gate.
        Assert.Equal(1042, count);
    }

    [Fact]
    public void Get_ReturnsTheKey_WhenItIsMissing()
        => Assert.Equal("no.such.key", McStrings.Get("no.such.key"));

    [Fact]
    public void Format_SubstitutesPositionalArguments()
    {
        // cmd.changeSlot.changed is "Changed to slot {0}" in the legacy corpus.
        Assert.Equal("Changed to slot 3", McStrings.Format("cmd.changeSlot.changed", 3));
    }

    [Fact]
    public void GeneratedAccessor_AgreesWithTheKeyLookup()
        => Assert.Equal(McStrings.Get("bot.alerts.start_rain"), McStrings.bot_alerts_start_rain);

    [Theory]
    // Wording the user must not see change.
    // Each is the legacy value, verbatim.
    [InlineData("cmd.inventory.inventories", "Inventories")]
    [InlineData("cmd.inventory.found_items", "Found items")]
    [InlineData("cmd.inventory.hotbar", "Your selected hotbar is {0}")]
    [InlineData("cmd.inventory.no_item", "No item in slot #{0}")]
    [InlineData("cmd.look.at", "Looking at yaw: {0} pitch: {1}")]
    [InlineData("cmd.inventory.tui_only", "Interactive TUI is only available in TUI console mode. Use '/inventory <id> list' instead.")]
    public void Corpus_KeepsLegacyWording(string key, string expected)
        => Assert.Equal(expected, McStrings.Get(key));
}

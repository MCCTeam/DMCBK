using System.Globalization;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Localization;
using DMCBK.Core.Manual;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// What the manual has to hold true, checked mechanically.
/// <para>
/// These check that pages EXIST, are reachable, and do not contradict the code about names.
/// Whether a page is still accurate is a human job, and AGENTS.md says so: the rule is that a change to a command or a feature updates its page in the same commit.
/// </para>
/// </summary>
public sealed class ManualCoverageTests
{
    private readonly ManualCatalog _manual = new();

    public static TheoryData<string> Topics()
    {
        var data = new TheoryData<string>();
        foreach (ManualTopic topic in ManualTopics.All)
            data.Add(topic.Id);

        return data;
    }

    /// <summary>A declared topic with no page renders "the manual page is missing from this build".</summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void EveryTopic_HasAnEnglishPage(string id)
    {
        Assert.True(_manual.Has(id, "en"), $"No English page for manual topic '{id}'.");
    }

    /// <summary>
    /// Every language ships the same topic set.
    /// A translation that silently drops a page would otherwise just fall back to English with nothing said, and nobody would notice the gap.
    /// </summary>
    [Fact]
    public void EveryLanguage_ShipsEveryTopic()
    {
        foreach (string language in _manual.Languages())
        {
            foreach (ManualTopic topic in ManualTopics.All)
            {
                Assert.True(
                    _manual.Has(topic.Id, language),
                    $"Language '{language}' is missing the '{topic.Id}' page. Add it, or remove the language.");
            }
        }
    }

    /// <summary>The index prints a summary per topic; a missing key would print the key.</summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void EveryTopic_HasASummaryString(string id)
    {
        string summary = McStrings.Get($"man.summary.{id}");
        Assert.False(string.IsNullOrWhiteSpace(summary), $"No man.summary.{id} in the corpus.");
        Assert.DoesNotContain("man.summary.", summary, StringComparison.Ordinal);
    }

    /// <summary>A page starts with its H1; the renderer treats the first heading as the page title.</summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void EveryPage_StartsWithATopLevelHeading(string id)
    {
        string page = _manual.Read(id, CultureInfo.InvariantCulture)!;
        string first = page.Split('\n').First(l => l.Trim().Length > 0).Trim();
        Assert.StartsWith("# ", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pages are prose for users, not internal notes.
    /// An em dash is banned repo-wide, and pages avoid curly quotes and the flagged words checked below.
    /// </summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void EveryPage_KeepsTheHouseStyle(string id)
    {
        string page = _manual.Read(id, CultureInfo.InvariantCulture)!;

        Assert.DoesNotContain('—', page);   // em dash
        Assert.DoesNotContain('‘', page);   // curly quotes
        Assert.DoesNotContain('’', page);
        Assert.DoesNotContain('“', page);
        Assert.DoesNotContain('”', page);

        foreach (string tell in new[] { "delve", "pivotal", "showcas", "underscor", "testament", "tapestry" })
            Assert.DoesNotContain(tell, page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown topic answers with a pointer to the index, never with an empty page.</summary>
    [Fact]
    public void UnknownTopic_IsNotFound()
    {
        Assert.Null(ManualTopics.Find("not-a-topic"));
        Assert.Null(_manual.Find("not-a-topic"));
        Assert.Null(_manual.Read("not-a-topic"));
    }

    /// <summary>
    /// A plugin's pages join the manual while it is loaded and leave when it unloads.
    /// A topic that outlives its plugin points at files nothing will read.
    /// </summary>
    [Fact]
    public void PluginSource_JoinsAndLeavesTheManual()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-man-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "en"));
        File.WriteAllText(Path.Combine(root, "en", "widget.md"), "# Widget\n\nDoes a thing.\n");

        try
        {
            Assert.Null(_manual.Find("widget"));

            var source = new DirectoryManualSource(root, [new ManualTopic("widget", ManualGroup.Plugins, "A widget.")]);
            using (_manual.Register(source))
            {
                Assert.NotNull(_manual.Find("widget"));
                Assert.Contains("Does a thing.", _manual.Read("widget")!, StringComparison.Ordinal);
            }

            Assert.Null(_manual.Find("widget"));
            Assert.Null(_manual.Read("widget"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A plugin page falls back per language the same way MCC's own pages do, and a flat <c>man/&lt;topic&gt;.md</c> counts as English.
    /// </summary>
    [Fact]
    public void PluginSource_FallsBackPerLanguage()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-man-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "de"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "widget.md"), "english");
        File.WriteAllText(Path.Combine(root, "de", "widget.md"), "deutsch");

        try
        {
            var source = new DirectoryManualSource(root, [new ManualTopic("widget", ManualGroup.Plugins, "A widget.")]);

            Assert.Equal("deutsch", source.Read("widget", "de"));
            Assert.Equal("english", source.Read("widget", "en"));
            Assert.Null(source.Read("widget", "fr"));

            // A topic id must not be able to walk out of the plugin folder.
            Assert.Null(source.Read("../../secret", "en"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Every group in the index has at least one topic, or it prints an empty heading.</summary>
    [Fact]
    public void EveryGroup_HasTopics()
    {
        foreach (ManualGroup group in ManualTopics.Groups)
        {
            // Plugins is the exception on purpose: it is filled at runtime by loaded plugins, and the index skips an empty group rather than printing a bare heading.
            if (group == ManualGroup.Plugins)
                continue;

            Assert.Contains(ManualTopics.All, t => t.Group == group);
        }
    }

    /// <summary>
    /// The plain-text fallback is what a host with no document renderer prints, so it must not leave Markdown syntax on screen.
    /// </summary>
    [Fact]
    public void PlainTextFallback_StripsMarkers()
    {
        string plain = ManCommand.PlainText("# Title\n\nSome **bold** and `code`.\n\n> [!WARNING]\n> Careful.\n");

        Assert.Contains("TITLE", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("**", plain, StringComparison.Ordinal);
        Assert.DoesNotContain('`', plain);
        Assert.Contains("WARNING:", plain, StringComparison.Ordinal);
    }

    /// <summary>Reading the same page twice is served from cache and returns the same text.</summary>
    [Fact]
    public void Pages_AreCachedStably()
    {
        string? first = _manual.Read("movement", CultureInfo.InvariantCulture);
        string? second = _manual.Read("movement", CultureInfo.InvariantCulture);
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    /// <summary>An unknown culture falls back to English rather than to nothing.</summary>
    [Fact]
    public void UnknownCulture_FallsBackToEnglish()
    {
        string? page = _manual.Read("movement", new CultureInfo("qps-ploc"));
        Assert.NotNull(page);
        Assert.Contains("physics", page, StringComparison.OrdinalIgnoreCase);
    }
}

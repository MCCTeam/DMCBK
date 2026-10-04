using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>book</c> command's grammar and its wording.
/// <para>
/// Reported after a session with it: <c>book</c> alone was a parse error, adding a page meant knowing that <c>edit insert</c> existed and passing the right number, <c>edit insert 2</c> without text was a parse error too, being told "Page 2 is outside the current page range 1-1" left the reader no way forward, and every success said "Book edit packet sent."
/// The grammar and the sentences below are the answer to each of those.
/// </para>
/// </summary>
public sealed class BookCommandTests
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

    /// <summary>
    /// Every form the command accepts.
    /// With no session these all stop at "not connected", which is the point: reaching that means the line PARSED.
    /// A form the grammar rejects reports the parse error instead, carrying the caret.
    /// </summary>
    [Theory]
    [InlineData("book")]
    [InlineData("book read")]
    [InlineData("book read 3")]
    [InlineData("book write hello world")]      // the shorthand; `write text` was the only spelling before
    [InlineData("book write text hello world")]
    [InlineData("book write file /tmp/x.txt")]
    [InlineData("book edit")]
    [InlineData("book edit page 1 text")]
    [InlineData("book edit append")]            // new: add a page at the end
    [InlineData("book edit append some text")]
    [InlineData("book edit insert 2")]          // new: text is optional, this was a parse error
    [InlineData("book edit insert 2 some text")]
    [InlineData("book edit delete 2")]
    [InlineData("book sign My Title")]
    public async Task EveryAcceptedForm_Parses(string command)
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(command);

        Assert.DoesNotContain("<--[HERE]", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(CmdStatus.NotRun, result.Status);
    }

    [Fact]
    public async Task BareBook_IsNotAParseError()
    {
        // It used to answer "Unknown command at position 4: book<--[HERE]".
        // It now has a root action of its own (report the held book), so it never reaches the parse-failure path at all.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("book");

        Assert.DoesNotContain("<--[HERE]", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUsageListingNamesTheNewVerbs()
    {
        await using Client client = BuildClient();
        CmdResult help = await client.Commands.DispatchAsync("help book");

        var sb = new System.Text.StringBuilder();
        string raw = help.Message!;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\u00a7' && i + 1 < raw.Length)
            {
                i++;
                continue;
            }

            sb.Append(raw[i]);
        }

        string listing = sb.ToString();

        // The page states one row per idea, in the wording a user types rather than the argument names the parser happens to use.
        Assert.Contains("book edit append", listing, StringComparison.Ordinal);
        Assert.Contains("book edit insert <n>", listing, StringComparison.Ordinal);
        Assert.Contains("book write <text>", listing, StringComparison.Ordinal);
        Assert.Contains("book sign <title>", listing, StringComparison.Ordinal);

        // Every verb the command actually has is on the page.
        Assert.Contains("book read", listing, StringComparison.Ordinal);
        Assert.Contains("book edit delete", listing, StringComparison.Ordinal);
    }

    #region Wording

    [Fact]
    public void ReadingAPageThatIsNotThere_SaysWhatTheBookHas()
    {
        // Was: "Page 9 is outside the current page range 1-2.", which is a range, not a sentence.
        var book = new BookContentInfo(false, null, null, 0, true, ["one", "two"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book, page: 9);

        string only = Assert.Single(lines);
        Assert.Contains("2 page(s)", only, StringComparison.Ordinal);
        Assert.Contains("no page 9", only, StringComparison.Ordinal);
        Assert.DoesNotContain("range", only, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingARealPage_IsUnchanged()
    {
        var book = new BookContentInfo(false, null, null, 0, true, ["one", "two"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book, page: 2);

        Assert.Contains(lines, l => l.Contains("Page 2/2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("two", StringComparison.Ordinal));
    }

    [Fact]
    public void TheResultMessages_SayWhatHappened()
    {
        // The whole family, checked for the thing that made the old ones useless: they described a PACKET.
        foreach (string key in new[]
                 {
                     "cmd.book.written", "cmd.book.page_updated", "cmd.book.page_inserted",
                     "cmd.book.page_appended", "cmd.book.page_deleted", "cmd.book.signed",
                     "cmd.book.update_failed", "cmd.book.editor_opened",
                 })
        {
            string text = Localization.McStrings.Get(key);

            Assert.NotEqual(key, text);
            Assert.DoesNotContain("packet", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TUI", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheOutOfRangeHint_NamesBothWaysToAddAPage()
    {
        // The message the user was left stuck on now carries the way forward.
        string hint = Localization.McStrings.Format("cmd.book.add_page_hint", "/", 2);

        Assert.Contains("book edit append", hint, StringComparison.Ordinal);
        Assert.Contains("book edit insert 2", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpLines_DescribeTheVerbsInWords()
    {
        // `help book edit` used to be a bare grammar dump; it now says what each verb does.
        string edit = Localization.McStrings.Get("cmd.book.help_edit");

        Assert.Contains("append", edit, StringComparison.Ordinal);
        Assert.Contains("add a page at the end", edit, StringComparison.Ordinal);
    }
    #endregion
}

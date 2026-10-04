using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Exercises the command behavior that consumes the recipe-book and dialog UMPK surfaces: recipe-book enumeration (with the honest 1.21.2+ incompleteness wording), leaving a bed, reading and re-signing a decoded book, and the real server-dialog model.
/// Split in the established style: the display logic is a pure static formatter tested directly against snapshot records, and the wiring is tested by dispatching against an unstarted <see cref="Client"/> (so <c>InSession</c> is false) where a parsed subcommand reaches its gate and an unparsed one would report <see cref="CmdStatus.NotRun"/>.
/// </summary>
public sealed class CommandConsumptionTests
{
    /// <summary>
    /// The offline reply, corpus key <c>mcc.disconnected</c> rendered with the default '/' prefix (CommandService.ApplyPrefix defaults to <c>InternalCommandPrefix.Slash</c>).
    /// It points at <c>/connect</c> rather than <c>/help</c>: without a session the next useful thing is a server, not the command index.
    /// </summary>
    private const string NotConnected = "Not connected to any server. Type '/connect <host[:port]>'.";

    private static Client BuildClient(ClientFeatures? features = null, ICommandOutput? output = null)
    {
        var builder = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new TestHost(output));
        if (features is not null)
            builder.UseFeatures(features);

        return builder.Build();
    }

    private static RecipeBookSnapshot Book(
        IReadOnlyList<string>? recipes = null,
        IReadOnlyList<int>? ids = null,
        int opaqueAdditions = 0)
        => new(recipes ?? [], ids ?? [], [], opaqueAdditions, Revision: 1);

    #region recipebook list (ClientState.Recipes)

    [Fact]
    public void RecipeBook_Empty_ReportsNoneUnlocked()
    {
        IReadOnlyList<string> lines = RecipeBookCommand.FormatList(Book());
        Assert.Equal([CommandStrings.RecipeBookNone], lines);
    }

    [Fact]
    public void RecipeBook_NamedRecipes_AreEnumeratedSortedUnderACountHeader()
    {
        IReadOnlyList<string> lines = RecipeBookCommand.FormatList(
            Book(recipes: ["minecraft:torch", "minecraft:stick", "minecraft:chest"]));

        Assert.Equal(CommandStrings.RecipeBookHeader(3), lines[0]);
        Assert.Equal(CommandStrings.RecipeBookEntry("minecraft:chest"), lines[1]);
        Assert.Equal(CommandStrings.RecipeBookEntry("minecraft:stick"), lines[2]);
        Assert.Equal(CommandStrings.RecipeBookEntry("minecraft:torch"), lines[3]);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void RecipeBook_NumericIds_AreEnumerated()
    {
        // 1.12-1.12.2 names recipes by crafting-manager id, so the list must not look empty there.
        IReadOnlyList<string> lines = RecipeBookCommand.FormatList(Book(ids: [7, 3]));

        Assert.Equal(CommandStrings.RecipeBookHeader(2), lines[0]);
        Assert.Equal(CommandStrings.RecipeBookNumericEntry(3), lines[1]);
        Assert.Equal(CommandStrings.RecipeBookNumericEntry(7), lines[2]);
    }

    /// <summary>
    /// A recipe is named after what it makes, so the listing leads with that name.
    /// It used to print the wire id, which asked the reader to translate "minecraft:diamond_block" into "Block of Diamond" themselves.
    /// </summary>
    [Fact]
    public void RecipeBook_NamesRecipesAfterWhatTheyMake()
    {
        var book = new RecipeBookSnapshot(
            ["minecraft:acacia_boat", "minecraft:diamond_block"], [], [], 0, 1);

        string text = string.Join('\n', RecipeBookCommand.FormatList(book, new VanillaNameStub()));

        Assert.Contains("Acacia Boat", text, StringComparison.Ordinal);
        Assert.Contains("Block of Diamond", text, StringComparison.Ordinal);

        // The identifier stays on the row: it is what `recipebook craft` takes, so a listing without it would leave nothing on screen the reader could type back.
        Assert.Contains("minecraft:acacia_boat", text, StringComparison.Ordinal);
    }

    /// <summary>A caller with no session gets the raw identifiers rather than a crash.</summary>
    [Fact]
    public void RecipeBook_WithoutTranslations_KeepsTheIdentifiers()
    {
        var book = new RecipeBookSnapshot(["minecraft:acacia_boat"], [], [], 0, 1);

        string text = string.Join('\n', RecipeBookCommand.FormatList(book));

        Assert.Contains("minecraft:acacia_boat", text, StringComparison.Ordinal);
    }

    /// <summary>A translation table that answers for the two keys this test needs and nothing else.</summary>
    private sealed class VanillaNameStub : Umpk.Text.ITranslationSource
    {
        private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
        {
            ["item.minecraft.acacia_boat"] = "Acacia Boat",
            ["block.minecraft.diamond_block"] = "Block of Diamond",
        };

        public bool TryResolve(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? template)
            => Table.TryGetValue(key, out template);
    }

    [Fact]
    public void RecipeBook_OpaqueAdditionsOnly_SaysUndecodedRatherThanEmpty()
    {
        // The 1.21.2+ honest limitation: additions arrived but stay opaque.
        // Reporting "no unlocked recipes" there would be a lie, so the opaque count drives its own message.
        IReadOnlyList<string> lines = RecipeBookCommand.FormatList(Book(opaqueAdditions: 4));

        Assert.Equal([CommandStrings.RecipeBookOpaqueOnly(4)], lines);
        Assert.DoesNotContain(CommandStrings.RecipeBookNone, lines);
    }

    [Fact]
    public void RecipeBook_NamedPlusOpaqueAdditions_MarksTheListIncomplete()
    {
        IReadOnlyList<string> lines = RecipeBookCommand.FormatList(
            Book(recipes: ["minecraft:stick"], opaqueAdditions: 2));

        Assert.Equal(CommandStrings.RecipeBookHeader(1), lines[0]);
        Assert.Equal(CommandStrings.RecipeBookEntry("minecraft:stick"), lines[1]);
        Assert.Equal(CommandStrings.RecipeBookOpaqueNote(2), lines[2]);
    }

    [Fact]
    public async Task RecipeBookList_WhenInventoryDisabled_FailsNeedInventory()
    {
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });
        CmdResult result = await client.Commands.DispatchAsync("recipebook list");
        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Fact]
    public async Task RecipeBookList_WithoutSession_ReportsNotConnected()
    {
        // This used to return Done with "no unlocked recipes" even with no session at all.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("recipebook list");
        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(CommandText.NotConnected(noPrefix: false, prefix: '/'), result.Message);
    }

    #endregion
    #region bed leave (MovementActions.LeaveBedAsync)

    [Fact]
    public async Task BedLeave_IsRegistered_AndGatesOnSessionOnly()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("bed leave");
        Assert.NotEqual(CmdStatus.NotRun, result.Status);
        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(NotConnected, result.Message);
    }

    [Fact]
    public async Task BedLeave_WithTerrainDisabled_IsNotBehindTheTerrainGate()
    {
        // Waking up is one player-command packet: no block lookup, no movement, so terrain must not gate it.
        await using Client client = BuildClient(new ClientFeatures { Terrain = false, Physics = false, Pathfinding = false });
        CmdResult result = await client.Commands.DispatchAsync("bed leave");
        Assert.NotEqual(CmdStatus.FailNeedTerrain, result.Status);
        Assert.Equal(NotConnected, result.Message);
    }

    [Fact]
    public async Task BedSleep_StillRequiresTerrain()
    {
        await using Client client = BuildClient(new ClientFeatures { Terrain = false, Physics = false, Pathfinding = false });
        CmdResult result = await client.Commands.DispatchAsync("bed sleep 5");
        Assert.Equal(CmdStatus.FailNeedTerrain, result.Status);
    }

    #endregion
    #region book read / sign (ItemStack.Book -> BookContent)

    // The four rendering cases below used to pin this client's own shape: a header that counted the pages ("Book and quill (2 page(s)):"), one " Page n: <text>" line per page, a "Copy generation: n." line, an "(empty)" placeholder and a "That book has no pages." line.
    // Legacy's FormatBook (MinecraftClient/Commands/Book.cs:273-298) has none of those: it writes the corpus header (cmd.book.header_signed "Written book: {0} by {1}" or cmd.book.header_writable "Writable book"), then per page a cmd.book.page_header line ("Page {0}/{1}") followed by the page text on its own line, and finally TrimEnd()s the whole thing.
    // Generation is not rendered at all, and an empty page contributes an empty line that the trailing TrimEnd() eats when it is last.

    [Fact]
    public void BookFormat_SignedBook_ShowsTitleAuthorAndPages()
    {
        var book = new BookContentInfo(
            IsSigned: true, "Travels", "Steve", Generation: 0, Resolved: true, ["First page", "Second page"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book);

        Assert.Equal(
            ["Written book: Travels by Steve", "Page 1/2", "First page", "Page 2/2", "Second page"],
            lines);
    }

    [Fact]
    public void BookFormat_Copy_RendersLikeAnyOtherSignedBook()
    {
        // Legacy never printed the copy generation, so a copy of a copy renders exactly as the original does.
        // Pinned as a whole-output equality so a reintroduced generation line fails here.
        var book = new BookContentInfo(true, "Travels", "Steve", Generation: 2, Resolved: true, ["p"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book);

        Assert.Equal(["Written book: Travels by Steve", "Page 1/1", "p"], lines);
        Assert.Equal(BookCommand.FormatBook(book with { Generation = 0 }), lines);
    }

    [Fact]
    public void BookFormat_UnsignedBook_UsesTheWritableHeader_AndTrimsTheTrailingEmptyPage()
    {
        var book = new BookContentInfo(false, null, null, 0, false, ["written", string.Empty]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book);

        // The second page still gets its "Page 2/2" header, so it is not skipped; only its empty text is trimmed away, because legacy TrimEnd()s the assembled block (Book.cs:297).
        Assert.Equal(["Writable book", "Page 1/2", "written", "Page 2/2"], lines);
    }

    [Fact]
    public void BookFormat_EmptyPageInTheMiddle_IsKeptAsItsOwnLine()
    {
        var book = new BookContentInfo(false, null, null, 0, false, [string.Empty, "written"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book);

        Assert.Equal(["Writable book", "Page 1/2", string.Empty, "Page 2/2", "written"], lines);
    }

    [Fact]
    public void BookFormat_NoPages_RendersTheHeaderAlone()
    {
        var book = new BookContentInfo(false, null, null, 0, false, []);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book);

        Assert.Equal(["Writable book"], lines);
    }

    [Fact]
    public void BookFormat_PageOutOfRange_ReplacesTheWholeRendering()
    {
        // Book.cs:283-284: an out-of-range page number returns the message on its own, header and all.
        // What it SAYS changed with the book redesign, from a range ("outside the current page range 1-1") to a sentence; that the whole rendering is replaced by it, which is what this pins, did not.
        var book = new BookContentInfo(false, null, null, 0, false, ["only page"]);

        IReadOnlyList<string> lines = BookCommand.FormatBook(book, page: 4);

        Assert.Equal(["This book has 1 page(s), so there is no page 4."], lines);
    }

    [Fact]
    public async Task BookSign_WithTitleOnly_IsRegistered()
    {
        // The new reuse-existing-pages form: it must parse (not NotRun) and reach the inventory gate.
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });
        CmdResult result = await client.Commands.DispatchAsync("book sign Travels");
        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Fact]
    public async Task BookSign_WithTitleAndText_StillParses()
    {
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });
        CmdResult result = await client.Commands.DispatchAsync("book sign Travels once upon a time");
        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Fact]
    public async Task BookRead_WithoutSession_ReportsNotConnected()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("book read");
        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(NotConnected, result.Message);
    }

    #endregion
    #region dialog (ClientState.Dialogs + ClientActions.Dialog)

    private static DialogSnapshot Dialog(
        IReadOnlyList<DialogInputInfo>? inputs = null,
        IReadOnlyList<DialogButtonInfo>? buttons = null)
        => new(
            "minecraft:multi_action",
            "Choose",
            null,
            CanCloseWithEscape: true,
            ["Pick one"],
            inputs ?? [],
            buttons ?? [],
            HasExitAction: true,
            RegistryId: null);

    /// <summary>
    /// The box legacy drew around every rendered dialog: 50 dashes above and below (DialogFormatter.cs:17, :22-25, :50).
    /// </summary>
    private const string DialogBorder = "--------------------------------------------------";

    /// <summary>The trailing hint legacy always appended, in italics (DialogFormatter.cs:48-49).</summary>
    private const string DialogHelpHint = "§oUse /dialog help for a list of commands.§r";

    // The five rendering cases below used to pin this client's own line-per-fact shape ("Dialog: Choose", "Type: ...", "Buttons:", 0-based button numbers, one line per option, "This dialog has no buttons.").
    // Legacy renders a BOX (DialogFormatter.Render, MinecraftClient/Dialogs/DialogFormatter.cs:19-52): a 50-dash border, the indented title, the border again, the non-blank body lines, an optional "Inputs:" block, an optional "Actions:" block whose entries are 1-based " [n] label (kind)", a blank line, the italic help hint and a closing border.
    // Options are listed inside their input's description rather than on lines of their own, and a dialog with no buttons simply has no "Actions:" block.

    [Fact]
    public void DialogFormat_RendersTheBoxedTitleBodyAndNumberedButtons()
    {
        DialogSnapshot dialog = Dialog(buttons:
        [
            new DialogButtonInfo("Yes", null, "Custom", null, "example:yes"),
            new DialogButtonInfo("No", null, "Custom", null, "example:no"),
        ]);

        IReadOnlyList<string> lines = DialogCommand.FormatDialog(dialog, new Dictionary<string, string>());

        Assert.Equal(
            [
                DialogBorder,
                "     Choose",
                DialogBorder,
                "Pick one",
                "Actions:",
                "  [1] Yes (custom)",
                "  [2] No (custom)",
                string.Empty,
                DialogHelpHint,
                DialogBorder,
            ],
            lines);
    }

    [Fact]
    public void DialogFormat_StagedValueOverridesTheInitialValue()
    {
        DialogSnapshot dialog = Dialog(inputs:
        [
            new DialogInputInfo("name", "Text", "Your name", "Steve", [], null, null),
        ]);

        IReadOnlyList<string> lines = DialogCommand.FormatDialog(
            dialog, new Dictionary<string, string> { ["name"] = "Alex" });

        // dialog.render.input is " {0} ({1}) {2} = {3} [{4}]"; a text field describes as nothing, so the bracket is empty (DialogFormatter.cs:37, :79-89).
        Assert.Contains("  name (Text) Your name = Alex []", lines);
        Assert.DoesNotContain("  name (Text) Your name = Steve []", lines);
    }

    [Fact]
    public void DialogFormat_SingleOptionInput_ListsItsOptions()
    {
        DialogSnapshot dialog = Dialog(inputs:
        [
            new DialogInputInfo(
                "pick", "SingleOption", "Pick", "a",
                [new DialogOptionInfo("a", "Apple", true), new DialogOptionInfo("b", "Pear", false)],
                null, null),
        ]);

        IReadOnlyList<string> lines = DialogCommand.FormatDialog(dialog, new Dictionary<string, string>());

        // Legacy lists the option IDS inside the input's own description (dialog.input_desc_options, "options: {0}", DialogFormatter.cs:85-86); there is no line per option.
        Assert.Contains("  pick (Options) Pick = a [options: a, b]", lines);
        Assert.Equal(1, lines.Count(static line => line.StartsWith("  pick", StringComparison.Ordinal)));
    }

    [Fact]
    public void DialogFormat_RegistryReference_SaysTheBodyIsUnavailable()
    {
        // A dialog is open but only as a registry index: "cannot render" must not read as "no dialog".
        var dialog = new DialogSnapshot(
            string.Empty, string.Empty, null, true, [], [], [], HasExitAction: false, RegistryId: 12);

        IReadOnlyList<string> lines = DialogCommand.FormatDialog(dialog, new Dictionary<string, string>());

        Assert.Equal(
            [
                DialogBorder,
                "     Unresolved dialog 12",
                DialogBorder,
                "The server referenced dialog registry entry 12, but MCC has no data for it.",
                string.Empty,
                DialogHelpHint,
                DialogBorder,
            ],
            lines);

        // And it still does not read as "no dialog" (corpus key dialog.none).
        Assert.DoesNotContain("No custom dialog is active.", lines);
    }

    [Fact]
    public void DialogFormat_NoButtons_OmitsTheActionsBlock()
    {
        IReadOnlyList<string> lines = DialogCommand.FormatDialog(Dialog(), new Dictionary<string, string>());

        Assert.Equal(
            [DialogBorder, "     Choose", DialogBorder, "Pick one", string.Empty, DialogHelpHint, DialogBorder],
            lines);
        Assert.DoesNotContain("Actions:", lines);
    }

    [Theory]
    [InlineData("dialog")]
    [InlineData("dialog show")]
    [InlineData("dialog open")]
    [InlineData("dialog click 1")]
    [InlineData("dialog click-label Yes")]
    [InlineData("dialog set answer yes")]
    [InlineData("dialog input answer yes")]
    [InlineData("dialog cancel")]
    [InlineData("dialog dismiss")]
    public async Task Dialog_Subcommands_AreRegisteredAndGateOnSession(string line)
    {
        // Every subcommand parses (never NotRun) and stops at the session gate rather than the old blanket "not tracked in this build" reply.
        //
        // "dialog select <n>" and "dialog action <id>" were never legacy verbs: legacy's Dialog.cs never registered them, and legacy's button numbering is 1-based (Arguments.Integer(min: 1), so "dialog click 0" is now correctly rejected).
        // The legacy grammar this pins is show/open/set/input/click/click-label/cancel/dismiss (MinecraftClient/Commands/Dialog.cs).
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(line);
        Assert.NotEqual(CmdStatus.NotRun, result.Status);
        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(NotConnected, result.Message);
    }

    [Theory]
    [InlineData("dialog select 1")]
    [InlineData("dialog action example:confirm")]
    [InlineData("dialog click 0")]
    public async Task Dialog_NewClientOnlyForms_AreGone(string line)
    {
        // The other half: these three no longer parse.
        // They are answered HERE, with the command's usage, rather than forwarded to the server.
        //
        // The expectation moved with the incomplete-command fix (see IncompleteCommandTests).
        // It used to assert NotRun, i.e. "hand the line to the server", which is what MCC did with every misuse of a command it owns, and which is the defect the user hit on `bed`: the client that owns `dialog` said nothing and the SERVER answered for it.
        // Legacy answered these itself (Archive/MinecraftClient/McClient.cs:1107-1119).
        // What is being pinned is unchanged: these three forms do not run.
        // Only who says so changed.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("dialog ", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogUnsupportedText_NamesTheVersionRequirement()
    {
        // The stale text claimed the build did not track dialogs; it now states the real era gate.
        Assert.DoesNotContain("this build", CommandStrings.DialogUnsupported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.21.6", CommandStrings.DialogUnsupported, StringComparison.Ordinal);
        Assert.Equal(771, DialogApi.FirstDialogProtocol);
    }

    private sealed class TestHost(ICommandOutput? output) : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public Umpk.Auth.IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput { get; } = output;

        public IHostUi? Ui => null;
    }
    #endregion
}

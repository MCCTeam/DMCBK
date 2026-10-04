using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>book</c> command: read, write, edit or sign the book held in the main hand.
/// Ported from the legacy <c>MinecraftClient/Commands/Book.cs</c>, including its <c>read [page]</c>, <c>write text|file</c>, <c>edit</c> / <c>edit page|insert|delete</c> and <c>sign</c> grammar, its writable-book preconditions, and its per-protocol page/title limit validation.
/// <para>
/// Reading decodes the held book through the era-neutral book-content view (<see cref="ItemStackInfo.Book"/>), which covers both the 1.20.5+ structured components and the pre-1.20.5 NBT.
/// Writing and signing go through the edit-book action and replace the pages wholesale, the same way legacy's <c>McClient.SendBookEdit</c> did.
/// The editor is a host UI hook (<see cref="IHostUi.TryOpenBookEditor"/>), which is this stack's <c>BookTuiHost</c>.
/// </para>
/// </summary>
public sealed class BookCommand : CommandBase
{
    /// <summary>The page separator legacy split a written page run on (Book.cs:16).</summary>
    private const char PageDelimiter = '\f';

    /// <inheritdoc/>
    public override string CmdName => "book";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.book.desc");

    /// <inheritdoc/>
    public override string CmdUsage => McStrings.Get("cmd.book.usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("read [page]", "read the book you are holding"),
        new("write <text>", "replace the contents"),
        new("write file <path>", "write from a file"),
        new("edit page <n> <text>", "replace one page"),
        new("edit append [text]", "add a page"),
        new("edit insert <n> [text]", "insert a page"),
        new("edit delete <n>", "remove a page"),
        new("sign <title>", "sign it, making it permanent"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["book read", "book write Hello world", "book sign My Diary"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy "help book read|write|edit|sign" each rendered their own one-line help (Book.cs:24-30).
        help.ThenLiteral("read", h => h.Executes(ctx => ctx.Source.Result.Ok(McStrings.Get("cmd.book.help_read"))))
            .ThenLiteral("write", h => h.Executes(ctx => ctx.Source.Result.Ok(McStrings.Get("cmd.book.help_write"))))
            .ThenLiteral("edit", h => h.Executes(ctx => ctx.Source.Result.Ok(McStrings.Get("cmd.book.help_edit"))))
            .ThenLiteral("sign", h => h.Executes(ctx => ctx.Source.Result.Ok(McStrings.Get("cmd.book.help_sign"))));

        builder.Literal(CmdName, l => l
            // Bare `book` reports what is in your hand rather than failing to parse.
            // It is the first thing anyone types and the answer they actually want; the help page is still one `help book` away.
            .Executes(ctx => DescribeHeld(ctx.Source))
            .ThenLiteral("read", h => h
                .Executes(ctx => ReadBook(ctx.Source, null))
                .ThenArgument("Page", Arguments.Integer(1, int.MaxValue), a => a
                    .Suggests(SuggestExistingPages)
                    .Executes(ctx => ReadBook(ctx.Source, ctx.GetArgument<int>("Page")))))
            .ThenLiteral("write", h => h
                .ThenLiteral("file", a => a
                    .ThenArgument("Path", Arguments.GreedyString(), b => b
                        .Suggests(SuggestPaths)
                        .Executes(ctx => WriteBookFromFile(ctx.Source, ctx.GetArgument<string>("Path")))))
                // `write text <text>` was the only spelling; `write <text>` is the one people reach for, and both land here.
                // The `file` literal above is matched first, so a book whose first word is literally "file" is the one case that needs the explicit `write text`.
                .ThenLiteral("text", a => a
                    .ThenArgument("Text", Arguments.GreedyString(), b => b
                        .Executes(ctx => WriteBook(ctx.Source, ctx.GetArgument<string>("Text")))))
                .ThenArgument("Text", Arguments.GreedyString(), a => a
                    .Executes(ctx => WriteBook(ctx.Source, ctx.GetArgument<string>("Text")))))
            .ThenLiteral("edit", h => h
                .Executes(ctx => OpenEditor(ctx.Source))
                .ThenLiteral("page", a => a
                    .ThenArgument("Page", Arguments.Integer(1, int.MaxValue), b => b
                        .Suggests(SuggestExistingPages)
                        .ThenArgument("Text", Arguments.GreedyString(), c => c
                            .Executes(ctx => EditPage(
                                ctx.Source, ctx.GetArgument<int>("Page"), ctx.GetArgument<string>("Text"))))))
                // Appending is what "add a page" means, and it needed a whole `insert <n> <text>` with the right n before.
                // Text is optional here and on insert: a blank page is a real thing to want, and demanding text for one is why `book edit insert 2` failed with a parse error.
                .ThenLiteral("append", a => a
                    .Executes(ctx => AppendPage(ctx.Source, string.Empty))
                    .ThenArgument("Text", Arguments.GreedyString(), b => b
                        .Executes(ctx => AppendPage(ctx.Source, ctx.GetArgument<string>("Text")))))
                .ThenLiteral("insert", a => a
                    .ThenArgument("Page", Arguments.Integer(1, int.MaxValue), b => b
                        .Suggests(SuggestInsertPositions)
                        .Executes(ctx => InsertPage(ctx.Source, ctx.GetArgument<int>("Page"), string.Empty))
                        .ThenArgument("Text", Arguments.GreedyString(), c => c
                            .Executes(ctx => InsertPage(
                                ctx.Source, ctx.GetArgument<int>("Page"), ctx.GetArgument<string>("Text"))))))
                .ThenLiteral("delete", a => a
                    .ThenArgument("Page", Arguments.Integer(1, int.MaxValue), b => b
                        .Suggests(SuggestExistingPages)
                        .Executes(ctx => DeletePage(ctx.Source, ctx.GetArgument<int>("Page"))))))
            .ThenLiteral("sign", h => h
                // No completion for the title: signing only applies to a WRITABLE book, and a writable book has no title to offer.
                // Verified live rather than assumed.
                .ThenArgument("Title", Arguments.GreedyString(), a => a
                    .Executes(ctx => SignBook(ctx.Source, ctx.GetArgument<string>("Title")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders a decoded book the way legacy <c>FormatBook</c> did (Book.cs:273-298): the title/author header for a signed book or the writable-book header, then either the one requested page or every page, each behind its <c>Page n/N</c> header.
    /// A page number outside the book's range renders as the out-of-range line alone, exactly as legacy did.
    /// </summary>
    /// <param name="book">The decoded book content.</param>
    /// <param name="page">The 1-based page to render, or null for the whole book.</param>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    public static IReadOnlyList<string> FormatBook(BookContentInfo book, int? page = null)
    {
        ArgumentNullException.ThrowIfNull(book);

        StringBuilder sb = new();
        sb.Append(book.IsSigned
                ? McStrings.Format("cmd.book.header_signed", book.Title ?? string.Empty, book.Author ?? string.Empty)
                : McStrings.Get("cmd.book.header_writable"))
            .Append('\n');

        if (page is not null)
        {
            int index = page.Value - 1;
            if (index < 0 || index >= book.Pages.Count)
                // The same sentence the edit verbs use, minus the "here is how to add one" hint: reading is not the moment to be told how to create the page.
                return [McStrings.Format("cmd.book.no_such_page", book.Pages.Count, page.Value)];

            sb.Append(McStrings.Format("cmd.book.page_header", page.Value, book.Pages.Count)).Append('\n');
            sb.Append(book.Pages[index]);
            return sb.ToString().Split('\n');
        }

        for (int i = 0; i < book.Pages.Count; i++)
        {
            sb.Append(McStrings.Format("cmd.book.page_header", i + 1, book.Pages.Count)).Append('\n');
            sb.Append(book.Pages[i]).Append('\n');
        }

        return sb.ToString().TrimEnd().Split('\n');
    }

    /// <summary>
    /// The port of legacy <c>ReadBook</c> (Book.cs:77-91): inventory gate, "are you holding a book at all", then the host book view when no explicit page was asked for, else the text rendering.
    /// </summary>
    private int ReadBook(CommandContext ctx, int? page)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        if (!TryGetHeldBookContent(ctx, out BookContentInfo content))
            return ctx.Result.Fail(McStrings.Get("cmd.book.not_holding_book"));

        // Legacy opened the book TUI read-ONLY here (BookTuiHost.TryOpen(..., editable: false), Book.cs:86).
        // BookEditorRequest carries a single "signed means read-only" flag rather than legacy's separate editable/signed pair, so reading asks for the read-only view with it.
        if (page is null &&
            ctx.Ui?.TryOpenBookEditor(new BookEditorRequest(content.Pages, content.Title, Signed: true)) == true)
            return ctx.Result.Ok(McStrings.Get("cmd.book.editor_opened"));

        foreach (string line in FormatBook(content, page))
            ctx.Output.WriteLine(line);

        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>The port of legacy <c>OpenEditor</c> (Book.cs:93-102).</summary>
    private int OpenEditor(CommandContext ctx)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out BookContentInfo content, out int failure))
            return failure;

        return ctx.Ui?.TryOpenBookEditor(new BookEditorRequest(content.Pages, content.Title, Signed: false)) == true
            ? ctx.Result.Ok(McStrings.Get("cmd.book.editor_opened"))
            : ctx.Result.Fail(McStrings.Get("cmd.book.tui_required"));
    }

    /// <summary>The port of legacy <c>WriteBook</c> (Book.cs:104-117), reporting what it wrote.</summary>
    private static int WriteBook(CommandContext ctx, string text)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out _, out int failure))
            return failure;

        IReadOnlyList<string> pages = SplitPages(text);
        return Validate(ctx, pages, title: null, out failure)
            ? SendEdit(ctx, pages, title: null, McStrings.Format("cmd.book.written", pages.Count))
            : failure;
    }

    /// <summary>
    /// The port of legacy <c>WriteBookFromFile</c> (Book.cs:119-125).
    /// The file check comes BEFORE the inventory/writable gate, exactly as legacy ordered it.
    /// </summary>
    private static int WriteBookFromFile(CommandContext ctx, string path)
    {
        if (!File.Exists(path))
            return ctx.Result.Fail(McStrings.Format("cmd.book.file_not_found", path));

        return WriteBook(ctx, File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// The port of legacy <c>EditPage</c> (Book.cs:127-144).
    /// Replacing a page that does not exist now says how to CREATE one instead of only saying the number was wrong: "Page 2 is outside the current page range 1-1" is a true statement that leaves the user no better off, and the thing they wanted was always one command away.
    /// </summary>
    private static int EditPage(CommandContext ctx, int page, string text)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out BookContentInfo content, out int failure))
            return failure;

        List<string> pages = [.. content.Pages];
        if (page > pages.Count)
            return ctx.Result.Fail(NoSuchPage(ctx, page, pages.Count));

        pages[page - 1] = DecodeInlineText(text);
        return Validate(ctx, pages, title: null, out failure)
            ? SendEdit(ctx, pages, title: null, McStrings.Format("cmd.book.page_updated", page, pages.Count))
            : failure;
    }

    /// <summary>
    /// Adds a page at the end.
    /// This is what "add a page" means to anyone who has not read the grammar, and it did not exist: the only way to grow a book was <c>edit insert</c> with the correct next page number, and getting that number wrong answered with a range error.
    /// </summary>
    private static int AppendPage(CommandContext ctx, string text)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out BookContentInfo content, out int failure))
            return failure;

        List<string> pages = [.. content.Pages];
        pages.Add(DecodeInlineText(text));
        return Validate(ctx, pages, title: null, out failure)
            ? SendEdit(ctx, pages, title: null, McStrings.Format("cmd.book.page_appended", pages.Count, pages.Count))
            : failure;
    }

    /// <summary>The port of legacy <c>InsertPage</c> (Book.cs:146-163).</summary>
    private static int InsertPage(CommandContext ctx, int page, string text)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out BookContentInfo content, out int failure))
            return failure;

        List<string> pages = [.. content.Pages];
        if (page > pages.Count + 1)
            return ctx.Result.Fail(NoSuchPage(ctx, page, pages.Count));

        pages.Insert(page - 1, DecodeInlineText(text));
        return Validate(ctx, pages, title: null, out failure)
            ? SendEdit(ctx, pages, title: null, McStrings.Format("cmd.book.page_inserted", page, pages.Count))
            : failure;
    }

    /// <summary>The port of legacy <c>DeletePage</c> (Book.cs:165-185).</summary>
    private static int DeletePage(CommandContext ctx, int page)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.cannot_edit_signed"), out BookContentInfo content, out int failure))
            return failure;

        List<string> pages = [.. content.Pages];
        if (page > pages.Count)
            // No add-a-page hint here: the request was to REMOVE one, and advising how to create it is advice for a different intention.
            return ctx.Result.Fail(McStrings.Format("cmd.book.no_such_page", pages.Count, page));

        pages.RemoveAt(page - 1);
        if (pages.Count == 0)
            pages.Add(string.Empty);

        return Validate(ctx, pages, title: null, out failure)
            ? SendEdit(ctx, pages, title: null, McStrings.Format("cmd.book.page_deleted", page, pages.Count))
            : failure;
    }

    /// <summary>The port of legacy <c>SignBook</c> (Book.cs:187-200): sign the pages the book already has.</summary>
    private static int SignBook(CommandContext ctx, string title)
    {
        if (!EnsureWritable(ctx, McStrings.Get("cmd.book.already_signed"), out BookContentInfo content, out int failure))
            return failure;

        string normalizedTitle = title.Trim();
        return Validate(ctx, content.Pages, normalizedTitle, out failure)
            ? SendEdit(ctx, content.Pages, normalizedTitle, McStrings.Format("cmd.book.signed", normalizedTitle))
            : failure;
    }

    /// <summary>
    /// Sends the edit-book action for the held slot, the port of legacy <c>McClient.SendBookEdit</c> (McClient.cs:1953-1968) including its page normalization.
    /// A version that cannot carry a book edit reports the same "could not be sent" line legacy used for every unsent edit.
    /// </summary>
    private static int SendEdit(CommandContext ctx, IReadOnlyList<string> pages, string? title, string sentMessage)
    {
        IReadOnlyList<string> normalized = NormalizePages(pages);
        int slot = ctx.Run(ct => ctx.Game.Inventory.GetHeldSlotAsync(ct));
        try
        {
            return ctx.Run(ct => ctx.Game.Inventory.EditBookAsync(slot, normalized, title, ct))
                ? ctx.Result.Ok(sentMessage)
                : ctx.Result.Fail(McStrings.Get("cmd.book.update_failed"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(McStrings.Get("cmd.book.update_failed"));
        }
    }

    /// <summary>
    /// The port of legacy <c>EnsureWritable</c> (Book.cs:211-232): inventory gate, then "the main hand holds a book and quill", telling a signed book apart from no book at all.
    /// </summary>
    private static bool EnsureWritable(
        CommandContext ctx, string signedBookMessage, out BookContentInfo content, out int failure)
    {
        content = EmptyWritable;
        if (!ctx.InventoryEnabled)
        {
            failure = ctx.Result.Set(CmdStatus.FailNeedInventory);
            return false;
        }

        if (!ctx.InSession)
        {
            failure = ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));
            return false;
        }

        ItemStackInfo? held = ctx.Run(ct => ctx.Game.Player.GetHeldItemAsync(ct));
        if (!IsWritableBook(held) || !TryReadBook(held, out content))
        {
            failure = ctx.Result.Fail(WritableFailureMessage(held, signedBookMessage));
            return false;
        }

        failure = 0;
        return true;
    }

    /// <summary>The port of legacy <c>GetWritableBookFailureMessage</c> (Book.cs:227-232).</summary>
    private static string WritableFailureMessage(ItemStackInfo? item, string signedBookMessage)
        => TryReadBook(item, out BookContentInfo content) && content.IsSigned
            ? signedBookMessage
            : McStrings.Get("cmd.book.not_holding_writable");

    /// <summary>
    /// The answer to a page number that is not there: what the book actually holds, followed by the two commands that would create the page.
    /// The hint is the whole point.
    /// The previous wording ("Page 2 is outside the current page range 1-1") is accurate and leaves the reader to guess that <c>edit insert</c> exists, which is exactly what happened when this was reported.
    /// </summary>
    private static string NoSuchPage(CommandContext ctx, int page, int pageCount)
    {
        char prefix = ctx.Commands.NoPrefix ? ' ' : ctx.Commands.Prefix;
        string prefixText = ctx.Commands.NoPrefix ? string.Empty : prefix.ToString();
        return pageCount == 0
            ? McStrings.Format("cmd.book.book_is_empty", prefixText)
            : McStrings.Format("cmd.book.no_such_page", pageCount, page)
                + " "
                + McStrings.Format("cmd.book.add_page_hint", prefixText, Math.Min(page, pageCount + 1));
    }

    /// <summary>
    /// What bare <c>book</c> answers: the book in your hand and how many pages it has.
    /// Typing the command name on its own used to be a parse error; the thing a reader wants at that moment is what they are holding, and the grammar is still one <c>help book</c> away.
    /// </summary>
    private int DescribeHeld(CommandContext ctx)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        if (!TryGetHeldBookContent(ctx, out BookContentInfo content))
            return ctx.Result.Fail(McStrings.Get("cmd.book.not_holding_book"));

        string kind = content.IsSigned
            ? McStrings.Format("cmd.book.header_signed", content.Title ?? string.Empty, content.Author ?? string.Empty)
            : McStrings.Get("cmd.book.header_writable");

        return ctx.Result.Ok(McStrings.Format("cmd.book.holding", kind, content.Pages.Count));
    }

    #region Completion
    //
    // Page numbers are completed from the book actually in hand, so tab offers 1..N for the verbs that address an existing page and 1..N+1 for insert, whose extra position is the end of the book.
    // Nothing here can know the pages without asking the session, so each provider reads the held item; the read is cheap and only runs while the user is typing.

    /// <summary>Completes a page number with the pages the held book has.</summary>
    private static ValueTask SuggestExistingPages(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
        => SuggestPageNumbers(ctx, sink, extra: 0);

    /// <summary>Completes an insert position: every existing page, plus the slot after the last one.</summary>
    private static ValueTask SuggestInsertPositions(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
        => SuggestPageNumbers(ctx, sink, extra: 1);

    private static ValueTask SuggestPageNumbers(ICommandContext<CommandContext> ctx, ISuggestionSink sink, int extra)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sink);

        if (!TryPeekHeldBook(ctx.Source, out BookContentInfo content))
            return ValueTask.CompletedTask;

        // Capped so a pathological book cannot flood the popup; the tail is reachable by typing anyway.
        int last = Math.Min(content.Pages.Count + extra, MaxSuggestedPages);
        for (int page = 1; page <= last; page++)
        {
            string text = page.ToString(CultureInfo.InvariantCulture);
            if (text.StartsWith(sink.Remaining, StringComparison.Ordinal))
                sink.Suggest(text);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Completes a filesystem path for <c>write file</c>, one directory at a time, the way a shell does.
    /// </summary>
    private static ValueTask SuggestPaths(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        string typed = sink.Remaining;
        string directory = Path.GetDirectoryName(typed) is { Length: > 0 } dir ? dir : ".";
        string stem = Path.GetFileName(typed);

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(entry);
                if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isDirectory = Directory.Exists(entry);
                string candidate = Path.GetDirectoryName(typed) is { Length: > 0 }
                    ? Path.Combine(Path.GetDirectoryName(typed)!, name)
                    : name;

                sink.Suggest(isDirectory ? candidate + Path.DirectorySeparatorChar : candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path the process cannot list suggests nothing; it is not an error worth reporting mid-keystroke.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The largest page number offered as a completion candidate.</summary>
    private const int MaxSuggestedPages = 100;

    /// <summary>
    /// Reads the held book for a COMPLETION, which must never throw and never block on a session that is not there: a suggestion runs while the user is mid-keystroke, and the honest answer to "no session" or "not holding a book" is to offer nothing.
    /// </summary>
    private static bool TryPeekHeldBook(CommandContext ctx, out BookContentInfo content)
    {
        content = EmptyWritable;
        if (!ctx.InventoryEnabled || !ctx.InSession)
            return false;

        try
        {
            return TryGetHeldBookContent(ctx, out content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads the book content of the item in the main hand (legacy <c>TryGetHeldBookContent</c>).</summary>
    private static bool TryGetHeldBookContent(CommandContext ctx, out BookContentInfo content)
        => TryReadBook(ctx.Run(ct => ctx.Game.Player.GetHeldItemAsync(ct)), out content);

    /// <summary>
    /// The port of legacy <c>BookContentHelper.TryRead</c> (BookContent.cs:53-67): only a writable or written book has content, and a book whose content the server has not sent still reads as an empty book of its own kind rather than as "not a book".
    /// </summary>
    private static bool TryReadBook(ItemStackInfo? item, out BookContentInfo content)
    {
        content = EmptyWritable;
        if (item is null || item.IsEmpty)
            return false;

        bool writable = IsWritableBook(item);
        if (!writable && !IsWrittenBook(item))
            return false;

        content = item.Book is { } book
            ? NormalizeBook(book)
            : new BookContentInfo(!writable, null, null, 0, false, [string.Empty]);
        return true;
    }

    /// <summary>The empty book and quill legacy handed back for an unreadable item (BookContent.cs:45).</summary>
    private static BookContentInfo EmptyWritable { get; } = new(false, null, null, 0, false, [string.Empty]);

    /// <summary>True when the stack is a book and quill (legacy <c>IsWritableBook</c>).</summary>
    private static bool IsWritableBook(ItemStackInfo? item)
        => item is { IsEmpty: false } && BarePath(item.ItemId).Equals("writable_book", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the stack is a signed written book.</summary>
    private static bool IsWrittenBook(ItemStackInfo? item)
        => item is { IsEmpty: false } && BarePath(item.ItemId).Equals("written_book", StringComparison.OrdinalIgnoreCase);

    /// <summary>The path part of a namespaced id (<c>minecraft:writable_book</c> to <c>writable_book</c>).</summary>
    private static string BarePath(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon < 0 ? id : id[(colon + 1)..];
    }

    /// <summary>The port of legacy <c>BookContentHelper.NormalizePages</c> (BookContent.cs:90-94).</summary>
    private static IReadOnlyList<string> NormalizePages(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0)
            return [string.Empty];

        return pages;
    }

    /// <summary>A book whose page list went through <see cref="NormalizePages"/>, so a blank book has one page.</summary>
    private static BookContentInfo NormalizeBook(BookContentInfo book)
        => book.Pages.Count == 0 ? (book with { Pages = [string.Empty] }) : book;

    /// <summary>The port of legacy <c>SplitPages</c> (Book.cs:234-237).</summary>
    private static IReadOnlyList<string> SplitPages(string text)
        => NormalizePages(DecodeInlineText(text).Split(PageDelimiter));

    /// <summary>The port of legacy <c>DecodeInlineText</c> (Book.cs:239-243): typed <c>\f</c> and <c>\n</c>.</summary>
    private static string DecodeInlineText(string text)
        => text.Replace("\\f", PageDelimiter.ToString(), StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// The port of legacy <c>Validate</c> (Book.cs:245-271): the server-enforced page count, per-page length and title length for the negotiated protocol, refused before anything is sent.
    /// </summary>
    private static bool Validate(CommandContext ctx, IReadOnlyList<string> pages, string? title, out int failure)
    {
        BookLimits limits = BookLimits.ForProtocol(ctx.Run(ct => ctx.Game.Session.GetInfoAsync(ct)).Protocol);

        if (pages.Count > limits.MaxPages)
        {
            failure = ctx.Result.Fail(McStrings.Format("cmd.book.too_many_pages", pages.Count, limits.MaxPages));
            return false;
        }

        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i].Length > limits.MaxPageLength)
            {
                failure = ctx.Result.Fail(
                    McStrings.Format("cmd.book.page_too_long", i + 1, pages[i].Length, limits.MaxPageLength));
                return false;
            }
        }

        if (title is not null && (title.Length == 0 || title.Length > limits.MaxTitleLength))
        {
            failure = ctx.Result.Fail(McStrings.Format("cmd.book.title_invalid", limits.MaxTitleLength));
            return false;
        }

        failure = 0;
        return true;
    }
    #endregion
}

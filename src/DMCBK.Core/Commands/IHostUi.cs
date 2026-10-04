using DMCBK.Core.Presentation;
using Umpk.Client.Actions;

namespace DMCBK.Core.Commands;

/// <summary>
/// Optional host UI hooks a command consults before falling back to text.
/// Every member has a text-fallback default so a host without a rich UI (the classic CLI has none) can leave them unimplemented: the <c>TryOpen*</c> hooks return <c>false</c> (the command then prints text through <see cref="ICommandOutput"/>), and the chunk viewport size is null (the command uses its own default grid).
/// Core exposes data; the host renders.
/// </summary>
public interface IHostUi
{
    /// <summary>Formats a player status snapshot for this host; null selects the portable localized text.</summary>
    string? FormatPlayerStatus(PlayerStatus status, GlyphSet glyphs) => null;

    /// <summary>Formats completion progress for a document; null selects the portable numeric percentage.</summary>
    string? FormatProgress(double fraction, GlyphSet glyphs) => null;

    /// <summary>Returns a host-specific inventory layout diagram, or null when this host does not draw one.</summary>
    string? GetInventoryLayout(string? menuType, bool playerInventory) => null;

    /// <summary>Presents an immutable RGB image; false reports that this host has no image capability.</summary>
    bool TryPresentImage(ImagePresentationRequest request) => false;

    /// <summary>Formats registered command metadata; null selects portable plain text.</summary>
    string? FormatCommandIndex(IReadOnlyList<CommandBase> commands, string prefix, GlyphSet glyphs) => null;

    /// <summary>Formats command usage metadata; null selects portable plain text.</summary>
    string? FormatCommandHelp(CommandHelpPresentation request, GlyphSet glyphs) => null;

    /// <summary>Presents a chunk snapshot; false selects the portable status summary.</summary>
    bool TryPresentChunkMap(ChunkMapPresentation request, GlyphSet glyphs) => false;

    /// <summary>The width, in chunk columns, the host wants the <c>chunk status</c> map sized to; null uses a default.</summary>
    int? ChunkViewportColumns => null;

    /// <summary>The height, in chunk columns, the host wants the <c>chunk status</c> map sized to; null uses a default.</summary>
    int? ChunkViewportRows => null;

    /// <summary>Opens the host's book editor for the request; false means the host has none (text fallback).</summary>
    bool TryOpenBookEditor(BookEditorRequest request) => false;

    /// <summary>Opens the host's container view for the window; false means the host has none (text fallback).</summary>
    bool TryOpenContainerView(int windowId) => false;

    /// <summary>
    /// Closes the host's container view for <paramref name="windowId"/>, or every open container view when it is null.
    /// A no-op for a host with no such view, and for a view showing some other window.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="TryOpenContainerView"/>, and needed because a container view can outlive the window it shows through no action of the user's: the server closes the window, or the whole session hops to another server (a proxy's backend switch), and the view is then showing something that no longer exists.
    /// The legacy client had exactly this seam (<c>InventoryTuiHost.NotifyInventoryClosed</c>, called from <c>McClient.OnInventoryClose</c>).
    /// <para>
    /// Closing this way must NOT send a close-window packet: the window is already gone, and on a switch the frame would land on a different server entirely.
    /// </para>
    /// </remarks>
    void CloseContainerView(int? windowId = null)
    {
    }

    /// <summary>Shows the host's tab-list overlay; false means the host has none (text fallback).</summary>
    bool TryShowTabOverlay() => false;

    /// <summary>Opens the host's live scoreboard window; false means the host has no visual scoreboard.</summary>
    bool TryOpenScoreboard() => false;

    /// <summary>
    /// Shows the host's view of a live server dialog; false means the host has none (text fallback).
    /// The request carries the decoded dialog, so the host renders the real title/body/inputs/buttons and never re-reads state itself.
    /// </summary>
    bool TryShowDialog(DialogViewRequest request) => false;

    /// <summary>
    /// Renders a Markdown document (a <c>/man</c> page) the way this host renders documents, and returns true when it did.
    /// False means the host has no document renderer and the caller falls back to printing the source as plain text through <see cref="ICommandOutput"/>.
    /// </summary>
    /// <remarks>
    /// The core hands over MARKDOWN, not rendered lines, because the two hosts need different things from the same page and neither answer can be produced in a console-free core.
    /// The classic host turns it into ANSI through Spectre; the TUI parses it into Consolonia controls, since it paints its own cells and would show ANSI escapes as literal characters.
    /// Anything embedding DMCBK.Core and implementing nothing still gets a readable page.
    /// <para>
    /// This is only ever called for MCC's own manual.
    /// Chat from the server is a Minecraft component tree and travels an entirely different path; it must never be treated as Markdown.
    /// </para>
    /// </remarks>
    bool TryWriteDocument(string markdown) => false;

    /// <summary>
    /// Opens the host's visual plugin manager (list, detail, settings editor, install, updates, marketplaces); false means the host has none and the caller falls back to the text verbs.
    /// </summary>
    bool TryOpenPluginManager() => false;

    /// <summary>Opens the host's script dashboard and source editor.</summary>
    bool TryOpenScriptManager() => false;

    /// <summary>Opens the shared command/manual browser on the requested tab.</summary>
    bool TryOpenCommandBrowser(CommandBrowserTab initialTab) => false;

    /// <summary>Opens the host's recipe-book browser.</summary>
    bool TryOpenRecipeBrowser() => false;

    /// <summary>Opens the host's tracked-entity browser.</summary>
    bool TryOpenEntityBrowser() => false;

    /// <summary>Opens the host's advancement browser.</summary>
    bool TryOpenAdvancementsBrowser() => false;

    /// <summary>Opens the host's live chunk-status map.</summary>
    bool TryOpenChunkBrowser() => false;

    /// <summary>
    /// Surfaces a plugin-raised notification through the host (console beep, highlighted write, TUI toast).
    /// Returns false when the host offers no notification channel, so the caller falls back to logging the text.
    /// This is the R26/R27 host seam the notifier-class plugins use instead of reaching for the console.
    /// </summary>
    bool TryNotify(HostNotification notification) => false;
}

/// <summary>The initial tab of the shared command and manual browser.</summary>
public enum CommandBrowserTab
{
    /// <summary>The live registered-command index.</summary>
    Commands,

    /// <summary>The live manual-topic catalogue.</summary>
    Manual,
}

/// <summary>A plugin-raised notification for the host to surface.</summary>
/// <param name="Message">The already-localized, already-rendered notification text.</param>
/// <param name="Beep">Whether the host should emit an audible alert if it can.</param>
/// <param name="Highlight">An optional substring within <paramref name="Message"/> the host may emphasize.</param>
public sealed record HostNotification(string Message, bool Beep = false, string? Highlight = null);

/// <summary>The data a <c>book edit</c> hands the host book editor.</summary>
/// <param name="Pages">The current book pages (plain text, one entry per page).</param>
/// <param name="Title">The current title, when the book is signed/titled.</param>
/// <param name="Signed">Whether the held book is already signed (read-only).</param>
public sealed record BookEditorRequest(IReadOnlyList<string> Pages, string? Title, bool Signed);

/// <summary>The data a <c>dialog show</c> hands the host dialog view.</summary>
/// <param name="Dialog">The decoded dialog the server is showing.</param>
/// <param name="Values">The input values staged so far, keyed by input key; empty when none were staged.</param>
public sealed record DialogViewRequest(DialogSnapshot Dialog, IReadOnlyDictionary<string, string> Values);

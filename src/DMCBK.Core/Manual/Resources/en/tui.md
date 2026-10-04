# TUI mode

Set `ConsoleMode = "tui"` in `console.toml`. The TUI needs a real terminal, so redirected output
always falls back to classic.

## Startup and login

Startup happens inside the TUI: the welcome lines land in the log pane, first-run login is a
dialog (pick a method, fill the fields, press a button), and online sign-in steps (the Microsoft
code, the browser-code paste, Yggdrasil credentials) are dialogs too. Picking Microsoft signs in
immediately: the link/code dialog appears at selection, and the later connect resumes the cached
session with no second prompt. The Microsoft dialog shows the link and the code big, with Open in
browser and Copy code buttons that use the platform default browser and clipboard.
Nothing is typed on a plain console first. Sub-dialogs have a Back button; Escape cancels a dialog.

## Layout

A scrolling log, a status bar, and an input line. The status bar carries health, food, XP progress
and active effects, with a Menu button at the right. `Ctrl+M` opens the same main menu from the main
TUI surface. Chat and log lines can be selected with the pointer and copied.

## Overlays

Some commands open a pane instead of printing:

    /tab           live player list
    /scoreboard ui movable live objectives and scores
    /inventory     clickable container view
    /book          book editor
    /dialog        server dialog with its real buttons
    /minimap       movable terrain and entity window
    /maps          fullscreen view of a map item
    /mcc-menu      main management launcher
    /help ui       commands and manual browser, Commands tab
    /man ui        commands and manual browser, Manual tab
    /scripts ui    script workbench and editor
    /recipebook ui unlocked recipe browser
    /entity ui     tracked entity browser
    /achievement ui advancement progress browser
    /chunk ui      responsive loaded-chunk browser

Overlays close with Escape. The minimap and scoreboard are floating tool windows: drag a title bar to move it.
The scoreboard starts on the right edge, vertically centred, and lists every objective because the public
snapshot does not identify the server-selected sidebar slot. The minimap lets you
drag its frame or use `Alt+S` to resize it, and use the title-bar maximize control or `Alt+R` to
expand or restore it. Its close button and `/minimap off` both hide it.

## Management workspaces

`/mcc-menu` opens the main TUI launcher. It groups the command and manual browser, Script Workbench,
player inventory, player list, scoreboard, minimap, recipe, entity, advancement, and chunk browsers,
the installed-plugin manager, Marketplace management, the server picker, and clean client exit in one place.
Entries use the same command gates as their direct commands,
so a live-session tool explains when a connection or gameplay feature is required instead of opening
a broken view.

Management browsers fill the terminal. Top-level workspaces show Close without a redundant Back
button. Back appears after opening a nested page, or after opening details on a narrow screen.
Compact forms and confirmations stay centred and only wrap their content. `Ctrl+F` focuses search,
`F5` refreshes live indexes, arrows move selection, Enter opens details, and Escape returns from a
detail or nested form before it closes the workspace. Closing cancels refresh work and restores
focus to the command input.

At 100 columns or wider, the list and detail appear side by side. Narrow terminals use list then
detail navigation. Lists and detail panes own their vertical scrolling instead of scrolling the
whole workspace, and wrapped detail text avoids unnecessary horizontal scrolling.

The command browser searches names, aliases, descriptions, syntax, and examples from the live
command registry. It refreshes after plugins or scripts add or remove commands. Insert command puts
the active-prefix command into the input for review; it never executes it. The Manual tab searches
topic ids, summaries, and page contents, including plugin topics.

The advancement browser groups the hierarchy by scope, reports total completion, and filters All,
Unlocked, or Locked. On protocol versions where structured advancement packets are not decoded, it
shows an unavailable-data explanation rather than saying the account has no advancements. The alias
`/advancements ui` opens the same browser.

## Minimap

`console.toml` `[Minimap]`:

    Enabled = false
    Zoom = 1              blocks per pixel, 1-16
    Width = 40
    Height = 20           uses half-block characters
    Position = "top_right"
    CaveMode = false
    ShowPlayerNames = true
    ShowHostileNames = true
    ShowNeutralNames = true
    ShowPassiveNames = true

`Position` chooses the window's starting anchor. `/minimap position ...` moves it back to an anchor;
dragging the title bar then places it freely for the rest of the run. Closing the window disables the
minimap, and `/minimap on` opens it again. Name labels are enabled by default for every living entity
category. Use `/minimap names <category> off` or the matching `console.toml` setting to hide a category.

## Manual pages here

`/man` renders into the TUI's own controls rather than writing escape sequences, because the TUI
paints its own cells and would show the escapes as literal characters. Same pages, same Markdown,
different renderer.

## Differences from classic

- `/clear-console` clears the log pane, not the terminal.
- The suggestion popup is drawn by the TUI, so `[CommandSuggestion]` colours do not apply.
- Scrollback is `TuiLogScrollback` lines, default 3000, not your terminal's buffer.

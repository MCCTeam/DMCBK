# Inventory

## Windows

Every open container is a window with an id. Your own inventory is window 0 and is always open.

    /inventory                  list open windows
    /inventory player list      what you are carrying
    /inventory container list   what is in the open container
    /inventory 3 list           by window id

`player` and `container` abbreviate to `p` and `c`.

## Slots

Slot numbers are the protocol's, not a friendly numbering. In your own inventory:

    0        crafting result
    1-4      crafting grid
    5-8      armour
    9-35     main inventory
    36-44    hotbar
    45       off hand

`/inventory player list` prints the grid with the numbers on it, which is easier than counting.

## Acting

    /inventory player click 36          left click a slot
    /inventory player click 36 right    right click
    /inventory player drop 36           drop one
    /inventory player drop 36 all       drop the stack
    /changeslot 3                       select hotbar slot 3
    /changeslot diamond_pickaxe         select whatever slot holds this
    /dropitem cobblestone 64            drop by name

## Crafting

`/recipebook list` shows what you have unlocked. `/recipebook craft <recipe>` places one into an open
crafting menu; `craftall` places as many as fit. You still have to take the result out.

In TUI mode, `/recipebook ui` opens a searchable browser for named and numeric recipe entries. The
detail pane explains when the current protocol cannot send that recipe form or when no compatible
menu is open. Craft one and Craft all use the same placement operation as the text command. The
inventory overlay also has a Recipes button and keeps the current crafting window open while you
browse. If the server sent recipe additions the client cannot decode, the browser says that the
visible list is incomplete instead of claiming that no recipes exist.

## Anvils and enchanting tables

`/nameitem <name>` renames the item in an open anvil. `/enchant top|middle|bottom` clicks an option
in an open enchanting table. Both need the window open first, usually via `/useblock`.

## Configuration

`[Gameplay] Inventory` in `client.toml`. With it off, every command here refuses and says so.

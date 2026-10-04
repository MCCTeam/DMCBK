# Entities

## Finding them

    /entity                 everything nearby
    /entity near zombie     one type, nearby
    /entity 21339           one entity by id

Each row carries the id, the type, the position, and for players the name and latency.

Entity ids are the server's and are not stable across reconnects. Use a type when you can.

## Acting

    /entity 21339 attack
    /entity zombie attack
    /entity 21339 use

`attack` swings at it, but only when you can see it: ahead of you and not behind blocks. A
target you cannot see is refused unless the run opts out with `-a`
(`/entity zombie attack -a`). `use` right-clicks it regardless of visibility, which is how you
shear a sheep, open a villager trade or put a lead on something.

A type instead of an id acts on the nearest match.

## TUI browser

`/entity ui` opens the tracked-entity browser. Search by id, type, player name, or custom name;
filter by Player, Hostile, Neutral, Passive, or Other; and sort by distance, name, or type. The
detail pane refreshes twice per second and shows position, rotation, velocity, equipment, effects,
passengers, vehicle, and carried item when the server supplied them. Selection follows the network
id across refreshes. A vanished entity remains visible briefly as `No longer tracked`, with actions
disabled.

The browser deliberately exposes only single-target Use and Attack. Attack applies the same facing
and line-of-sight checks as the safe text-command path. The browser never offers the `-a` override or
bulk actions.

## Players

    /list             who is online
    /tab              the tab list, with ping and gamemode
    /teams            scoreboard teams and members
    /scoreboard       scoreboard objectives and their scores
    /scoreboard ui    live movable scoreboard window (TUI)

`/scoreboard` lists every objective with its entry count. `/scoreboard kills` prints
the top 15 scores for one objective, highest first, and `--all` prints the rest.
In TUI mode `/tab` opens a live overlay instead of printing once. `/scoreboard ui` opens a live,
resizable window anchored at the right side and vertically centred. The snapshot does not identify
the server-selected sidebar slot, so the window lists every objective instead of guessing which one
the vanilla sidebar would show. Drag its title bar to move it; Escape closes it. The other
`/scoreboard` forms keep their text output in both modes.

## Configuration

`[Gameplay] Entity` in `client.toml`. Turning it off saves memory and bandwidth on a busy server, at
the cost of every command on this page.

> [!NOTE]
> A tab-list entry carries a `listed` flag from 1.19.3 onward. `/list` shows what a vanilla client
> would show, which is not always every connected player: servers hide staff and NPCs this way.

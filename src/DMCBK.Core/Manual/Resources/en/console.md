# Console

`console.toml` belongs to the host, not to the client, which is why it is a separate file and why it
loads first.

## Colour

    ConsoleColorMode = "vt100_24bit"

Also `vt100_8bit`, `vt100_4bit`, `legacy_4bit`, `disable`. If you see garbage like `←[0m`, your
terminal is not interpreting escapes: use `legacy_4bit`, or `disable`.

## Glyphs

    Glyphs = "auto"

`auto` looks at the output encoding and `TERM` and picks emoji or ASCII. Force it with `"emoji"` or
`"ascii"`.

This one setting drives every status glyph, the chunk map squares, the plugin list markers, and the
table borders in `/man` pages, so they degrade together instead of leaving clean glyphs inside a
table made of characters your terminal cannot draw.

| Role | Emoji | ASCII |
| --- | --- | --- |
| Success | check | `[+]` |
| Failure | cross | `[x]` |
| Blocked by a gate | no entry | `[-]` |
| Not ready yet | hourglass | `[~]` |
| Warning | warning | `[!]` |

It replaced `[Gameplay] EnableEmoji`, which was in the wrong file: whether a terminal can draw an
emoji is not a property of the world.

## Input

    DisplayInput = true            echo what you type
    HistoryInputRecords = 32       how much history to keep
    Timestamps = false             prefix printed lines with the time

`[CommandSuggestion]` controls the completion popup, including its colours, which only apply in
`vt100_24bit`. `UseBasicArrow = true` if the arrows render as boxes.

## Modes

    ConsoleMode = "classic"

`classic` is a scrolling terminal. `tui` is a full-screen interface. See `/man tui`.

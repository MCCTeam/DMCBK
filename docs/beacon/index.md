# Beacon scripting

Beacon is DMCBK's automation language. A script can react to chat, read game state, run timed work and call plugin functions. Each client has its own runtime.

You can test calculations without a Minecraft server. Game actions need a live session and the corresponding client features.

| Start here | What it covers |
| --- | --- |
| [First script](getting-started.md) | Files, headers, host composition and offline execution |
| [Language](language.md) | Values, expressions, blocks, functions, tasks and errors |
| [Events and game APIs](events-and-game.md) | Event fields, timers, chat, inventory, world, movement and dialogs |
| [State and integrations](state-and-integrations.md) | Settings, persistence, imports, exports, files, network and plugins |
| [Host APIs and testing](hosting-and-testing.md) | Discovery, editing, diagnostics, reload, REPL and test boundaries |

The examples target Beacon syntax version `1`, DMCBK `0.1.0-preview.1` and UMPK `0.9.0-beta.4`.

Beacon scripts and C# plugins serve different needs. Scripts express automation directly. Plugins add compiled behavior and reusable services through the [plugin SDK](../plugins/index.md).

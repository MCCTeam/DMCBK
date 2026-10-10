# Beacon scripting

Beacon is DMCBK's automation language. A script can react to chat, read game state, run timed work and call plugin functions. Each client has its own runtime.

You can test calculations without a Minecraft server. Game actions need a live session and the corresponding client features.

| Start here | What it covers |
| --- | --- |
| [Chaptered beginner guide](guide/index.md) | Nine chapters with complete files, expected results, and repeatable checks |
| [First script](getting-started.md) | Files, headers, host composition and offline execution |
| [Recipes from MCC](recipes.md) | Complete practical scripts and a simulated event test |
| [Language](language.md) | Values, expressions, blocks, functions, tasks and errors |
| [Events and game APIs](events-and-game.md) | Event fields, timers, chat, inventory, world, movement and dialogs |
| [State and integrations](state-and-integrations.md) | Settings, persistence, imports, exports, files, network and plugins |
| [Host APIs and testing](hosting-and-testing.md) | Discovery, editing, diagnostics, reload, REPL and test boundaries |

The examples target Beacon syntax version `1`, DMCBK `0.1.0-preview.5` and UMPK `0.9.0-beta.6`.

Beacon scripts and C# plugins serve different needs. Scripts express automation directly. Plugins add compiled behavior and reusable services through the [plugin SDK](../plugins/index.md).

## What runs when

Loading executes top-level statements. It also registers event handlers, timers, commands, and exports. A successful load does not prove that each registered body can complete.

An event handler runs later when an event matches. A command body runs when the dispatcher invokes it. A timer runs when the host advances the scheduler. Top-level code should not assume that login already completed.

## Terms used in these guides

| Term | Meaning |
| --- | --- |
| Script ID | The name used by the runtime, normally the filename without `.bcn` |
| Host | The application that supplies IO, connection state, paths, and presentation |
| Engine | One interpreter coordinator, event bus, shared state store, and scheduler environment |
| Session | One connection to a Minecraft server |
| Capability | A named operation available to a script or required by its header |
| Tracking | Client collection of inventory, terrain, entity, or other game observations |
| Diagnostic | A report that identifies a source or execution problem |
| Test double | A simulated host that records actions instead of contacting a server |

The host and the Minecraft server are separate programs. The host can accept a request that the server later refuses. Inspect both the local result and the observed server result for game actions.

## Agent skills

Install the [Beacon scripts authoring skill](https://github.com/MCCTeam/MCC-Skills/tree/master/skills/beacon-scripting) for your coding agent:

```bash
npx skills add MCCTeam/MCC-Skills --skill beacon-scripting
```

The skill includes standalone references and examples. Read [agent skill installation](../agent-skills.md) for agent selection and installation scope.

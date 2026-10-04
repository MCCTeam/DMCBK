# Events and game APIs

Event handlers run when the host delivers an event. They can filter its fields and apply a cooldown before performing work.

## React to chat

```beacon
# beacon 1
# needs: chat.send
on chat as e when e.message contains "!help"
  whisper e.player "Available commands: !help and !rules."
end on
on join
  say "Welcome, {player}!"
end on
```

Without `as e`, the handler uses the name `event` for its record. Event fields also work as bare names inside handlers. They take precedence over globals.

`join` and `leave` follow the server tab list. They do not describe every entity entering render distance.

| Event | Useful fields or behavior |
| --- | --- |
| `chat` | `player`, `message`, `raw`, `is_private` |
| `whisper` | `player`, `message`, `raw` |
| `server_message` | `text`, `translation_key` |
| `raw_chat` | Untouched `raw`, plus `category`, `sender`, `body`, `verified` and protocol metadata |
| `player_list` | `players`, `count`, `header`, `footer` |
| `container_open` | `window`, `title`, `kind` |
| `container_close` | `window` |
| `entity_add`, `entity_remove` | Entity identity, type, name, position, distance and pose |
| `dialog` | `title`, `body`, `inputs`, `buttons`, `input_keys`, `registry_id` |
| `start` | Script loaded |
| `login`, `logout`, `disconnect`, `reconnect` | Connection lifecycle |
| `death`, `respawn`, `health`, `hunger`, `inventory`, `kick`, `tps` | Game or session changes |

`raw_chat` observes messages alongside parsed dispatch. It never suppresses them. `stop event` only suppresses hooks that support suppression.

Entity events require `entity.read`. The first entity poll establishes a baseline. It does not emit additions for every already-tracked entity.

## Timers

```beacon
# beacon 1
in 5 seconds do
  show "One-shot timer"
end in
every 60 seconds
  show "Recurring timer"
end every
```

A one-shot body runs once. Reconnect cancels pending one-shots and waits. A missed recurring tick runs at most once before cadence resumes.

A handler can use `cooldown 300 seconds named "warning"`. The cooldown limits repeated handling, independently of chat throttling.

## Send the right type of output

| Operation | Capability | Behavior |
| --- | --- | --- |
| `show "text"` | None | Local script output |
| `say "text"` | `chat.send` | Public chat, without a leading slash |
| `whisper "Steve" "text"` | `chat.send` | Private message |
| `server "/home"` | `server.send` | Server command, with a leading slash |
| `set result to mcc "help"` | `mcc.run` | Internal command output as text |
| `disconnect "Finished"` | `server.disconnect` | Leave the server and stop automatic reconnect |

1. Use `server` for server commands.
2. Use `show` for multiline internal-command output.
3. Add delays when sending repeated messages.

All scripts share a chat allowance of eight messages per ten seconds. Excess messages queue and produce warnings. `chat_bucket()` exposes the current bucket state.

## Read and act on game state

| Area | Examples | Requirements |
| --- | --- | --- |
| Player | `me.health`, `me.food`, `me.pos`, `me.effects` | A session for live values |
| Server | `server.tps`, `server.protocol`, `server.score("kills", "Alice")` | Tracked server state |
| Inventory reads | `inv.list`, `inv.count`, `inv.has`, `inv.container` | `inventory.read`, inventory tracking |
| Inventory actions | `inv.select`, `inv.drop`, `inv.move`, `inv.take`, `inv.put` | `inventory.write` and negotiated action support |
| World reads | `world.block_at`, `world.sign_text` | `world.read`, terrain tracking |
| World search | `world.find_blocks`, `world.find_signs` | `world.search`, terrain tracking |
| World actions | `world.dig`, `world.place`, `world.use`, `world.looking_at` | `world.write`, gameplay gates and mutation allowance |
| Entity reads | `entities.near`, `entities.by_id`, `entities.nearest` | `entity.read`, entity tracking |
| Entity actions | `attack`, `interact`, entity following | `entity.write`, action support |
| Dialogs | `dialog.show`, `dialog.answer`, `dialog.close` | `dialog.read` or `dialog.write` |

Capabilities do not enable tracking or make an unsupported protocol action available. Missing game prerequisites can still cause a catchable failure.

`world.find_blocks("chest", 16, 10)` finds nearby tracked blocks. Searches cap radius at 32 blocks and results at 64.

`entities.near(radius, max)` returns tracked rows, nearest first. Radius defaults to 64 and caps at 128. Results cap at 64.

Entity IDs last for one session. Do not persist an ID for use after reconnect.

## Movement and containers

`move_goto(120, 65, -40, {tolerance: 2, sneak: no})` finishes on arrival. `move_follow("Steve")` continues until cancellation.

The newest movement request wins. The previous request receives a superseded error. `stop_moving()` cancels steering.

1. Open a container before trading or enchanting.
2. Wait for `container_open` before reading its slots.
3. Catch failures if the window closes during an action.

`trade.list()` reads offers. `trade.buy(index, count)` returns completed units. `enchant.options()` reads choices. `enchant.choose("top")` selects one.

## Server dialogs

1. Match a stable input key in the `dialog` event.
2. Use `dialog.answer` to submit values and press a button in one operation.
3. Keep passwords out of chat and local output.

Button numbers start at one. Titles can change with translation. Input keys provide a more stable filter.

## Execution limits

Event work receives 100,000 fuel units and a five-second wall-clock budget. The runtime limits function call depth to 256.

World mutations have a per-script allowance of eight actions per ten seconds. A refusal reports a retry delay. A successful local action is not proof that a server accepted the result.

Next: [State and integrations](state-and-integrations.md).

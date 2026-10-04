# Scripts

Beacon scripts automate the client in plain words. Each script is one UTF-8 `.mcc` file in the
`scripts/` folder next to your configurations, with its own globals. The first line is mandatory:

    # beacon 1

A missing header, or a newer major version, fails closed with a pointer instead of guessing.

## Your first script

Save this as `scripts/welcome.mcc`:

    # beacon 1
    # needs: chat.send

    on join:
      say "Welcome to the server, {player}!"
    end on

    on chat when message contains "!rules":
      whisper player "1. Be kind. 2. No griefing. 3. Have fun."
    end on

Then run `/scripts run welcome` in the client. That is the whole onboarding.

## Commands

    /scripts ui            open the script workbench in TUI mode
    /scripts list          every script found, and its state
    /scripts run welcome   load and start a script
    /scripts stop welcome  stop one script
    /scripts stop all      the panic button, stops every script
    /scripts reload        reload every running script
    /scripts lint shop     check a script without running it
    /scripts format shop   normalize layout and print the diff (`--check` for a dry run)
    /scripts new shop      scaffold a working script from a template
    /scripts mute on       gag script chat while the logic keeps running
    /scripts mute off      let script chat through again
    /scripts repl          evaluate one line against the live session
    /scripts watch on      reload running scripts when their files change
    /scripts config shop   show a script declared settings, or set one key

`new` knows four templates: `welcome`, `guard`, `shop`, and `empty`. `mute on` stays visible in
the status line, so a quiet bot never looks like a dead one. `watch` is opt-in and debounced;
manual `reload` always works whether watching or not.

## TUI workbench

`/scripts ui` opens every top-level `.mcc` file in one workspace and marks it Running or
Stopped. Run or stop the selected script, reload it, lint it, preview a format diff, edit its
declared settings, or open a persistent REPL with optional seeded randomness and a locals view.
Watch and Mute stay visible on the dashboard. Delete removes the selected `.mcc` source only after
confirmation and stops it when it is running. Script settings and saved state are kept.

The REPL has a multiline input area on the left and a split results side for output and locals.
Each text area scrolls independently. On a narrow terminal the two bounded panes stack vertically.
Press `Ctrl+Enter` to evaluate and `Ctrl+Up` or `Ctrl+Down` to move through input history.

The editor is multiline. `Ctrl+S` lints before writing: errors block the save and selecting a
diagnostic moves the caret to its source line; warnings do not block it. Writes replace the file
atomically. If another program changed the file after it was opened, choose Reload, Overwrite, or
Cancel. Formatting changes only the editor buffer until you save. Closing a dirty editor asks for
Save, Discard, or Cancel. When Watch is off, saving a running script reports that a reload is still
needed.

## Values

Six kinds cover everything: text, number, yes/no, list, map, none. There is exactly one way to
assign:

    set name to "Steve"
    set health to 20

`=` never assigns. Writing `set x = 5` errors with the fix. Comparisons spell out `is` and
`is not` (`==` and `!=` parse as quiet aliases). Text interpolation uses braces and takes any
expression: `say "Online: {online_count + 1}"`. Write a literal brace as `{{` or `}}`.

Only `no` and `none` are false. `0`, empty text, and empty lists are all true, and conditions
require an actual yes/no value, so `if food` is rejected with a concrete comparison suggested.
The one exception keeps defaults readable: `or` doubles as the default operator, so
`saved("seen") or {}` yields the saved map, or a fresh one when nothing was saved yet.

## Talking

Five verbs reach the outside world, plus `show` for thinking out loud locally:

    say "Hello everyone!"
    whisper "Steve" "Meet at spawn?"
    server "/home"
    disconnect "mule run done"
    set answer to mcc "scripts list"
    show "would say: Running scripts: {answer}"

`say` with a leading slash warns and sends nothing. `server` requires the leading slash. `disconnect` (reason optional) leaves the server cleanly and stays out: auto-reconnect stops and `on disconnect` still fires; the reason is a local note. `mcc`
runs an MCC internal command, named without its slash (`scripts list`), and returns its
output text; a failing `mcc` call raises a
catchable error. Branch on the answer (`contains`, `len`) and never `say` it raw:
command output can carry newlines, which the chat gate refuses instead of risking a
server kick. `show` prints locally, needs no capability, and never touches chat.

A line starting with `/` is a syntax error that suggests the right verb, so a bare `/home`
copied from chat never teleports anyone by accident.

## Control flow

Blocks open with words and close with labeled ends:

    if health < 10 then
      say "I need food!"
    else if food < 6 then
      eat()
    else
      say "All good."
    end if

    repeat 3 times
      say "Still here!"
    end repeat

    while food < 20
      eat()
      wait 2 seconds
    end while

    for each p in online_players
      whisper p "The event starts in 5 minutes."
      wait 1 second
    end for

`stop` breaks out, `skip` continues. `wait` yields the current task and never freezes the
session; the minimum quantum is 100 ms, so `wait 0 seconds` in a loop cannot spin.

Failures get `try`, with optional cleanup in `finally` that runs either way:

    try
      server "/home"
    catch err
      say "Could not go home: {err.message}"
    finally
      show "home attempt settled"
    end try

Block reads, moves during lag spikes, and failing `mcc` calls raise catchable errors with
`.message`, `.code`, and `.line` instead of killing an overnight bot. As a rule, every call
that touches the session (`world.*`, inventory writes, `move_*`, `http_*`, `mcc`, `server`,
`extern`, `call`) raises catchably on failure; pure helpers (`text`, `len`, `pick`, `min`)
only raise on wrong argument kinds.

## Functions and tasks

    function greet(name)
      return "Hello, {name}!"
    end function

    say greet("Steve")

Locals stay local; globals are per script. Background work is explicit: `start patrol()`
returns a task id, `await id` waits for it, `tasks()` lists live tasks, `cancel task id`
stops one. Movement calls are tasks too: `move_goto` completes on arrival and takes
coordinates or an entity row (`move_goto(120, 65, -40)`, `move_goto(v)`), `move_follow`
tracks a player by name (or any tracked entity by id or `entities.*` row) until cancelled,
and the newest movement request wins while the loser gets a
catchable superseded error. `stop_moving()` cancels steering. `move_goto` takes an options
map for tolerance and posture (`move_goto(120, 65, -40, {tolerance: 2, sneak: no})`);
unknown option names fail closed naming the option.

One-shot work uses `in`, the middle ground between `wait` (inside a handler) and `every`
(forever):

    in 30 seconds do
      say "Auction ends in 5 minutes."
    end in

A fired body never refires. Pending one-shots cancel on reconnect, exactly like pending
`wait` sleeps, and a reload re-arms them from the fresh source.

## Events

    on chat as e when e.message contains "!help"
      whisper e.player "Try !rules, !spawn, !shop."
    end on

`as e` is optional; without it the record is called `event`. Inside a handler, event fields
are also in scope as bare names (`message`, `player`), resolving to event fields before
globals. Filters are ordinary boolean expressions. Busy handlers take a throttle:

    on tps cooldown 300 seconds named "tps-warn" when tps < 15
      say "Heads up: server TPS is {tps}."
    end on

Stopping a message from reaching the server spells out `stop event`. Known hooks: `chat`
(player, message, raw, is_private), `whisper` (player, message, raw), `server_message`
(text, translation_key), `raw_chat` (raw, category, sender, sender_id, chat_type, target,
body, translation_key, verified), `join`, `leave`, `death`, `respawn`, `health`, `hunger`,
`inventory`, `login`, `logout`, `disconnect`, `reconnect`, `kick`, `tps`, `start`,
`player_list` (tab-list churn: `players`, `count`, `header`, `footer`), `container_open`
(`window`, `title`, `kind`), `container_close` (`window`), `entity_add` and
`entity_remove` (render-distance tracking: `id`, `uuid`, `type`, `name`, `custom_name`,
`is_player`, `x`, `y`, `z`, `distance`, `yaw`, `pitch`, `pose`, `on_ground`), `dialog`
(server dialog: `title`, `body`, `inputs`, `buttons`, `input_keys`, `registry_id`),
plus any custom event a loaded
plugin registers. All hooks take the same throttle clause (`cooldown 60 seconds named "x"`).

`join`/`leave` follow the tab list; `entity_add`/`entity_remove` follow what the client
actually tracks. The first poll only sets the baseline, so nothing fires for entities
already there at load; on busy servers pair these hooks with a `when` filter and a
`cooldown`.

`chat.raw` is the parsed-view trimmed line; `raw_chat.raw` is the untouched line.
`raw_chat` fires alongside the parsed dispatch and never suppresses. `whisper.raw`
is the full trimmed plain line.

Lifecycle hooks: `start` (loaded), `login`, `logout`, `disconnect`, `reconnect`. Scheduler
blocks run on a cadence:

    every 60 seconds
      if online_count > 0
        say "Tip: use !help to see what I can do."
      end if
    end every

After a reconnect, `login` and `reconnect` refire, pending sleeps are cancelled, and a missed
`every` tick runs at most once before the normal cadence resumes.

## State

`saved` persists across restarts as TOML under `configurations/beacon/`:

    set warns to saved("warns") or {}
    set warns[player] to (warns[player] or 0) + 1
    save "warns" to warns

`shared` is RAM-only cross-script state with namespaced keys, and `lock shared` serializes a
read-modify-write so two handlers cannot interleave a counter.

A script can declare typed settings with commented defaults in its header:

    # setting thirst = 5 ; seconds between sips

The first load writes `configurations/beacon/<id>.settings.toml` with those defaults and
comments; editing the file (or `/scripts config <id> key value`) overlays new values, and
`settings.thirst` reads the resolved value. Unknown keys fail closed naming the key.

## Capabilities

A script gets chat, movement, and reads. Files and network are granted in the header, and the
loader refuses when the header and the code disagree (the refusal prints the exact line to
paste). Optional integrations ride as `# wants:` and warn instead of refusing:

    # needs: chat.send inventory.read world.read
    # wants: econ.read

Chat is throttled by one global bucket shared across scripts: burst 8 per 10 seconds, overflow
queues then warns naming the script, never silently dropped. `chat_bucket()` reports the
bucket status as a map, so a bot can back off before the throttle bites. Event dispatches get 100k fuel
plus 5 seconds of wall clock; file IO stays under the script `data/` folder; network fetches
need `net.fetch` plus an allowlist entry in `beacon.toml`, HTTPS only.

Block search needs `world.search`; digging, placing, opening containers, and the targeting
read need `world.write`, which additionally honors the gameplay gates and a per-script
allowance of 8 mutations per 10 seconds (past it the verb refuses catchably with the retry
delay). `world.use` needs the write cap because it actuates the world: it sends a use
interaction like `world.place` does, even though it reads a container back. The targeting read
`world.looking_at` is capped the same way for the same reason. Nearby-entity reads and the
`entity_add`/`entity_remove` hooks need `entity.read`; `attack`, `interact`, and entity
follows need `entity.write`. Villager trading and enchanting ride the open container
under `inventory.read`/`inventory.write`. Server dialogs read under `dialog.read` and
answer under `dialog.write`. Leaving the server with `disconnect` needs
`server.disconnect`.

## Libraries and plugins

One file stops being enough around week two:

    import "lib/econ.mcc" as econ
    say "Bread costs {econ.price("bread")} coins."

Paths resolve relative to the importing file, circular imports error naming the cycle, and
lint unions the library permissions into the report. Libraries contribute functions and
top-level `set` constants; their event and command blocks never register.

The C# bridge runs both directions. A script calls out through `extern`, naming a plugin id:

    extern price_of from "shop"
    say price_of("bread")

A missing provider fails the call catchably and names the plugin to install. Plugins can
also offer read-only variable namespaces that scripts read as maps: where
`extern balance from "econ"` calls a function, `coins.balance` reads a namespace a plugin
registered, snapshotted fresh on every read. Going the other
way, a script marks a function visible with `export`, and C# awaits it. A script can also
export a value table alongside its functions:

    export set prices to {apple: 5, bread: 3} Scripts call each
other with `call`, which runs on the caller fuel and raises a catchable error naming script,
function, and caller line when the target is missing:

    set report to call "shopkeeper.daily_report"()
    say report

Exported values read back through the same `call` spelling with no arguments
(`call "shopkeeper.prices"()`), and unload withdraws them with the functions.

Script variables surface under `vars.beacon.*`, shared with `%beacon_*%` console variables:
`set vars.beacon.coins to 5` is the same value as `%beacon_coins%`.

## Custom commands

A script can register a real command:

    # desc: Look up today's price.
    # example: /price bread
    command "/price <item>"
      set target to arg("item")
      say "{target} costs {price_of(target)} coins today."
    end command

The pattern needs its leading slash; each `<name>` is one word argument read with `arg`.
Doc comments above the block feed `/help`: `# desc:` becomes the description, each
`# example:` a copy-paste sample.

## Builtins

Identity and self: `me.name`, `me.health`, `me.max_health`, `me.food`, `me.saturation`,
`me.armor`, `me.air`, `me.xp_level`, `me.gamemode`, `me.pos` (a map with x, y, z), `me.yaw`,
`me.pitch`, `me.ping`, `me.effects`, `me.is_sneaking`. Bare `health`, `food`, `online_count`,
and `tps` read their namespaced twins outside handlers.

Server: `server.online_count`, `server.tps`, `server.mspt`, `server.ip`, `server.port`,
`server.version_name`, `server.protocol` (the game protocol number), `server.max_players`,
`server.motd`, `server.day_time`, `server.day`, `server.weather`, `server.difficulty`,
`server.scoreboard` (objectives with scores, plus teams), `server.bossbars`,
`online_players` (paged, `online_players(50)` takes the first page). `game.protocol` reads
the same protocol number without a session; `game.protocols` lists every protocol in the
dataset, so scripts gate version-specific behavior on numbers instead of version names.
Titles, subtitles, and action bars have no read: they never reach the chat path scripts
observe, so no builtin pretends otherwise.

Scoreboard reads come in two shapes. `server.scoreboard()` returns the whole board:
`objectives` maps each objective name to its display name and its `scores` map, and
`teams` maps each team name to its display name and members. A missing objective reads
as none, so check before digging:

    set board to server.scoreboard()
    if board.objectives.kills is set
      show board.objectives.kills.scores.Alice
    end if

`server.score("kills", "Alice")` skips the navigation and answers the number directly,
or none when the objective or the entry is unknown. `server.score("kills")` answers
the whole scores map for one objective. Names with spaces or punctuation need
brackets: `server.scoreboard().objectives["my obj"]`.

Time: `time.now`, `time.date`, `time.today`, `time.hour`, `time.minute`, `time.stamp`,
`time.format(stamp, "HH:mm")`, `time.ago(stamp)` ("3 minutes ago", localized).

Chat: `chat_history(n)` (capped at 200), `last_from(player)`, `count_matching(text, minutes)`.

Inventory: `inv.list`, `inv.count`, `inv.has`, `inv.find`, `inv.find_all`, `inv.selected`,
`inv.select` (hotbar slots 0-8 only; anything else is a catchable error), `inv.drop`, `inv.drop_stack`, `inv.move`, `inv.click`, `inv.armor`,
`inv.container` (the open container window, or none when only the player inventory is
open), `inv.take`, `inv.put`, `craft_list`, `craft_one`. A matcher is text (`"compass"`
matches type id or display name, case-insensitive, `minecraft:` prefix optional) or a map
(`{type: "compass", name_contains: "server"}`).

World: `world.block_at`, `world.light_at`, `world.biome_at`, `world.sign_text(x, y, z)`
(the joined sign lines, or none when no sign is tracked there, needs `world.read`),
`world.find_blocks("chest", 16, 10)` (nearest first, capped at 64 results inside a
32-block radius, needs `world.search`), `world.find_signs("Storage", 32, 5)` (signs whose
text contains the needle, case-insensitive, nearest first with x, y, z, and text per hit,
same caps as `find_blocks`, needs `world.search`), `world.dig`, `world.place`,
`world.use(x, y, z)` (uses the block and returns the opened container with window, title,
kind, and slots exactly like `inv.container`, or none when no window appears, needs
`world.write`), `world.looking_at` (need `world.write`), `look_at` (coordinates or a
row), `attack(target)`
(text, id, or row), `interact(target)` (right-click: villagers, shears, leads),
`use_in_hand`, `eat`. A chest run reads `world.find_signs("Storage", 32, 5)`, then
`world.sign_text`, then `world.use`, then the existing `inv.container().slots` plus
`inv.take(slot, 2)`.

Entities: `entities.near([radius[, max]])`, `entities.of_type(type[, radius[, max]])`,
`entities.by_id(id)`, `entities.nearest([matcher[, radius]])`,
`entities.count([matcher[, radius]])` (radius defaults to 64 and clamps at 128, rows cap
at 64 nearest-first; ids hold for one session only). `move_follow` takes a player name,
an id, or a row.

Trade and enchanting work the open container: `trade.list()` (index, first, second,
result, uses, max_uses, sold_out, xp), `trade.select(i)`, `trade.buy(i[, n])`
(answers completed units); `enchant.options()` (slot, level), `enchant.choose(slot |
"top" | "middle" | "bottom")`. Open the window first (`interact` a villager,
`world.use` a table) and wait for `on container_open`; a missing window raises
catchably.

Dialogs work wherever the server shows them, configuration included:
`dialog.show()` (the open dialog as a map, or none), `dialog.set(key, value)`
(stages one input), `dialog.click(1)` (presses a 1-based button number or a button
label, submitting the staged values), `dialog.answer({key: value}[, button])`
(stages and presses in one call), `dialog.close()`. Buttons are 1-based like the
`/dialog` command. Reading needs `dialog.read`, answering needs `dialog.write`.
Filter the hook on the input key, not the title, since titles get retranslated
and keys do not:

    # needs: dialog.read dialog.write
    # setting login_password = "change-me" ; only sent to the auth dialog below

    on dialog as d when d.input_keys contains "auth_login_password"
      dialog.answer({auth_login_password: settings.login_password}, 1)
    end on

`dialog.answer` is the call to reach for: a `set` followed by a separate `click`
can straddle a dialog change, while the single call cannot. Never `say` or `show`
a password value.

Text and data: `len`, `lower`, `upper`, `trim`, `trim_start`, `trim_end`, `split`,
`join`, `slice`, `replace`, `replace_first`, `index_of`, `pad_start`, `pad_end`,
`repeat_str`, `escape_regex`, `sort`, `reverse`, `unique`, `keys`, `values`, `has_key`,
`text`, `number`, `arg`, `match`, `match_all`, `json_parse`, `json_stringify`,
`random`, `pick`, `chance`, `min`, `max`, `clamp`, `round`, `abs`, `log`.
`index_of` returns the 0-based position or none when absent. `match_all` returns a list
of per-hit maps in the same shape as `match`. `repeat_str` refuses past 10000 output chars.
`sort` needs an all-numbers or all-text list and never mutates in place;
`unique` dedupes keeping first-seen order.

Tests are one builtin plus a convention: `assert(health > 0, "bot survived")` returns yes
when the condition holds and raises a catchable error naming the label when it does not.
Keep check blocks in their own files and drive them with headless `run`; a failing assert
exits nonzero, so CI can gate on it.

Game tables: `items.totem_of_undying` gives the full type id, with `effects.*` and
`enchants.*` alongside, all tracking the session version. Unknown keys suggest neighbors.

Network and files are gated rooms: `http_get`, `http_post`, `file_read`, `file_write`. The
library versions separately from the syntax: `beacon.lib` reports the runtime version.

## Checking scripts

`/scripts lint` chases imports, unions permissions, and fails closed on missing manifest
entries. `/scripts format` normalizes layout (end labels, indent, quotes, trailing
whitespace) and prints the diff before writing. The same engines drive headless `lint`,
`format`, and `run`, so interactive and headless output never disagree. `--strict` escalates unresolvable `extern`/`call` targets and missing providers
from warnings to errors. `--fix` applies only the safe mechanical rewrites with a diff first:
bare `end` label completion, the forgiven `cancel event` spelling, two-space indent
normalization, and pasted smart-quote normalization. Nothing that touches meaning is ever
edited.

Headless `run` executes scripts end to end without a client, on the inert host with a
virtual clock and a seeded RNG:

    mcc run quiz.mcc --seed 42 --tick 60 --format text

Exit codes mirror `lint`: 0 clean (warnings allowed), 1 script errors, 2 usage. `--tick`
advances virtual seconds after load so one-shot timers fire; `--seed` replays random draws
bit-for-bit; `--format json` emits the agent-scriptable report; `--stdin` runs piped
source instead of files.

Errors read like a person explaining, with file, line, column, the source line, what was
expected, and one paste-ready fix.

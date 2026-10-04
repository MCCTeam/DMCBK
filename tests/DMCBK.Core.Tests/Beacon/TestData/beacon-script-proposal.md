# Beacon: a scripting language for MCC 2.0

Status: proposal. Date: 2026-09-08. Author: MCC scripting design pass.
Audience: anyone who has to approve, implement, or write scripts in the new language.

## Why this exists

MCC 1.x had two scripting doors and both were half stuck. The `.txt` door was simple: a linear list of `send`, `wait`, `exit` lines, one command per tick, no branches or loops except through `execif` and `%vars%` tricks (see `Archive/MinecraftClient/ChatBots/Script.cs:200-282`). Anything beyond a greeting macro meant leaving the door entirely. The `.cs` door was the opposite: full C# compiled with Roslyn at load time, a `//MCCScript 1.0` header gate, `//using` and `//dll` directives, and the whole `ChatBot` API with its forty-some event hooks (`Archive/MinecraftClient/Scripting/ChatBot.cs`, `Archive/MinecraftClient/Scripting/CSharpRunner.cs:32-126`). Powerful, but it asked a twelve-year-old who wants an auto-welcome bot to learn C#, threading (the API is not thread-safe, work must hop with `InvokeOnMainThread`), and a fragile file format where one missing header line rejects the script.

The 2.0 refactor ported neither. That is actually good news. It gives us room to build the middle layer that was always missing: a language as easy to start as the old text scripts, with a ceiling high enough that most C# scripts never need C#.

I am calling it Beacon. Short to type, easy to say on a voice call, and it fits the fantasy: something you place once and it keeps working for everyone in range. Files end in `.mcc` and live in a `scripts/` folder next to the configurations.

## What I read before writing this

Three strands went into this proposal, and I want them visible so nobody has to take the design on faith.

First, the archived client itself. The facts that shaped the most decisions: scripts were dispatched per tick with `sleepticks` countdowns, variables lived in a dual string/object store with `%name%` expansion (`Archive/MinecraftClient/Settings.cs:1349-1538`), scheduling lived outside the language in `ScriptScheduler` with login/time/interval triggers, and C# scripts ran on their own thread while sampling `Thread.Sleep`, which fought the session loop. Any new design has to answer threading, scheduling, and variables natively instead of bolting them on.

Second, fresh web research I ran for this proposal through the Tavily research API (three reports, September 2026, full JSON kept in scratch). The findings that changed my mind on specific points:

- Conservative truthiness wins for beginners. Only `false` and `none`/`nil` are falsy; `0` and `""` stay truthy, and emptiness gets explicit checks. Lua and Elixir both do this, and the report recommends it outright because Python-style falsy `0`/`""` keeps biting novices in exactly the hungry/health checks a game bot lives on.
- Words beat symbols on day one. Lua, Python, and GDScript all spell out `and`/`or`/`not`, and Elixir keeps both forms but pays for it with documented confusion. Default to words.
- End markers beat significant whitespace when there is no editor doing autoindent. Python and GDScript assume an editor; MCC scripts get edited in Notepad and pasted from Discord where whitespace arrives mangled. Explicit closers survive that.
- Interpolation should be native, not a library call. Lua's lack of it is a documented beginner tax; Elixir's `#{...}` and f-strings are the model.
- Gradual everything: dynamic by default, stricter checks opt-in later. Luau's non-intrusive checker and GDScript's optional typing are the pattern. Errors get a beginner mode with a fix suggestion plus an advanced toggle.
- Sandboxes that survive contact with players all share the same skeleton: a small API surface with an allowlist, isolated globals per script, a watchdog (ComputerCraft kills a coroutine after 7 seconds without yield; Luau caps at 64 KB of instructions per block and 1 M per module), a cooperative `wait` that yields instead of blocking, and chat throttles with real numbers (Roblox documents 5-30 second chat cooldowns and a 1 minute floor for user text fields; HTTP capped around 500 calls/minute in game).
- The English-like event DSL already won this exact contest once. Skript's `on chat:` / `on join:` with inline guards, `%player%` interpolation, `{home::%uuid%}` per-player namespaced variables, and plain verbs (`send`, `give`, `set`) is the closest thing to proof that non-programmers will automate Minecraft in sentences. mIRC contributes the access-level prefix idea, Streamer.bot contributes typed trigger filters and persisted globals, AutoHotkey contributes hotstring-style triggers and throttle helpers. Skript's failure mode is also documented: the English stretches until parsing turns to guesswork, and then errors degrade to "can't understand this". Keep the English skin over a small formal grammar.

Third, I put a first draft through three hostile reviews: a beginner-UX reviewer, an implementer/security reviewer, and a power-user reviewer. Every one of them found real holes. The critics table near the end lists what they broke and what changed because of it. The short version: one syntax per idea, no bare-slash statements, strict booleans, a mandatory version header and capability manifest, and an honest power ceiling with C# interop instead of pretending the DSL does everything.

## Design principles

Eight rules. Everything below traces back to one of these.

1. One idea, one spelling. If two syntaxes do the same thing, tutorials will mix them and helpers on Discord will contradict each other. The parser may forgive, the docs never teach two ways.
2. Read like a sentence, parse like a grammar. English on the surface, a small closed set of keywords underneath. Nothing the parser cannot point at.
3. The first script takes five minutes. Zero config, one folder, one command to run, a starter template that already works.
4. No silent behavior. No truthy traps, no quiet coercion, no swallowed chat. If the runtime holds your message back, it tells you why and what to do.
5. Time is explicit. `wait` yields, never blocks. Long jobs are tasks you can name and cancel. Reconnects cancel pending sleeps instead of firing them into a dead session.
6. Least power by default. A script gets chat, movement, and reads. Files and network are granted entries in a manifest, scoped to a directory and a host list, or the loader says no.
7. Beginners get fixes, experts get spans. Every error shows what you wrote, what was expected, and one paste-ready correction. Power users can ask for the full trace.
8. The ceiling is real interop, not infinite sugar. When the DSL ends, a documented bridge to single-file C# plugins continues. No fork of the community into throwaway scripts and unportable C#.

## The language in sixty seconds

This is the whole pitch. A working welcome bot:

```
# beacon 1
# needs: chat.send

on join:
  say "Welcome to the server, {player}!"
end on

on chat when message contains "!rules":
  whisper player "1. Be kind. 2. No griefing. 3. Have fun."
end on
```

Save as `scripts/welcome.mcc`, run `/scripts run welcome` in the client. That is the entire onboarding.

## Files, headers, comments

Scripts are UTF-8 text files ending in `.mcc`, kept in the `scripts/` folder beside the configurations. One file is one script with its own globals.

The first line is mandatory, not decorative:

```
# beacon 1
```

The loader rejects a missing or newer major version with a pointer at the migrator instead of guessing. The old `.txt` `%var%` scripts and the archived `.cs` samples get a converter (`/scripts migrate oldscript.txt`), they do not get to keep their syntax alive inside the new language.

Capabilities ride in the same comment block:

```
# needs: chat.send inventory.read world.read
```

The loader infers what the script uses, compares it against this list, and refuses to load when they disagree. The refusal message includes the exact line to paste. There is no silent downgrade and no "lint said maybe". The safety section near the end details the capability set. Optional capabilities ride alongside as `# wants:`: the loader warns when a wanted capability is missing but loads anyway, for scripts that degrade gracefully (no bridge plugin? skip logging, keep chatting). Without `wants`, every optional integration would have to be a hard dependency.

Comments come in three forms: `#` and `//` for single lines (`//` only counts at line start or after whitespace, so `https://` inside strings and text never breaks), and `/* ... */` for spans. An unclosed span closes at end of file with one warning, not fifty errors.

## Values and variables

Beacon is untyped the way Lua is untyped: variables have no types, values do. Six value kinds cover everything scripts need: text, number, yes/no, list, map, none.

There is exactly one way to assign:

```
set name to "Steve"
set health to 20
set home to {x: 100, y: 64, z: -30}
```

`=` never assigns. Writing `set x = 5` or `if name = "Steve"` produces an error that says so and shows the fix. This single rule deletes the `=` versus `==` trap that eats every beginner cohort. Comparisons spell out: `is`, `is not`, with `==` and `!=` accepted as quiet aliases for arrivals from other languages. The docs teach `is`.

Names start with a letter, then letters, digits, underscores. Keywords are case-insensitive (`On Join` works, paste from chat survives), variable names are case-sensitive and the error for `Player` versus `player` suggests the right one.

Text interpolation uses braces and it evaluates any expression, not just names:

```
say "Welcome back, {player}! Online: {online_count + 1}"
```

Write a literal brace as `{{` or `}}`. There is no `%name%` form; the migrator rewrites it on import with a warning. Multiline text uses triple quotes, which is also how you paste signs and books without escaping every line.

Numbers and text never mix through `+`. `"health: " + health` is an error that tells you to write `"health: {health}"` or `text(health)`. This felt strict when I first wrote it down, then I remembered every chat-spam bug I have ever seen from silent coercion, and it stayed. Explicit converters are `text()`, `number()`, `yesno()`. `number("12")` gives 12; `number("twelve")` gives none, which tests honest with `is set` / `is not set`.

The falsiness rule is the conservative one the research backs: only `no` and `none` are false. `0`, empty text, and empty lists are all true. Conditions require an actual yes/no value, so `if food` is rejected with "Did you mean `if food > 0`?" and `if items` suggests `if items is not empty`. Verbose for a week, then nobody thinks about it again, which is the point.

One deliberate exception keeps defaults readable: `or` doubles as the default operator. `saved("seen") or {}` yields the saved map, or a fresh one when nothing was saved yet (`no` or `none` on the left). `and` and `or` return operand values, never coerced booleans, so the `if` rule still holds: `if warns[player] or 0` is rejected, `if (warns[player] or 0) > 2` is fine.

Bare shortcuts save typing without hiding ownership. Outside handlers, and anywhere unless shadowed, these bare names read their namespaced twins: `health` for `me.health`, `max_health`, `food`, `saturation`, `air`, `xp_level`, `online_count` for `server.online_count`, `tps` for `server.tps`. Inside a handler the event fields win (`health` in `on health` is the event snapshot), and writing `me.health` or `server.tps` always means the live value explicitly. There is no bare `hunger`; food is `food` everywhere.

## Operators and precedence

Words first, symbols tolerated. `and`, `or`, `not` are canonical; `&&`, `||`, `!` parse and the linter stays quiet about them, because yelling at someone's muscle memory is not onboarding.

```
not tired and (hungry or hurt)
```

Precedence, highest to lowest: member access and calls (`a.b`, `f(x)`); unary `not`/`-`; `*` `/` `%`; `+` `-`; comparisons (`is`, `<`, `>`, `contains`, `matches`, ...); `and`; `or`. When in doubt, parentheses. The reference implementation is a Pratt parser over span-tracked tokens, and the full grammar is appendix A of this document so the parser cannot quietly drift from the docs.

Comparisons worth naming because bots use them constantly: `is`, `is not`, `<`, `>`, `<=`, `>=`, `contains` (text or list membership), `matches` (regex, see Built-in reference below), `starts with`, `ends with`, `is empty`, `is not empty`, `is set`, `is not set`.

## Talking: say, whisper, server, mcc

Beginners have one mental model, "type something", and the old split between chat, server commands, and MCC internals punished them for it. Beacon has four explicit verbs for reaching the outside world, plus `show` for thinking out loud locally, and no bare slash lines. A line starting with `/` is a syntax error that suggests the right verb, because a bare `/home` copied from chat should never teleport someone by accident.

```
say "Hello everyone!"
whisper "Steve" "Meet at spawn?"
server "/home"
set answer to mcc "/list"
say "Online now: {answer}"
```

`say` sends chat. `say` with a leading slash warns ("did you mean `server`?") and sends nothing. `whisper` takes a player name and text. `server` requires the leading slash and sends it down the server command path. `mcc` runs an MCC internal command and gives back its output text, so scripts can branch on it. A failing `mcc` call raises a catchable error (see Control flow below); outside `try` it stops the handler with a readable message, it never returns a mystery empty string you mistake for "nobody online".

Every passthrough is logged at debug level with script name and line, so an admin auditing a griefing report can see exactly which script sent what. The local echo stamps every outgoing line with its script id; public chat itself is never prefixed (that would leak bot structure to strangers), but when two scripts fire at once the trace always says who spoke.

Dry runs get their own verb. `show` prints to the local console and never touches chat, needs no capability, and is the REPL's default output, so the first thing a beginner runs cannot spam a public server:

```
set answer to mcc "/list"
show "would say: Online now: {answer}"
```

## Control flow

Blocks open with words and close with labeled ends. Bare `end` also parses, for paste tolerance, but every doc example and the formatter write the label. Indentation is ignored by the parser and checked by the linter: if your indent disagrees with your nesting, you get one warning pointing at the line, not a silent reinterpretation.

```
if health < 10 then
  say "I need food!"
else if food < 6 then
  eat()
else
  say "All good."
end if
```

`then` and the trailing colon are both optional; include whichever reads better to you. Nested dangling `else` binds to the nearest `if`, and the labeled closer makes that visible instead of spooky.

Loops come in three shapes. `repeat` counts, `while` guards, `for each` walks a list or the keys of a map:

```
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
```

`stop` breaks out, `skip` continues to the next round (`break` and `continue` parse as quiet aliases). `wait` takes a number and a unit (`millisecond`, `second`, `minute`, all plural-tolerant, `sec` and `s` accepted) and yields the current task; it never freezes the session. Minimum quantum is 100 ms so `wait 0 seconds` in a loop cannot spin.

Failure gets `try`:

```
try
  server "/home"
catch err
  say "Could not go home: {err.message}"
end try
```

Game calls that can fail from lag or distance (a block read outside render distance, a move during a lag spike) raise catchable errors with `.message` instead of killing an overnight bot. Which calls raise is documented per function, not left to folklore.

## Functions

Small, named, with inputs and one output. No colon, no ceremony:

```
function greet(name)
  return "Hello, {name}!"
end function

say greet("Steve")
```

Calls are expressions, so they nest: `say greet(pick(online_players))`. Locals stay local; globals are per script. Recursion is allowed and the fuel budget (section 12) catches the enthusiastic kind. A function whose return value is discarded gets a lint note, because nine times out of ten that means someone wrote `greet("Steve")` on a line alone and wondered why nothing appeared. The canonical examples always show the `say`. A `# desc:` line above a function documents it for REPL autocomplete and `lint` output, the same way doc comments feed `/help` for commands.

## Tasks and waiting

The 1.x threading trap (scripts sampling `Thread.Sleep` on their own thread while the API demanded main-thread hops) does not get a second life. Beacon runs each script on a cooperative scheduler: one logical task per script plus children, a FIFO event queue, and `wait` as a yield point. Event payloads are immutable snapshots taken at dispatch, so a `wait 5 seconds` inside a chat handler cannot watch the world mutate mid-sentence. Reconnects cancel pending sleeps and rebind reads on the next event; nothing fires into a dead session.

Background work is explicit:

```
start patrol()

function patrol()
  while yes
    move_goto(120, 64, -40)
    wait 3 seconds
  end while
end function
```

`start` returns a task id. `await id` waits for that task, `tasks()` lists them, `cancel task id` stops one. Movement calls are tasks too: `move_goto` completes when you arrive or when it is cancelled, which means patrol and chat handlers need a stated preemption rule. The rule is simple: the newest movement request wins, the loser gets a catchable `superseded` error, and the loser is always told. No two handlers silently fight over the feet.

## Scheduler and lifecycle

The old `ScriptScheduler` lived in config files with login/time/interval triggers and no in-language equivalent, so interval bots were `while true` plus `wait` plus hope. Beacon says these things in the script:

```
every 60 seconds
  if online_count > 0
    say "Tip: use !help to see what I can do."
  end if
end every

on login
  say "Back online and ready."
end on

on disconnect
  log "Lost connection, tasks parked."
end on
```

Lifecycle hooks: `start` (script loaded), `login`, `logout`, `disconnect`, `reconnect`. Scheduler blocks: `every N seconds|minutes|hours`. Do handlers refire after reconnect? Yes for `login`/`reconnect`, no replay for missed `every` ticks (one catch-up run at most, then the normal cadence). Task state does not survive a reload; `saved` state does (see State below).

The in-client surface is one command with subcommands: `/scripts list|run <file>|stop <id|all>|reload|lint <file>|migrate <oldfile>|new <template> <id>|mute on|off`. `stop all` is the panic button that kills every script; `mute on` gags script chat while logic keeps running, with the mute visible in the status line. `new` scaffolds a working commented script from a template (`welcome`, `guard`, `shop`, `empty`), because onboarding that starts with `run this` beats onboarding that starts with a blank file. Each subcommand carries the standard metadata (category, usage, examples) that `/help` reads, per repo convention.

## Events

Triggers are top-level syntax, one per block, as many per file as you like, hot-reloadable individually. The form splits the trigger name from the filter so the grammar never has to parse English sentences:

```
on chat as e when e.message contains "!help"
  whisper e.player "Try !rules, !spawn, !shop."
end on
```

`as e` is optional; the default name is `event`. Inside a handler, event fields are also in scope as bare names (`message`, `player`), resolving to event fields before globals, because `when message contains "!help"` reads better and the beginner reviewer was right about it. The full `event.message` form always works and the docs teach it second, once the first script already runs.

Busy handlers get a throttle clause between the event and the filter: `on tps cooldown 300 seconds named "tps-warn" when tps < 15`. A firing inside the window is skipped and debug-logged, the window is per script and RAM-only so reloads reset it, and two handlers sharing a name share the throttle. This deletes the saved-timestamp dance that every interval bot otherwise reinvents wrong.

Filters are ordinary boolean expressions over the event record plus globals. There is deliberately no special filter grammar: `on chat when message contains "x"`, `on health when health < 6`, and `on join when player is "Notch"` all parse through the same path, which is what keeps error messages precise instead of Skript's "can't understand this condition".

Stopping a chat message from reaching the server spells out:

```
on chat when message contains "badword"
  stop event
  whisper player "Please keep chat friendly."
end on
```

(`cancel event` parses and the linter suggests `stop event`. One spelling taught, one forgiven.)

The v1 hook catalog is small on purpose. It covers what the archived `ChatBot` hooks and the new `GameApi` already surface, and it names what stays in C# so nobody plans around a hook that will not exist:

| Beacon hook | Event fields | Provenance |
|---|---|---|
| `chat` | player, message, raw, is_private | `GetText` / `ChatApi.MessageReceived` |
| `whisper` | player, message | private-message parsers |
| `server_message` | text, translation_key | system chat path |
| `join` | player | `OnPlayerJoin` |
| `leave` | player | `OnPlayerLeave` |
| `death` | player, cause | `OnDeath`/`OnKilled` |
| `respawn` | player | `OnRespawn` |
| `health` | health, max_health, change | `OnHealthUpdate` |
| `hunger` | food, saturation, change | food/vitals path |
| `inventory` | slots_changed | `OnInventoryUpdate` |
| `login` | (lifecycle) | `AfterGameJoined` |
| `disconnect` | reason | `OnDisconnect` |
| `reconnect` | (lifecycle) | supervisor reconnect |
| `kick` | reason | disconnect with kick |
| `tps` | tps, mspt | `OnServerTpsUpdate` |

Entity movement, packet capture, and per-packet hooks stay in C# plugins. That is an explicit non-goal for v1, stated here so the power-user crowd does not read absence as promise.

## State: saved and shared

Two tables, two lifetimes, no confusion between them.

`saved` persists across restarts, backed to `configurations/beacon/<script>.toml` with atomic writes. Warn counts, home lists, seen players live here:

```
set warns to saved("warns") or {}
set warns[player] to (warns[player] or 0) + 1
save "warns" to warns
```

`shared` is RAM-only cross-script state with namespaced keys, for scripts that cooperate (a shop script reading the economy script's prices). Writes take the form `shared["shop.price.bread"]`, reads check `is set` first, and a `lock shared` block serializes a read-modify-write so patrol and chat handlers cannot interleave a warn count. Quotas bound key count and value size; the linter reports who shares what.

## Imports and C# interop

One file stops being enough around week two, so week two gets `import`:

```
import "lib/econ.mcc" as econ
say "Bread costs {econ.price("bread")} coins."
```

Paths resolve relative to the importing file, the loader content-hashes like the `.cs` plugin cache, circular imports are a clear error naming the cycle, and `lint` chases imports to union their permissions into the report.

The C# bridge runs both directions, because the power-user reviewer correctly called a one-way bridge a community fork. Beacon can call out:

```
extern price_of from "shop"
say price_of("bread")
```

`extern` names a plugin id, not a file. The C# side that publishes `price_of` is the SDK section below. Going the other way, a single-file C# plugin can call an exported script function through `IBeaconHost.CallFunctionAsync("greet", args, ct)`. Beacon variables surface under `vars.beacon.*`, readable from `%beacon.coins%` expansion and writable through the existing `VariableStore`, so scheduled commands and scripts share state instead of maintaining rival truths. Scripts can even register a real Brigadier command:

```
command "/price <item>"
  set target to arg("item")
  say "{target} costs {price_of(target)} coins."
end command
```

Metadata (category, usage lines, examples) comes from doc comments above the block, so `/help price` reads properly. Scripts call each other through the same door C# uses, with the same value marshaling:

```
set report to call "shopkeeper.daily_report"()
say report
```

`lint` chases `call` targets like imports and unions their needs; at runtime the callee runs on the caller's fuel, and a missing script or function raises a catchable error naming script, function, and caller line. Packet-level work, custom renderers, and anything needing raw throughput stays in C#. That boundary is a feature: it tells each kind of author where they are strongest.

## Extending the language from the C# plugin SDK

`extern`, `export`, and script-registered `command` blocks are promises. This section is the C# side that keeps them, drawn directly against the real SDK so the implementer is not inventing a second plugin model. The SDK facts that constrain the design: a plugin implements `IMccPlugin` (`Configure` side-effect free, `ActivateAsync` once per client start, `DeactivateAsync` with a bounded teardown wait), per-session work hangs off `PluginContext.SessionStarted`/`SessionEnded`, commands register through `IPluginCommandScope` (plugin lifetime or session lifetime, auto-unregistered), persistence lives in the `IPluginStorage` sandbox (`data/` plus `storage.toml`), plugins talk through `IPluginMessenger` (typed pub/sub and request/response, shared-contract types only across collectible load contexts) and `IPluginServices` (`Register`/`TryGet`, handles withdrawn on unload), and manifests are `plugin.toml` with `id`, `version`, `entry`, `api-version`, `deps`, `enabled`.

Beacon follows the same shapes. The runtime (living in `Mcc.Core` beside `GameApi` and `CommandService`) exposes one host object, and plugins reach it the boring way:

```
PluginContext.Beacon  // IBeaconHost, present when the Beacon runtime is loaded
```

Three extension points, one each for the three promises.

First, functions. A plugin offers a function under a name, a capability, and a description. The description is not decoration: it becomes the Beacon help text shown by `lint` and REPL autocomplete, the same way doc comments feed `/help` for commands.

```csharp
public sealed class Shop : IMccPlugin
{
    public void Configure(PluginDescriptor d)
    {
        d.Id = "shop";
        d.Version = "1.0.0";
        d.ApiVersion = "2";
    }

    public Task ActivateAsync(PluginContext ctx)
    {
        ctx.Beacon.Functions.Register(new BeaconFunction(
            Name: "price_of",
            Capability: "econ.read",
            Description: "Today's buy price for an item id, or none when unlisted.",
            Parameters: ["item"],
            Invoke: async call =>
            {
                string item = call.RequireText(0);
                double? price = PriceList.Today(item);
                return price is null ? null : (object)price.Value;
            }));
        ctx.Beacon.RegisterEvent("shop_buy", ["player", "item", "price"],
            "Fired when the shop completes a sale.");
        ctx.SessionStarted += (_, e) => WatchSales(e.Session);
        return Task.CompletedTask;
    }
}
```

Single-file plugins get the short form, an attribute over a static method, for the common case of a pure function:

```csharp
[BeaconFunction("price_of", Capability = "econ.read",
    Description = "Today's buy price for an item id, or none when unlisted.")]
public static double? PriceOf(string item) => PriceList.Today(item);
```

The host scans for the attribute at load and registers the same record. One spelling taught (registration), one forgiven (attribute), same rule as the language itself.

Values cross the boundary as a fixed six-kind mapping, and only as those kinds: text to string, number to double (ints widen), yes/no to bool, list to read-only list, map to read-only string-keyed dictionary, none to null. Anything else fails registration fast with the plugin id and the offending signature, never at first call at 3 AM. This mirrors the messenger's shared-contract rule: because every plugin sits in its own collectible load context, a plugin-private type can never leak into a script value. The host validates signatures in `ActivateAsync`, and two plugins claiming the same function or event name is a load error naming both, the same way `IPluginServices` refuses a taken contract instead of silently shadowing it.

Second, events. The `RegisterEvent` call above declares `shop_buy` with its fields. Firing it is one call from session-safe code, honoring the scope's `Detached` token exactly like any session loop must:

```csharp
await ctx.Beacon.FireEventAsync("shop_buy",
    new Dictionary<string, object?>
    {
        ["player"] = sale.Player,
        ["item"] = sale.Item,
        ["price"] = sale.Price,
    }, scope.Detached);
```

Scripts consume it with the ordinary `on` form, no new syntax to learn:

```
# beacon 1
# needs: chat.send econ.read

on shop_buy when item is "bread"
  say "Thanks {player}! Bread is the staff pick today."
end on
```

Third, calling into scripts. A script marks a function visible with `export`, and C# awaits it like any async call:

```
export function daily_report()
  return "Sales today: {saved("sales_total") or 0} coins."
end function
```

```csharp
object? report = await ctx.Beacon.CallFunctionAsync(
    "shopkeeper", "daily_report", [], ct);
```

The first argument is the script id (file name without extension). A missing script, a missing export, or an arity mismatch raises `BeaconCallException` naming all three, loud like the messenger's contract mismatch, never a null that reads as "no sales today".

Lifetime and threading follow SDK precedent throughout. Registrations are per plugin and withdrawn automatically on unload, disable, or reload, the way service and messenger handles already are. Extension functions execute on the Beacon scheduler, never on the UMPK session loop, and receive cancellation that fires on reconnect and unload; a function that blocks longer than the event budget is aborted and counted against the owning plugin, with the plugin id in the trace. Functions must be reentrant and stateless beyond their own storage: snapshots ride in through arguments, never as cached scopes, because `PluginContext.Session` throws when no session is attached and a cached `ISessionScope` dangles across reconnects. Movement stays under the lease rule by construction: extension functions cannot steer except by returning data to script-side `move_*` tasks, so the newest-movement-wins preemption from Tasks and waiting above has exactly one door to guard.

Capabilities close the loop with the manifest. Every extension function and event carries its capability string (`econ.read`), scripts list those strings in `# needs:`, and `lint` resolves each one to the providing plugin id, warning when the provider is not loaded and refusing when nothing provides it. The refusal names the plugin id to install, which keeps the five-minute onboarding true even when a script outgrows the standard library: the error is still one paste-ready line.

## Built-in reference

Flat and verb-first for daily use, namespaced tables for state. Names below are the proposed freeze list; renaming after v1 requires a major version bump. The library versions separately from the syntax: `# beacon 1` gates grammar, while `beacon.lib` reports the library version at runtime and new builtins are documented with the lib version that introduced them. `lint --target-lib N` flags anything newer, so old scripts keep running as the library grows.

Identity and self (`me`): `me.name`, `me.uuid`, `me.health`, `me.max_health`, `me.food`, `me.saturation`, `me.armor`, `me.air`, `me.xp_level`, `me.gamemode`, `me.pos` (a map with x, y, z), `me.yaw`, `me.pitch`, `me.ping`, `me.effects` (list of maps with name, level, seconds_left), `me.is_sneaking`.

Server (`server`): `server.ip`, `server.port`, `server.version_name`, `server.tps`, `server.mspt`, `server.online_count`, `server.max_players`, `server.motd`, `server.day_time`, `server.day`, `server.weather`, `server.difficulty`, `online_players` (paged list, capped per read so a 500-player hub cannot become a self-DDoS; `online_players(50)` takes the first page).

Time (`time`): `time.now` (HH:MM), `time.date`, `time.today` (weekday name), `time.hour`, `time.minute`, `time.stamp` (unix seconds), `time.format(stamp, "HH:mm")`, `time.ago(stamp)` ("3 minutes ago", localized through the corpus, never hardcoded).

Chat: `chat_history(n)`, `last_from(player)`, `count_matching(text, minutes)`.

Inventory (`inv`): `inv.list()` (per-slot maps with slot, type, name, count, lore), `inv.count(matcher)`, `inv.has(matcher, n)`, `inv.find(matcher)` (slot or none), `inv.find_all(matcher)` (slot list), `inv.selected()`, `inv.select(slot)`, `inv.drop()`, `inv.drop_stack()`, `inv.move(from, to)`, `inv.click(slot, mode)`, `inv.armor()` (worn pieces), `craft_list()`, `craft_one(recipe)`. A matcher is text or map: bare `"compass"` matches the normalized type id or the display name, case-insensitive (`minecraft:` prefix optional, `_`/space/`-` equivalent). The map form is exact unless the key says otherwise: `{type: "compass", name_contains: "server"}`, with `name`, `name_matches /.../`, `lore_contains`, `lore_matches`, `min_count`, `named: yes/no`. Display names resolve through the same translation path as chat, falling back to raw NBT; `type` never localizes, so it stays stable across versions and languages.

World (`world`): `world.block_at(x, y, z)` (map with name, id; raises outside range, catchable), `world.light_at(x, y, z)`, `world.biome_at(x, y, z)`, `move_goto(x, y, z)` (task), `move_follow(player)` (task), `stop_moving()`, `look_at(x, y, z)`, `attack(target)`, `use_in_hand()`.

Text and data helpers: `len`, `lower`, `upper`, `trim`, `split`, `join`, `slice`, `keys`, `values`, `has_key`, `text`, `number`, `arg(name)` (the named argument of a `command` block, none when absent), `match(text, /pattern/)` (map of numbered and named groups, or none), `json_parse`, `json_stringify`, `random(n)`, `pick(list)`, `chance(p)` (yes when a random draw under p hits), `min`, `max`, `clamp`, `round`, `abs`, `log`.

Self actions: `eat()` (consume one best-food item from inventory, returns yes when something was eaten), alongside the movement verbs below.

Date and time got their own corner because the requirements call them out and because every forum bot reinvents "what day is it" badly. `time.today`, `time.ago`, and `time.format` cover greetings, cooldown messages, and uptime reports without anyone hand-rolling weekday math.

Network is the gated room: `http_get(url)` and `http_post(url, body)` exist, require `net.fetch` in the manifest plus a host allowlist in `beacon.toml`, enforce HTTPS, a 5 second timeout, and a 1 MB cap. File access is narrower still: `file_read`/`file_write` under `<script-dir>/data/`, no symlink escape, 1 MB cap. Anything broader waits for v2 or lives in C#. The `accounts.toml` secrets file is never reachable from script IO, by construction, not by policy text.

Read-only game tables kill hardcoded strings: `items.totem_of_undying` gives the full type id, with `effects.*` and `enchants.*` alongside, all drawn from UMPK's dataset so they track versions. Unknown keys suggest neighbors, which retires the number-one works-on-my-version failure.

## Safety model

Borrowed shapes, local numbers. The skeleton comes straight from the Luau and ComputerCraft evidence; the constants are chosen for a chat client rather than a game engine.

| Control | Rule | Why this number |
|---|---|---|
| Manifest | `# needs:` must cover used verbs (refuse on mismatch); `# wants:` declares graceful-degradation caps (warn only) | implementer review: advisory lint is not a sandbox |
| Event budget | 100k operations fuel plus 5 s wall clock per dispatch, abort at loop back-edges, calls, waits, host calls | between Luau's 1 M instruction ceiling and ComputerCraft's 7 s watchdog, scaled to chat automation |
| Chat bucket | global token bucket shared by all scripts, burst 8 per 10 s, overflow queues then warns, never silently drops | per-script buckets are bypassed with N scripts; the throttle message names the script and suggests `wait` |
| Wait floor | 100 ms minimum quantum, max 32 pending sleeps per script | kills `wait 0` spin loops |
| Reads | `online_players` paged, `chat_history` capped at 200, block reads one at a time | no giant materialized lists in a hot handler |
| Globals | fresh globals per script, builtins read-only, `shared` namespaced with quotas | Luau's per-script `_G` with a read-only metatable, adapted |
| IO | fs jail to script `data/`, net to allowlisted HTTPS hosts, 1 MB caps, 5 s timeout | ComputerCraft's virtual root plus whitelist, tightened |
| Determinism | seeded RNG per dispatch, seed recorded on failure for replay | tests and bug reports need the same dice twice |

Timeouts name the handler, the line, and the locals at abort, and dump a task stack. A 24/7 bot that dies at 3 AM should leave a note its owner can act on.

## Errors and tooling

Errors read like a person explaining, with the machine details one flag away:

```
welcome.mcc:6:9
  whisper player 1. Be kind.
                ^
I expected text after the player name, but found the number 1.
Try this:
  whisper player "1. Be kind. 2. No griefing."
Note: whisper needs a player and one piece of text.
```

No spans jargon in the default view, no bare stack trace, and any "did you mean" suggestion (misspelled names, `=` for assignment, `if count` for `if count > 0`) is part of the format, not luck.

Budget aborts get the same treatment, matching the classic mistakes by pattern:

```
trace.mcc:11:3 fuel exhausted after 100,000 steps.
This loop has no 'wait' inside it, so it never yields.
Try this: interval work belongs in an 'every' block.
```

Tooling ships with the language, because the research is blunt that a language without a REPL, formatter, and linter is a language people bounce off:

- `/scripts list|run|stop|reload` for daily driving, with tab completion over script names.
- `/scripts lint <file>` chasing imports, reporting the permission union, and failing closed on missing manifest entries.
- `/scripts run <file> --trace` emitting per-line structured logs for the one bug that only happens overnight.
- `/scripts repl` with live `me`/`server`/`world` against the current session, seeded RNG available for replay.
- `/scripts migrate <oldfile>` converting `.txt` `%var%` scripts and flagging C# patterns it cannot carry (packet hooks, custom threads) with pointers at the interop docs.

The same engine drives a headless frontend, because anything an AI writes must be checkable without booting a client:

```text
Mcc.Cli.dll lint <file...> [--format text|json] [--target-lib N] [--strict] [--fix] [--stdin]
```

It runs fully offline: no session, no network, pure. The engine lives in `Mcc.Core` (console-free, per the analyzer rule) and the Cli adapter only formats, while in-client `/scripts lint` calls the same engine, so interactive and headless output can never disagree. Dispatch is on exact `argv[0]`; a configurations folder literally named `lint` must be passed as `./lint/`.

JSON goes to stdout as one document, human chatter to stderr, and the exit code is the contract agents script against: 0 means clean (warnings allowed), 1 means errors, 2 means usage failure. Every diagnostic carries a stable code (`B0001` parse, `B1xxx` manifests, `B2xxx` names and events, `B3xxx` strictness), file with line and span, severity, message, and a paste-ready suggestion. Codes are the agent's match surface, prose is the human's:

```json
{"files": [{"path": "quiz.mcc", "ok": false}],
 "diagnostics": [{"code": "B1007", "severity": "error", "file": "quiz.mcc",
   "line": 4, "col": 1, "message": "script uses chat.send but the manifest lacks it",
   "suggestion": "# needs: chat.send"}],
 "summary": {"errors": 1, "warnings": 0}}
```

`--strict` escalates unresolvable `extern`/`call` targets and missing capability providers from warnings to errors, for CI gates and pre-commit hooks. `--fix` applies only the safe mechanical set (completing `end` labels, `cancel event` to `stop event`) and prints a diff first; anything touching meaning (`=` versus `is`, a missing `wait`) stays a suggestion. `--stdin --stdin-name quiz.mcc` lints piped output, which closes the AI loop: generate, lint, read JSON, repair, repeat, without ever joining a server.

Tests run headlessly in `Mcc.Core.Tests` against the in-memory fake server, with a virtual clock and seeded RNG injected behind the host seam, so script behavior is deterministic in CI. That requirement goes in now, while the seam is being drawn, not later when faking it costs a refactor.

## Examples

Seven small scripts that each teach one idea. All are complete and follow the frozen syntax.

A greeter that remembers (persistence):

```
# beacon 1
# needs: chat.send

on join
  set seen to saved("seen") or {}
  if seen[player] is set
    say "Welcome back, {player}! Visit {seen[player]} times and counting."
    set seen[player] to seen[player] + 1
  else
    say "Welcome for the first time, {player}! Type !rules to start."
    set seen[player] to 1
  end if
  save "seen" to seen
end on
```

A totem guard that watches vitals (events plus inventory):

```
# beacon 1
# needs: chat.send inventory.read inventory.write

on health when health <= 6
  if inv.has("totem_of_undying", 1)
    inv.move(inv.find("totem_of_undying"), "offhand")
    say "Totem equipped. That was close."
  else
    say "Low health and no totem! Logging out would be smart."
  end if
end on

on hunger when food <= 6
  eat_best()
end on

function eat_best()
  if inv.has("cooked_beef", 1)
    inv.select(inv.find("cooked_beef"))
    use_in_hand()
  end if
end function
```

Bedtime for the AFK farmer (time plus movement tasks):

```
# beacon 1
# needs: chat.send movement

every 60 seconds
  if time.hour >= 22 or time.hour < 6
    if me.pos.y < 60
      say "Night shift over, heading to bed. Back at sunrise."
      start go_sleep()
    end if
  end if
end every

function go_sleep()
  move_goto(120, 65, -40)
  say "Goodnight. {time.ago(time.stamp)} of work done."
end function
```

A shopkeeper with real memory (imports, saved state, custom command):

```
# beacon 1
# needs: chat.send
import "lib/econ.mcc" as econ
extern price_of from "shop"

command "/price <item>"
  set target to arg("item")
  set p to econ.price(target)
  if p is set
    say "{target} costs {p} coins today."
  else
    say "Sorry, I do not buy {target}."
  end if
end command

on chat when message starts with "!sell "
  set item to trim(slice(message, 6))
  set p to econ.price(item)
  if p is set
    say "Sold! {econ.credit(player, item)}"
    set ledger to saved("ledger") or {}
    set ledger[player] to (ledger[player] or 0) + p
    save "ledger" to ledger
  end if
end on
```

A TPS guard for admin peace of mind (server state plus cooldowns):

```
# beacon 1
# needs: chat.send

on tps cooldown 300 seconds named "tps-warn" when tps < 15
  say "Heads up: server TPS is {tps} ({server.mspt} ms/tick). Easy on the farms."
end on
```

A quiz night (state machine in a map, the thing `.txt` could never do):

```
# beacon 1
# needs: chat.send

set quiz to {running: no, q: "", a: "", wins: {}}

on chat when message is "!quiz"
  if quiz.running is yes
    whisper player "A round is already running: {quiz.q}"
    stop event
  end if
  set quiz.running to yes
  set quiz.q to "What mob explodes when it gets close?"
  set quiz.a to "creeper"
  say "Quiz! {quiz.q} First correct whisper wins."
end on

on whisper when message contains quiz.a and quiz.running is yes
  set quiz.running to no
  set wins to quiz.wins
  set wins[player] to (wins[player] or 0) + 1
  set quiz.wins to wins
  say "{player} got it! Wins total: {wins[player]}. Say !quiz for another round."
end on
```

An auction sniper sketch (regex, http bridge behind the gate):

```
# beacon 1
# needs: chat.send net.fetch

on server_message when text matches /bought (?<n>[0-9]+)x (?<item>[a-z_ ]+) for \$(?<price>[0-9.]+)/
  set deal to match(text, /(?<n>[0-9]+)x (?<item>[a-z_ ]+) for \$(?<price>[0-9.]+)/)
  if deal is set and number(deal.price) < 20
    say "Saw cheap {deal.item} ({deal.n}x for ${deal.price}). Logging it."
    http_post("https://hooks.example.com/mcc-deals", json_stringify(deal))
  end if
end on
```

The last one needs its host on the allowlist or the loader refuses with the exact `beacon.toml` lines to add. That refusal is the feature.

## What the critics changed

| Critic | Problem they found | Decision in this proposal |
|---|---|---|
| Beginner | `set x to` versus `x =` reads as two different meanings | one assignment spelling: `set ... to ...`, `=` rejected with a fix |
| Beginner | `{name}` versus `%name%` doubles learning and collides with chat `%` | braces only, migrator rewrites legacy `%var%` |
| Beginner | ignored indentation plus required `end` fails silently on mis-nesting | parser uses `end`, linter warns when indent disagrees |
| Beginner | `say`/`send`/`run`/bare-slash is four ways to talk | `say`, `whisper`, `server`, `mcc`, no bare slash lines |
| Beginner | event filters shaped like three different English sentences | `on <name> [as v] [when <boolean>]`, filters are normal expressions |
| Beginner | truthy `0`/`""` lies in hunger checks | strict booleans, explicit comparisons, suggested fixes |
| Beginner | silent coercion in `+` and `wait "2"` | no mixed `+`, explicit converters, typed `wait` |
| Beginner | `stop loop`/`skip`/colon/function-return confusion | `stop`/`skip` canonical with `break`/`continue` forgiven, no colon, examples always show the return being used |
| Beginner | Discord paste mangling (smart quotes, case, plurals) | loader normalizes quotes, case-insensitive keywords, plural-tolerant units |
| Beginner | lint file plus `permissions.toml` is onboarding death | zero-config start, refusal messages carry the paste-ready fix |
| Implementer | bare `/cmd` versus division needs a context-sensitive lexer | bare slash removed, three explicit verbs |
| Implementer | English event filters are unparseable long term | closed event enum plus boolean predicates over typed records |
| Implementer | optional `then`/`:` plus generic `end` misattributes errors | one block form, labeled closers canonical, EBNF published |
| Implementer | dual assignment/interpolation plus loose truthiness compounds | single assignment, single interpolation, strict booleans |
| Implementer | `wait` with no threading model stalls the session | cooperative scheduler, immutable snapshots, reconnect cancels sleeps |
| Implementer | loop cap plus timeout does not bound cost | fuel plus wall clock, global chat bucket, paged reads, wait floor |
| Implementer | `run` plus flat globals is a sandbox escape | mandatory manifest, internal-command allowlist, namespaced `shared` quotas |
| Implementer | binary file/net grant enables exfiltration of `accounts.toml` | fs jail, HTTPS allowlist, caps, secrets unreachable by construction |
| Implementer | optional version header guarantees silent breakage | `# beacon 1` mandatory, fail closed, migrator shipped |
| Implementer | no replay or test seam | spans through desugar, injected clock/RNG, trace mode, recorded seeds |
| Power user | no tables, bots cannot model waypoints or warn counts | map/list literals, indexing, `keys`/`values`, JSON round trip |
| Power user | no `try`/`catch`, one hiccup kills a 24/7 bot | `try`/`catch err`, documented raising calls |
| Power user | `wait` semantics undefined, no background patrol plus chat | `start`/`await`/`cancel`, newest movement wins with `superseded` error |
| Power user | three hooks versus forty | v1 catalog of fifteen with provenance, packets explicitly out |
| Power user | no scheduler or lifecycle in language | `every` blocks plus lifecycle hooks with stated reconnect semantics |
| Power user | `shared` is a RAM toy, no persistence | `saved` TOML-backed plus namespaced `shared` with locks |
| Power user | zero C# interop defined | `extern`/`export`, `vars.beacon.*` bridge, `command` registration |
| Power user | inventory and movement too shallow for real automation | click/drag/craft/recipe surface, movement as cancellable tasks |
| Power user | chat parsing stuck at `contains` | `matches` plus `match()` with named groups, raw and translation keys exposed |
| Power user | no modules | relative `import` with hash cache, cycle errors, permission union in lint |
| Power user | no bridge or debug story | gated `http_*`, `repl`, `--trace`, timeout dumps with locals |

## Rollout plan

Four phases, each shippable, none requiring a flag day.

Phase one is the core: tokenizer through interpreter for values, expressions, control flow, functions, and verbs, plus the mandatory header and the strictness rules. Tests pin the grammar (every example in this document runs in CI), the `=` rejection, and the truthiness errors. Phase one also ships the conformance suite: an accept/reject snippet file owned by the grammar appendix, so parser and docs share one source of truth from the start.

Phase two wires events and state: the scheduler, the fifteen hooks against the live session, `saved`/`shared`, `every` blocks, and lifecycle hooks, all headless-tested against the fake server with virtual time.

Phase three is safety and tools: manifest enforcement, budgets, the chat bucket, fs/net jails, `lint` (in-client and headless JSON), `--trace`, `repl`, `migrate`, and the Elm-style error renderer. This is also when the template picker, the mute switch, and the rest of the `/scripts` surface land, since tooling is the onboarding.

Phase four is the ceiling: `import`, `extern`/`export`, `command` registration, movement tasks with preemption, and the http bridge. Each interop surface gets a contract test proving both directions, so the DSL and C# worlds stay one community instead of two.

Docs move with the code: the `docs/` site still describes the 1.x `.ini` world and needs its scripting chapter rewritten against this proposal, and any command behavior the bridge touches gets its manual page updated in the same commit per repo rule.

## Open questions

Five things I did not want to decide alone.

First, the canonical assignment word. `set x to 5` reads best and dodges the `=` trap, but arrivals from Python and JavaScript will type `x = 5` on day one and meet an error. The proposal keeps the error friendly. If testing shows it still burns people, the fallback is accepting `=` with a lint nudge, not teaching both.

Second, whether `every` blocks should catch up missed ticks after downtime. The proposal says at most one catch-up run. A farming bot owner may want full catch-up; a chat bot owner definitely does not want five queued greetings. Per-block policy (`every 60 seconds catch up` versus `skip missed`) is the likely answer.

Third, how much of the Brigadier surface `mcc` may call. The proposal defaults the internal-command allowlist to read-only commands and requires explicit grants beyond that. The exact default list needs a pass over the command registry with the plugin authors in the room.

Fourth, localization of the language itself. Keywords are English; user-facing strings go through the corpus per repo rule. Whether error text and the starter template ship translated in v1 or English-first is a scope call with the localization folks.

Fifth, the name. Beacon is my suggestion and I like it, but names stick. If it collides with an existing plugin or a Mojang term of art I have not tripped over, rename before the header string freezes, because `# beacon 1` in ten thousand scripts is forever.

## Appendix A: formal grammar (normative for v1)

What follows is the whole language, no more and no less. If prose anywhere above disagrees with this section, this section wins and the prose gets fixed. Notation is Wirth EBNF: `=` defines, `|` alternates, `[ ]` option, `{ }` repetition, `( )` groups, `"x"` is a literal token, `(* *)` comments. Keywords are case-insensitive (`On Join` parses); identifiers are case-sensitive. Newlines separate statements; indentation is trivia.

```
(* ---------- top level ---------- *)
script      = header prologue { topdecl } EOF ;
header      = "#" "beacon" MAJOR ;
prologue    = { manifest | importdecl | externdecl } ;
manifest    = "#" ( "needs" | "wants" ) ":" caplist ;
caplist     = capability { capability } ;
capability  = ident { "." ident } ;
topdecl     = onblock | everyblock | functiondef | commandblock | statement ;

importdecl  = "import" STRING "as" ident ;
externdecl  = "extern" ident "from" STRING ;

(* ---------- blocks ---------- *)
onblock     = "on" eventname [ "as" ident ]
              [ "cooldown" expr cooldownunit "named" STRING ]
              [ "when" expr ] headtail
              block "end" [ "on" ] ;
everyblock  = "every" expr everyunit headtail
              block "end" [ "every" ] ;
functiondef = [ "export" ] "function" ident "(" [ params ] ")" headtail
              block "end" [ "function" ] ;
commandblock= "command" STRING headtail
              block "end" [ "command" ] ;
params      = ident { "," ident } ;
waitunit    = "millisecond" | "milliseconds" | "second" | "seconds"
            | "sec" | "s" | "minute" | "minutes" | "min" ;
everyunit   = "second" | "seconds" | "minute" | "minutes" | "hour" | "hours" ;
cooldownunit= everyunit ;
headtail    = [ "then" ] [ ":" ] ;
block       = { statement } ;

(* ---------- statements ---------- *)
statement   = setstmt | saystmt | whisperstmt | serverstmt | showstmt
            | waitstmt | stopstmt | skipstmt | returnstmt
            | savestmt | lockstmt | startstmt | awaitstmt | cancelstmt
            | ifstmt | whilestmt | repeatstmt | forstmt | trystmt
            | exprstmt ;
setstmt     = "set" target "to" expr ;
target      = ident { "." ident | "[" expr "]" } ;
saystmt     = "say" expr ;
whisperstmt = "whisper" expr expr ;
serverstmt  = "server" expr ;
showstmt    = "show" expr ;
waitstmt    = "wait" expr waitunit ;
stopstmt    = "stop" [ "event" ] | "cancel" "event" ;
skipstmt    = "skip" | "break" | "continue" ;
returnstmt  = "return" [ expr ] ;
savestmt    = "save" expr "to" expr ;
lockstmt    = "lock" "shared" block "end" [ "lock" ] ;
startstmt   = "start" postfix ;
awaitstmt   = "await" expr ;
cancelstmt  = "cancel" "task" expr ;
ifstmt      = "if" expr headtail block
              { "else" "if" expr headtail block }
              [ "else" block ] "end" [ "if" ] ;
whilestmt   = "while" expr headtail block "end" [ "while" ] ;
repeatstmt  = "repeat" expr "times" headtail block "end" [ "repeat" ] ;
forstmt     = "for" "each" ident "in" expr headtail block "end" [ "for" ] ;
trystmt     = "try" block "catch" ident block "end" [ "try" ] ;
exprstmt    = expr ;
```

Expressions, highest precedence at the bottom. `or` doubles as the default operator (the values section); `and`/`or` return operand values, conditions still require yes/no.

```
expr        = orexpr ;
orexpr      = andexpr { "or" andexpr } ;
andexpr     = notexpr { "and" notexpr } ;
notexpr     = [ "not" | "!" ] cmpexpr ;
cmpexpr     = addexpr [ compop addexpr ] ;
compop      = "is" [ "not" ] ( "empty" | "set" | addexpr )
            | "==" | "!=" | "<" | ">" | "<=" | ">="
            | "contains" | "matches" | "starts" "with" | "ends" "with" ;
addexpr     = mulexpr { ( "+" | "-" ) mulexpr } ;
mulexpr     = unary { ( "*" | "/" | "%" ) unary } ;
unary       = ( "-" unary ) | postfix ;
postfix     = primary { "." ident | "[" expr "]" | "(" [ args ] ")" } ;
args        = expr { "," expr } ;
primary     = NUMBER | TEXT | TRIPLETEXT | REGEX
            | "yes" | "no" | "none"
            | callprim | ident | listlit | maplit | "(" expr ")" ;
callprim    = "call" STRING "(" [ args ] ")" ;
listlit     = "[" [ expr { "," expr } ] "]" ;
maplit      = "{" [ mapentry { "," mapentry } ] "}" ;
mapentry    = ( ident | STRING ) ":" expr ;
```

Lexical notes, which are part of the contract, not commentary:

- `ident` is a letter followed by letters, digits, or underscores. Reserved words cannot be identifiers: `set to on every when as end if else while repeat times for each in try catch function return export command import extern from say whisper server show wait stop skip break continue save lock shared start await cancel call task event not and or is contains matches starts ends with empty set yes no none then cooldown named wants`.
- Multiword tokens lex as one (`is not`, `is empty`, `is not set`, `starts with`, `for each`, `else if`, `stop event`, `cancel event`, `cancel task`, `lock shared`), with any single spaces between the words. `end` plus its label is two tokens; a label that does not match the opener is a static error, a missing label is fine.
- `TEXT` is a double-quoted string with `{ expr }` interpolation and `{{`/`}}` escapes; `TRIPLETEXT` is the triple-quoted multiline form with the same interpolation. Interpolation never nests a string containing the same quote kind unescaped; the migrator handles the rest.
- `REGEX` is `/` up to the next unescaped `/`, with `(?<name>...)` groups allowed. The lexer reads `/` as regex-start only where an operand is expected (after `matches`, `(`, `,`, `[`, `:`, `when`, `return`, or at expression start); everywhere else it is division. `a / b` divides, `text matches /x/` tests.
- `NUMBER` is digits with an optional fraction (`60`, `2.5`); no hex, no exponents in v1. `MAJOR` is digits only: `# beacon 1.2` is rejected with a pointer at the header rule.
- Units: `waitunit` is `millisecond(s)`, `second(s)`, `sec`, `s`, `minute(s)`, `min`; `everyunit` is `second(s)`, `minute(s)`, `hour(s)`. Anything else is a static error naming the allowed units.
- `#` starts a comment to end of line; `//` comments only at line start or after whitespace, so `https://` never breaks; `/* ... */` spans nest nothing and an unclosed span ends at EOF with one warning. A `# needs:` or `# wants:` line after the first declaration is an ordinary comment (the linter flags it as too late).
- A statement line starting with `/` is a lexical error suggesting `server` or `mcc`. `=` outside the listed operators is a lexical error suggesting `set ... to` or `is`.
- `eventname` lexes as an identifier; the closed v1 set (`chat whisper server_message join leave death respawn health hunger inventory login disconnect reconnect kick tps`) plus plugin-registered names from the SDK section is enforced semantically, so an unknown hook errors with the full list instead of failing the parse.

Static checks the parser hands off (each with a beginner-worded error): `if`/`while`/`when` operands must be yes/no; `+` operands must both be text or both numbers; `wait`/`every`/`cooldown` counts must be numbers; `end` labels must match; `on`/`every`/`function`/`command` nest in nothing; `export` appears only at top level; `call` targets resolve like imports with needs unioned, and a missing script or function raises catchably; cooldown windows are per script and RAM-only; `mcc` failure raises (the verbs section); `online_players(n)` caps the page; movement calls complete as tasks under newest-wins preemption.

## Appendix B: how the grammar was checked

Every Beacon snippet in this document was machine-parsed against appendix A with a throwaway recursive-descent checker written during this design pass (Python, kept in scratch, not shipped). Result at the time of writing: all 29 Beacon snippets in the document parse, including all seven full examples, the SDK-section script, and the `export` fragment. Seventeen extra edge cases pass (`lock shared`, `await`, `cancel task`, `show`, `call`, cooldown clauses with and without `as`, `# wants:`, uppercase keywords, span and trailing comments, map literals, `or`-defaults). Fifteen negative cases are all rejected: `set x = 5`, bare `x = 5`, a leading-slash line, `if food`, `while 1`, `while true`, `repeat 3 time`, a mismatched `end while`, an empty `when`, an unclosed string, an empty map value, a bare `contains`, a bad cooldown unit, a bare `show`, and a `call` without parens. The single warning in the whole run is the expected one: `shop_buy` is not in the built-in hook set because a plugin registers it.

Honest limits of that checker, so the implementer does not mistake it for a type system: it enforces the boolean-condition rule structurally (a condition must be a comparison, an `and`/`or`/`not` combination, or a call), which catches `if food` but waves through `if not tired` where only runtime types can judge. Mixed `+` operands and unknown-variable reads are runtime errors, not parse errors, exactly as the grammar says. That is the same bar the rollout plan sets for CI: every example in this document runs as a grammar test, so the docs cannot drift from the parser without a test going red first.

---

Sources consulted: `Archive/MinecraftClient/ChatBots/Script.cs`, `Archive/MinecraftClient/Scripting/ChatBot.cs`, `Archive/MinecraftClient/Scripting/CSharpRunner.cs`, `Archive/MinecraftClient/Scripting/DynamicRun/Builder/Compiler.cs`, `Archive/MinecraftClient/Settings.cs`, `Archive/MinecraftClient.ini`, `Archive/MinecraftClient/config/sample-script*.cs/.txt`, `docs/guide/creating-bots.md`, `docs/guide/creating-text-script.md`; `Mcc/Mcc.PluginSdk/IMccPlugin.cs`, `Mcc/Mcc.PluginSdk/PluginContext.cs`, `Mcc/Mcc.PluginSdk/ISessionScope.cs`, `Mcc/Mcc.PluginSdk/IPluginCommandScope.cs`, `Mcc/Mcc.PluginSdk/IPluginServices.cs`, `Mcc/Mcc.PluginSdk/IPluginMessenger.cs`, `Mcc/Mcc.PluginSdk/IPluginStorage.cs`, `Mcc/Mcc.PluginSdk/PluginManifest.cs`; Tavily research reports Q1 (beginner language design, Luau/Python/GDScript/Elixir evidence), Q2 (Luau and ComputerCraft sandbox mechanisms), Q3 (Skript, mIRC, AutoHotkey v2, Streamer.bot DSL comparison); three adversarial critiques (beginner UX, implementer/security, power user) run against the v1 draft during this design pass.

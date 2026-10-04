# Configuration

Four TOML files in your configurations folder. They are generated with commented defaults on first
run and are never rewritten by a plain load, so your comments and formatting survive. The folder
is `./configurations` unless `--configurations <folder>` names another one (a folder, or a
`client.toml` file inside one).

| File | Holds | Secrets |
| --- | --- | --- |
| `client.toml` | connection, gameplay gates, chat, logging, permissions | no |
| `accounts.toml` | accounts, cache policy, proxy | yes, gitignored |
| `servers.toml` | named servers | no |
| `console.toml` | colours, glyphs, suggestions, TUI | no |

## Load order

1. `console.toml` loads first, so colours are known before anything prints.
2. The other three load into one immutable snapshot.
3. The `<username> <password|-> <host[:port]>` positional arguments override the files.
4. `--section.setting=value` flags override the positionals.

A flag whose first segment is `console` targets `console.toml`; everything else targets
`client.toml`.

## Feature gates

`[Gameplay]` decides which subsystems run at all. All five default on.

    Terrain       track the world
    Inventory     track items
    Entity        track entities
    Physics       local collision and gravity
    Pathfinding   route planning

Turning one off makes every command that needs it refuse with a message naming the key. `/debug
state` lists all five and their current state, and `/help <command>` shows which ones that command
needs.

## Plugins

`[Plugins]` is the one limit the plugin host puts on a plugin.

    CrashThreshold = 10       exceptions allowed inside the window
    CrashWindowSeconds = 60   the window, in seconds

Exceptions out of a plugin's event handlers, its commands and its scheduled callbacks are counted
together. Past the threshold inside the window the plugin is disabled and you are told, with its last
error, which `/plugins info` and `/plugins doctor` keep afterwards. The window slides, so a plugin
that throws once an hour is left alone and one that throws ten times in a minute is not.

Set `CrashThreshold = 0` to count exceptions and never disable anything, which is what you want while
developing a plugin.

This is not a sandbox. Plugins run in this process with your rights; what these two numbers contain
is a plugin throwing without end. See `/man plugins`.

## Language

`[Localization] Language` is the one language setting. It decides what MCC itself says: its own
messages, the comments in generated config files, the manual, and every plugin's own strings and
settings comments.

    Language = "auto"     follow the operating system (the default)
    Language = "pt_br"    a tag in Minecraft form
    Language = "pt-BR"    the same tag in BCP-47 form

Both spellings are accepted and normalised. A tag no known language matches falls back to `auto`
with a warning, so a typo never leaves the client speaking nothing.

`[Localization] LoadMccTranslation` is obsolete and can be deleted. It was a switch where a
language belongs: `true` meant what `auto` means now, and `false` meant English. It is still read
for one release, with a warning, and `false` is taken as `Language = "en"` unless the file already
names a language of its own.

`[ClientSettings] Locale` is a different axis: the locale announced to the server, which is what
server-side plugins and datapacks see. It stays `en_US` by default. Set it to `auto` to announce
the language above instead.

The language is applied once, at startup, straight after the configuration loads, so everything
printed afterwards follows it. `client.toml` itself is the exception on a first run: its comments
are written before the file that carries the preference exists, so they come out in the operating
system's language.

Only English ships today. The setting is live and everything reads it; the other languages are a
translation problem, not a code one.

## Resource packs

`[Localization] ResourcePackPolicy` decides what the server hears when it pushes a resource
pack, and therefore whether the session survives a strict server:

    ResourcePackPolicy = "accept"    report the pack as loaded (the default)
    ResourcePackPolicy = "decline"   always refuse; require-resource-pack servers will kick you
    ResourcePackPolicy = "prompt"    ask once per pack, in the console or the TUI input line

Anything but yes/y at the prompt declines. A host with no prompt, such as a headless
embedding, declines too. A declined pack the server marked required is logged as a warning,
because the kick that usually follows is the server's decision, not a client bug.

MCC is a console client and never renders pack textures or sounds. What it does take from an
accepted pack is language entries: with `[Localization] LoadResourcePackTranslations` on (the
default), the pack is downloaded over http/https with a size cap and a hash check, its
`assets/*/lang/*.json` entries for `en_us` plus your language are extracted and cached under
the configurations `cache/resourcepacks` folder, and chat renders against them until the
server pops the pack or the session ends. A failed download only loses those strings; the
server already got its answer.

### `/lang`

`/lang` with no argument reports what is actually in force: the configured value and the language
it resolved to, the locale being announced to the server, and, for each loaded plugin, the language
tags it ships, the one being read, and how many keys that one is missing against English.

`/lang <tag>` changes it. The tag is validated first, then written back into `client.toml` as
`Localization.Language`; only that one line changes, so every comment and every other key in the
file stays exactly as it was. `/lang auto` stores `auto` rather than the tag it happens to resolve
to today.

The change applies immediately to MCC's own text and to every loaded plugin's strings. It does not
touch settings files already on disk: comments there are frozen when the file is generated, the
same rule every generated file in this client follows. `/plugins settings all regen` rewrites them
in the new language, and `/lang` says so after a change.

## Reloading

`/reload` re-reads `client.toml` without restarting. It does not reopen the connection, so
connection settings take effect on the next `/reco`.

Writes only happen when you ask for one, such as saving an account or a server. Nothing is written
back as a side effect of starting or stopping.

## Unknown keys

A key with no matching setting is reported and ignored rather than failing the load. If you see
`Unknown config key`, check the spelling: the value is not being used.

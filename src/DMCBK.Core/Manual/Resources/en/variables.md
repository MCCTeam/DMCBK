# Variables

`/set name=value` stores a variable. `%name%` expands to it in any MCC command you type afterwards.

    /set home=150 80 380
    /move %home%

Expansion happens once, on the line you type, before the command is parsed. So a variable can hold
several arguments, as `home` does above.

## Where it does not expand

- Chat sent to the server. `%name%` reaches other players literally.
- Config file contents at load time.
- Inside a plugin's own settings.

## Predefined

`client.toml` has a `Variables` table for values that should exist at startup:

    Variables = { home = "150 80 380", base = "-200 64 940" }

Variables set with `/set` last for the session. Variables in `client.toml` come back every start.

## Naming

Anything without `=` or `%` in it. Case-sensitive. Setting a name that already exists replaces it,
and `/set` prints what it stored, so you can see it took.

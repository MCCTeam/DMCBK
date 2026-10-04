# Connecting

## Where the address comes from

Four sources, later ones win:

1. `client.toml` `[Connection]` `Host` and `Port`
2. The active entry in `servers.toml`
3. The last positional argument on the command line
4. A `--connection.host=` style flag

`/connect <name>` switches servers without restarting. The name is an entry in `servers.toml`, or a
literal `host:port`.

## Saving servers

`/servers add <name> <host[:port]>` writes an entry to `servers.toml` and selects it, so the next
`/connect <name>` and the next start already know it. Saving an existing name overwrites it.

In TUI mode the idle prompt opens a connect dialog instead: pick a saved server, or enter a new
host with an optional port and either connect once or save and connect.

## Starting without connecting

With no address at all, MCC starts anyway. It says

    [MCC] Not connected: no server is configured. Type '/connect <host[:port]>', or add one to servers.toml.

and gives you the prompt. Plugins load, commands run, and `/connect` dials when you are ready. A
Discord bridge, a scheduler or anything else that does not need a session works in this state; only
`/man`-documented game commands answer "not connected".

`[Connection]` `AutoConnect = false` does the same with a server configured: nothing is dialled until
`/reco` or `/connect`. It is per run as `--connection.auto-connect=false`, which is what a scripted
`plugins install` wants.

## Versions

`Version = "auto"` pings the server and uses what it reports. Pin a version when the server hides
its version, or when you want to force a protocol.

MCC speaks 50 protocols, 1.8 through 26.3. The version list is UMPK's dataset, not a switch statement
in MCC, so a new Minecraft release is a data update.

## Reconnecting

`[Connection.Reconnect]` in `client.toml`:

| Key | Default | Meaning |
| --- | --- | --- |
| `MaxAttempts` | 0 | 0 disables auto-reconnect. Negative means unlimited. |
| `DelaySeconds` | 5.0 | Wait before the first retry. |
| `BackoffFactor` | 1.0 | Multiply the delay after each failure. |
| `MaxDelaySeconds` | 120.0 | Cap the backoff. |
| `RetryOnKick` | true | Also retry after a kick. |

A kick is a decision the server made about you: a ban, a whitelist, an anti-bot rule. Set
`RetryOnKick = false` and MCC will still recover from dropped connections without walking back into a
server that just told it to leave.

`/reco` reconnects now. `/reco <account>` reconnects as somebody else.

## SRV records

`SrvResolve = "fast"` gives DNS 5 seconds before falling back to the literal host. Use `"no"` if your
network has no SRV records and you want to skip the lookup entirely.

## Proxies

`accounts.toml` `[Proxy]` handles HTTP, SOCKS4, SOCKS4a and SOCKS5. Login and in-game traffic are
separate switches, because a proxy that allows HTTPS for login often blocks the game port.

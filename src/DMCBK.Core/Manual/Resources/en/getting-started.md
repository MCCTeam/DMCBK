# Getting started

MCC connects to a Minecraft Java server as a headless client. You type commands, it plays.

## Your first connection

MCC writes four config files into a `configurations/` folder on first run. You need two of them:

- `accounts.toml` decides who logs in
- `servers.toml` decides where

If neither is filled in, MCC asks at startup. An online sign-in is requested when you
connect, and the choice is remembered after the first successful sign-in; later starts
print which account is used.

The command line does the same job without editing files:
`Mcc.Cli <username> <password|-> <host[:port]>`, with `--configurations <folder>` for a
different settings folder.

## Two kinds of command

Everything you type is one or the other.

- `/help`, `/move`, `/inventory` are handled by MCC. They never reach the server.
- Anything else goes to the server as chat, or as a server command if it starts with `/`.

If you type an MCC command name wrong, MCC forwards it to the server, which usually answers
"Unknown or incomplete command". When the name is close to a real one, MCC says so first.

To reach a server command that shares a name with an MCC one, double the slash: `//help` runs the
server's help, not this one.

## Finding your way

    /help              every command, grouped
    /help move         one command, with examples
    /help ui           browse live commands in TUI mode
    /man               this manual
    /man -k water      search the manual
    /man ui            browse commands and manuals in TUI mode

## Next

`/man connecting` covers servers and versions. `/man movement` covers making the bot walk.

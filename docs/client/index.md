# Build a client: a chaptered guide

This guide builds a small Minecraft Java client. The client connects, displays chat, reads player state, sends a message and stops. Each chapter explains one part of that application.

You do not need MCC source. Your project uses DMCBK packages. DMCBK uses UMPK packages for Minecraft authentication, networking and game state.

Start with the chapters in order. Later, use a chapter to check one topic.

| Chapter | What you build or learn |
| --- | --- |
| [1. Prepare the project](01-project.md) | Install .NET, select packages and create the project |
| [2. Connect and send chat](02-connect.md) | Build a client, connect and wait for player placement |
| [3. Observe game state](03-game.md) | Read snapshots, display events and select gameplay features |
| [4. Add configuration and a host](04-host.md) | Load TOML, present command output and support device-code login |
| [5. Add optional modules](05-modules.md) | Compose Commands, Beacon, Plugins and Marketplace |
| [6. Handle reconnect and shutdown](06-lifecycle.md) | Cancel work, reconnect and release resources |
| [7. Test and distribute the client](07-test-and-deploy.md) | Run protocol checks, package the app and understand platform limits |

## Terms used in this guide

| Term | Meaning |
| --- | --- |
| Host | Your application. It decides which UI, files and modules to use. |
| Client | One DMCBK object that owns configuration and connection management. |
| Session | One connection to one Minecraft server. A reconnect creates a new session. |
| Snapshot | A copy of game state at the time of a read. |
| Module | An optional service, such as commands or Beacon. |
| Cancellation token | A value that tells an asynchronous operation to stop. |
| NuGet | The package system that restores .NET libraries. |

## Complete working sample

The [ClientGuide sample](../../samples/ClientGuide/README.md) contains the final application and an executable check. The check uses an in-memory Minecraft protocol session. It needs no Minecraft installation, public server or Microsoft account.

The real connection mode needs a server. Use a server that you control for the first exercise. The guide uses offline authentication for that exercise. An online-mode server requires an online account and host authentication interaction.

## Before you start

You need basic C# knowledge: variables, methods, events and `async`/`await`. The guide explains how these concepts apply to DMCBK. It does not teach all C# syntax.

DMCBK is a preview library. Pin package versions. Check this guide again when you change those versions.

Continue to [Chapter 1](01-project.md).

# DMCBK documentation

A client application needs more than a network connection. It needs somewhere to store account state, a way to handle reconnects, and a clear boundary between game actions and its interface. DMCBK provides those services on top of [UMPK](https://github.com/MCCTeam/UMPK).

Start with a small client. Add commands, scripts or plugins when your application needs them. You can use game APIs without attaching any optional module.

## Choose a starting point

| Goal | Guide |
| --- | --- |
| Restore packages or build the repository | [Installation](getting-started/installation.md) |
| Connect a bot and send chat | [Your first client](getting-started/first-client.md) |
| Add a desktop, mobile or web interface | [Hosting](hosting.md) |
| Route commands or run a script | [Commands and Beacon](guides/commands-and-beacon.md) |
| Learn Beacon scripting in detail | [Beacon guides](beacon/index.md) |
| Build plugins in detail | [Plugin guides](plugins/index.md) |
| Write a C# plugin | [Plugin authoring and tests](guides/plugins.md) |
| Install a particular plugin version | [Marketplace v2](marketplace-v2.md) |
| Load TOML settings | [Configuration](reference/configuration.md) |
| Select packages | [Package reference](reference/packages.md) |
| Diagnose a missing capability | [Limitations and troubleshooting](reference/limitations.md) |
| Publish packages from GitHub | [NuGet release setup](releases.md) |

## Terms used in these guides

| Term | Meaning |
| --- | --- |
| Host | Your application. It supplies the interface, paths and prompts. |
| Client | One `DMCBK.Core.Client` instance. It owns its modules and connection lifecycle. |
| Session | One connection to a Minecraft server. A reconnect creates another session. |
| Module | An optional service attached through `ClientBuilder`. |
| Capability | A named feature that a host provides, such as `commands` or `beacon`. |
| RID | A runtime identifier, such as `win-x64`, that describes an operating system and process architecture. |
| Asset | One source or compiled archive for a plugin release. |

The examples use DMCBK `0.1.0-preview.1`, UMPK `0.9.0-beta.4`, .NET 10 and C# 14. Several public type names still start with `Mcc`. Those names are part of the current DMCBK API. They do not require MCC source.

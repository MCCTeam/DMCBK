# DMCBK documentation

A client application needs more than a network connection. It needs somewhere to store account state, a way to handle reconnects, and a clear boundary between game actions and its interface. DMCBK provides those services on top of [UMPK](https://github.com/MCCTeam/UMPK).

Start with a small client. Add commands, scripts or plugins when your application needs them. You can use game APIs without attaching any optional module.

## Follow a complete learning path

Each guide builds one working example through numbered chapters. Start at chapter 1. Complete the check at the end of each chapter before continuing.

| Guide | What you build | First requirement |
| --- | --- | --- |
| [Build a client](client/index.md) | A hosted client with game access, optional modules and verification | .NET 10 and basic C# |
| [Build a plugin](plugins/tutorial/index.md) | A session journal with settings, saved data, a command and Beacon integration | .NET 10 and a C# class library |
| [Write Beacon scripts](beacon/guide/index.md) | A script helper with functions, events, time and state | A Beacon host or the offline runner |

You can complete the offline and in-memory checks without a Minecraft account. Connecting to an actual server needs the server and account settings described in the client guide.

The guide text defines technical terms as they appear. Instructions use short, direct sentences. Code samples retain the exact API names that your compiler needs.

## Choose a starting point

| Goal | Guide |
| --- | --- |
| Use a coding agent for scripts or plugins | [Agent skills](agent-skills.md) |
| Restore packages or build the repository | [Installation](getting-started/installation.md) |
| Connect a bot and send chat | [Client chapters](client/index.md) or [first-client overview](getting-started/first-client.md) |
| Add a desktop, mobile or web interface | [Hosting](hosting.md) |
| Route commands or run a script | [Commands and Beacon](guides/commands-and-beacon.md) |
| Learn Beacon scripting in detail | [Beacon guides](beacon/index.md) |
| Build plugins in detail | [Plugin guides](plugins/index.md) |
| Write a C# plugin | [Plugin authoring and tests](guides/plugins.md) |
| Install a particular plugin version | [Marketplace v2](marketplace-v2.md) |
| Load TOML settings | [Configuration](reference/configuration.md) |
| Select packages | [Package reference](reference/packages.md) |
| Diagnose a missing capability | [Limitations and troubleshooting](reference/limitations.md) |
| Publish packages from GitHub | [Create a release](releases.md) |
| Run packaged installation checks | [Marketplace sample](../samples/MarketplaceGuide/README.md) |

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

The examples use DMCBK `0.1.0-preview.3`, UMPK `0.9.0-beta.4`, .NET 10 and C# 14. Former `Mcc`-prefixed types now use `Dmcbk`. Plugin authors do not need MCC source.

## Use examples correctly

A complete program includes its imports, setup and cleanup. A fragment demonstrates one operation inside the surrounding program. Follow each page's file and placement instructions before copying a fragment.

Expected output is a check, not decoration. Compare your output with the documented result. Read diagnostics before continuing when the result differs.

The runnable samples use pinned package versions. The tutorial verification programs use actual library and plugin code with simulated protocol sessions. They do not contact an external server unless you select the live connection path explicitly.

## More terms

| Term | Meaning |
| --- | --- |
| API | Types and methods that your code can call |
| NuGet | The .NET package system used to restore library dependencies |
| Restore | Download or locate the packages declared by a project |
| Build | Compile source code into assemblies |
| Entry class | The class that implements a plugin's `IPlugin` contract |
| Manifest | The `plugin.toml` file that describes an installable package |
| Scope | An owner that releases registrations when its lifetime ends |
| Cancellation token | A signal that asks asynchronous work to stop |
| Private dependency | A library loaded for one plugin's implementation |
| Exported contract | A shared assembly that defines types used between dependent plugins |
| Native library | Code compiled for a specific operating system and architecture |
| Hash | A digest used to check that downloaded bytes match expected bytes |
| Immutable | Fixed after publication or installation, rather than edited in place |
| Pin | A local policy that prevents changing a selected plugin version |
| Prerelease | A version such as `0.1.0-preview.3` that precedes a stable release |

## Report a documentation problem

Include the page, chapter, package versions and failing command. Include compiler diagnostics or the actual output. State whether you used an offline runner, scripted session or live server.

Do not include account tokens or private server credentials. A small reproducible example helps correct the guide more quickly than a description of a large application.

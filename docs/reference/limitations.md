# Limitations and troubleshooting

DMCBK is a preview. Pin package versions in applications and rebuild binary plugins after a breaking SDK change. API `1.0` and marketplace schema `2` define the current plugin contract.

## Common problems

| Symptom | Check |
| --- | --- |
| `client.Commands` throws `NotSupportedException` | Attach `UseCommands` before building. |
| Beacon or plugin composition fails | Attach Commands first. |
| Online login needs interaction | Implement `IAuthInteraction` in the host. |
| Position is unavailable after connection | Wait for spawn before reading position-dependent state. |
| A game command reports a disabled feature | Enable the corresponding gameplay feature and its dependencies. |
| A source plugin cannot find references | Supply accessible reference assemblies or an explicit compilation reference provider. |
| A mixed release has no matching binary | Enable source fallback explicitly, or select a supported release. |
| An install plan is stale | Create another plan from the current lock. |
| Old preview code appears after rebuilding packages | Restore with a new `NUGET_PACKAGES` directory. |

## Platform support

DMCBK's portable libraries contain no console or graphical UI dependency. A compatible .NET runtime still needs to support the selected packages, native libraries and transport.

Runtime plugins use dynamic assembly loading. Source plugins use Roslyn compilation. The current runtime does not support Native AOT unchanged. Mobile deployment restrictions require device tests. The repository contains host samples, not finished desktop or mobile clients.

A browser cannot use the ordinary Minecraft TCP transport directly. The web sample runs the client in a server backend. No browser transport relay ships here.

## Plugin trust and rollback

Plugins run in the host process with the host's permissions. A collectible load context separates assembly identities. It does not sandbox plugin code.

Package rollback restores the selected package graph. It preserves settings and data. It cannot undo external effects that a plugin already performed, such as a server command or HTTP request.

Old MCC plugin binaries and schema-1 catalogues require migration. The runtime rejects them rather than changing their format or deleting user data.

## Version support

Game protocol support comes from the pinned UMPK packages. Read [UMPK's version reference](https://github.com/MCCTeam/UMPK/blob/master/docs/reference/supported-versions.md) for protocol mappings. A newer UMPK repository checkout can describe versions that the installed package does not contain.

## Marketplace storage on macOS

Marketplace storage checks reject symbolic links in a path or its parents. macOS exposes `/var` through a system link.

1. Select a physical directory for plugin storage.
2. Use `/private/var` instead of `/var` when the system provides that physical directory.
3. Set `TMPDIR` to a physical directory before starting a host that imports development plugins.

The CI workflow uses its runner's physical temporary directory. Plugin-controlled links remain rejected.

## Diagnose a failure in order

1. Record the exact DMCBK and UMPK package versions.
2. Check whether the required module is attached.
3. Check whether a session exists.
4. Check whether the client reached the required game state.
5. Check the feature gates.
6. Read the operation result and diagnostics.
7. Reproduce the problem with the smallest relevant sample.

A connection, a session and a spawned player are different states. A transport connection alone does not make position, world or inventory data ready. Check the relevant state before using that data.

## What each kind of check proves

| Check | Evidence | Remaining uncertainty |
| --- | --- | --- |
| C# compilation | Names, types and signatures agree with the selected packages | Runtime behavior and server permissions |
| Offline Beacon execution | Parsing, expressions and simulated scheduling behave as checked | Actual Minecraft transport and game responses |
| Scripted protocol session | Lifecycle callbacks and the modeled packet interaction work | Unmodeled server behavior |
| Local Minecraft server test | The tested protocol and server configuration accept the behavior | Other versions, mods and configurations |
| Cross-platform compilation | The code builds for the selected target | Native library execution on that target |

The guides include reproducible offline and in-memory checks. Use a private live server for game actions that depend on server state. A simulated inventory does not prove every server will accept a click.

## Plugin loading failures

Inspect each entry from `PluginHost.List()`. An aggregate discovery or load result can succeed while an individual plugin reports an activation failure.

Check the manifest identity against `Configure`. Check the entry file, required capabilities, framework and compatibility ranges. For a source package, check compiler diagnostics and reference availability. For a compiled package, check private dependencies and the process target.

A native dependency needs the correct OS, architecture and Linux libc. A Windows x86 process needs x86 native dependencies even when the computer runs Windows x64. A portable managed assembly can still depend on a platform-specific native library.

## Cancellation and unload

Cancellation asks an operation to stop. It cannot interrupt arbitrary synchronous plugin code safely. A plugin must observe cancellation and release resources it creates.

Collectible load contexts permit eventual unloading after all references disappear. Retained delegates, tasks, static fields or native handles can keep code alive. Do not treat an unload request as proof that every file handle closes immediately.

Immutable installation directories let an update select another package without overwriting a loaded DLL. Cleanup can occur later when references and handles permit it.

## Keep a useful error report

Record the operation, package versions, process target, expected result and actual result. Include a minimal reproducer when possible. Include the relevant diagnostic identifiers and compiler errors.

Remove tokens, credentials, account files and private chat before sharing a report. Keep enough context to reproduce the failure. A screenshot of a generic failure message usually hides the information needed to diagnose it.

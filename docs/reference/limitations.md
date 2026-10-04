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

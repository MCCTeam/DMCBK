# Package reference

All DMCBK packages use version `0.1.0-preview.1`. They target .NET 10. Internal packages share one release version. Plugin releases have their own versions.

| Package | Responsibility | DMCBK dependencies |
| --- | --- | --- |
| `DMCBK.Core` | Client lifecycle, authentication, game APIs and host contracts | None |
| `DMCBK.Commands` | Command dispatch, completion and portable commands | Core |
| `DMCBK.Configuration` | Explicit TOML loading, validation and persistence | Core |
| `DMCBK.Beacon` | Script runtime, scheduling, lint and format APIs | Core, Commands |
| `DMCBK.PluginSdk` | Plugin author contracts, manifests, settings and services | Core |
| `DMCBK.Plugins` | Discovery, activation, source compilation and assembly loading | Core, Commands, PluginSdk |
| `DMCBK.Marketplace` | Release catalogues, dependencies and installation transactions | Core, PluginSdk |
| `DMCBK.Testing` | Plugin and script test hosts | Core, Beacon, PluginSdk, Plugins |
| `DMCBK` | Convenience package for a standard client | Core, Commands, Configuration |

Core accepts typed options. It does not read configuration files unless the host selects the Configuration package. Commands, Beacon and plugin loading do not appear automatically when you reference a package. The builder must attach each selected module.

## Choose a package set

| Application | Start with |
| --- | --- |
| Bot with direct game APIs | `DMCBK.Core` |
| Client with commands and TOML settings | `DMCBK` |
| Scripted client | `DMCBK` and `DMCBK.Beacon` |
| Plugin host | `DMCBK`, `DMCBK.Plugins`, optionally Beacon |
| Client with a marketplace | Plugin host packages and `DMCBK.Marketplace` |
| Plugin author | `DMCBK.PluginSdk` |
| Plugin test project | `DMCBK.Testing` |

UMPK remains the engine. DMCBK references the focused UMPK components that each package uses. Roslyn belongs to `DMCBK.Plugins`. UI frameworks belong to the host application.

XML API documentation ships with the packages. The source declarations also describe lifecycle and cancellation details. Start with [`ClientBuilder`](../../src/DMCBK.Core/ClientBuilder.cs), [`IPlugin`](../../src/DMCBK.PluginSdk/IPlugin.cs) and [`MarketplaceService`](../../src/DMCBK.Marketplace/MarketplaceService.cs).

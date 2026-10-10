# Package reference

All DMCBK packages use version `0.1.0-preview.6`. They target .NET 10. Internal packages share one release version. Plugin releases have their own versions.

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

## Package, namespace and module are different concepts

A package is a NuGet download. An assembly is a compiled `.dll` inside that package. A namespace groups C# types. A module is a service attached to a particular client.

These names do not always match. For example, the Beacon package exposes types in `DMCBK.Core.Beacon`. The plugin runtime exposes `PluginHost` in `DMCBK.PluginSdk`. Follow the `using` statements in the tested examples.

Installing `DMCBK.Beacon` gives your project its types. Calling `UseBeacon()` creates the script module for your client. The `DMCBK` convenience package does not include Beacon, Plugins or Marketplace.

## Pin versions explicitly

A preview version can change its public API in a later release. Pin the DMCBK package versions in your project file. Pin UMPK packages that your own code references directly.

```xml
<ItemGroup>
  <PackageReference Include="DMCBK.Core" Version="0.1.0-preview.6" />
  <PackageReference Include="DMCBK.Commands" Version="0.1.0-preview.6" />
  <PackageReference Include="DMCBK.Beacon" Version="0.1.0-preview.6" />
</ItemGroup>
```

Use the same DMCBK release for all DMCBK packages in one application. The plugin's own version is independent. A plugin compatibility range describes supported hosts. It does not download a different DMCBK runtime into that host.

## Separate author and host dependencies

A plugin author normally references `DMCBK.PluginSdk`. A host references `DMCBK.Plugins` to load that plugin. A test project references `DMCBK.Testing` to exercise plugin loading and sessions.

Do not copy host contract DLLs into a plugin archive. The host shares the SDK and UMPK contract identities. Private third-party dependencies belong to the plugin package. See [dependency loading](../plugins/dependencies-and-loading.md).

`DMCBK.Testing` provides test infrastructure. Applications do not need it for an ordinary connection. Tutorial verification programs use it so you can reproduce checks without an account or server.

## Use local packages during development

A local feed is a directory of `.nupkg` files. Pack the libraries into that directory. Add that directory as a NuGet package source in the consumer project.

NuGet caches packages by ID and version. Repacking the same preview version does not force every consumer to read the new bytes. Use a fresh package cache for a validation run. See [installation](../getting-started/installation.md) for complete commands.

Build a consumer outside the repository when you test package independence. That consumer must restore from NuGet packages. It must not reference a sibling source project.

## Find the correct level of documentation

Use the chaptered guides to create an application, plugin or script. Use the reference pages when you need an option or format. Use XML API comments for precise signatures and cancellation rules.

The source declarations are the final reference for this preview. A snippet from another version can compile incorrectly or use a different lifetime model.

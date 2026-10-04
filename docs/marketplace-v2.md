# Plugin marketplace v2

Marketplace metadata is separate from plugin payloads. Git stores source, manifests, release declarations and historical catalogues. Release archives contain the selected source file or compiled assembly, private dependencies, manuals, localization and default settings. Binary history lives in release assets.

The runtime accepts only schema 2. Migrate older catalogues, mutable manifest flags and binary plugins that use the MCC SDK. User settings are separate from package contents.

## Catalogue files

`mcc-marketplace.toml` lists identities and relative catalogue locations:

```toml
schema-version = 2
id = "official"
name = "Official DMCBK Plugins"

[[plugins]]
id = "example-plugin"
description = "An example plugin."
tags = ["automation"]
releases = "catalog/example-plugin.toml"
```

Each catalogue keeps the complete release history:

```toml
schema-version = 2
id = "example-plugin"

[[releases]]
version = "2.0.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.1 <0.2.0"
umpk = ">=0.9.0-beta.4 <0.10.0"
framework = "net10.0"
needs = ["commands"]
yanked = false
assets = [
  { kind = "compiled", target = "win-x64", url = "https://publisher.example/2.0.0/win-x64.zip", sha256 = "<64 hexadecimal characters>" },
  { kind = "compiled", target = "linux-x64", url = "https://publisher.example/2.0.0/linux-x64.zip", sha256 = "<64 hexadecimal characters>" },
  { kind = "source", target = "any", url = "https://publisher.example/2.0.0/source.zip", sha256 = "<64 hexadecimal characters>" },
]

[releases.requires]
shared-tools = "^2.1.0"

[releases.optional]
alerts = "^3.0.0"

[releases.hosts]
mcc = ">=2.0.0 <3.0.0"
```

The checksum placeholders above must be replaced by generated archive digests. Add another `[[releases]]` table for a new version. Payloads and compatibility metadata of a published release are immutable. Yank a release to remove it from ordinary selection without deleting its history.

API compatibility requires the same major and at least the requested minor. DMCBK and UMPK ranges check actual library versions independently of application versions. Omitted or empty `hosts` permits any compatible application. A populated table restricts applications to those identities and version ranges. `needs` requires capabilities actually composed by the host, such as `commands`, `beacon` or `aspnetcore`.

A shared SemVer implementation checks version ranges during resolution and loading. It accepts exact versions, comparators, caret, tilde, wildcard and alternative ranges. Ordinary requests exclude prereleases unless the host enables them. Exact prerelease requests remain exact. Required dependencies install transitively. Optional dependencies do not install automatically. They become available only when the installed version matches the declared range.

## Package manifest

Every archive has `plugin.toml` at its root:

```toml
schema-version = 2
id = "example-plugin"
version = "2.0.0"
kind = "compiled"
target = "win-x64"
entry = "ExamplePlugin.dll"
framework = "net10.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.1 <0.2.0"
umpk = ">=0.9.0-beta.4 <0.10.0"
needs = ["commands"]
deps = ["lib/ExampleHelpers.dll"]
man = ["example-plugin"]

[requires]
shared-tools = "^2.1.0"

[optional]
alerts = "^3.0.0"

[hosts]
mcc = ">=2.0.0 <3.0.0"

[exports]
assemblies = ["ExamplePlugin.Contracts.dll"]
```

For a source asset, use `kind = "source"`, `target = "any"` and `entry = "ExamplePlugin.cs"`. Runtime compilation accepts the single entry source file and declared private assemblies. It does not run project builds or arbitrary NuGet restores. Put manuals in `man/en`, localization in the plugin's resource files, and default settings in the package's `settings.toml`.

Do not package DMCBK, UMPK or host framework assemblies. The loader shares provider contracts from `exports.assemblies` through required dependency contexts. Other dependencies stay private to a collectible plugin load context. Compiled packages can carry `.deps.json` and native dependencies for `AssemblyDependencyResolver`.

## Asset selection

Detection uses the operating system, process architecture and Linux libc. A 32-bit process on Windows x64 selects `win-x86`.

| System | Targets |
| --- | --- |
| Windows | `win-x86`, `win-x64`, `win-arm64` |
| Linux glibc | `linux-x64`, `linux-arm64`, `linux-arm` |
| Linux musl | `linux-musl-x64`, `linux-musl-arm64`, `linux-musl-arm` |
| macOS | `osx-x64`, `osx-arm64` |
| Portable managed | `any` |

Selection prefers an exact compiled target, then compiled `any`. Source-only releases can select a matching source asset automatically. Mixed releases require explicit source fallback permission when no compiled asset matches. An explicit source preference selects source first. Unsupported targets produce an explanation rather than downloading every release.

Installation downloads one selected archive for each changed plugin in the required graph. The installer reuses unchanged installed selections. A portable managed plugin normally publishes one compiled `any` asset. Plugins with native dependencies publish only the targets they actually build and check.

## Planning and transactions

`MarketplaceInstaller.PlanInstallAsync`, `PlanUpdateAsync`, `PlanUninstallAsync`, `PlanPolicyAsync` and `PlanRollbackAsync` return an immutable `InstallPlan`. Its changes include exact versions, targets, hashes and affected dependents. `ApplyAsync` verifies the plan against the current lock while holding the installation-root lock. A stale plan must be recreated.

The resolver selects one version per identity, preserves compatible installed selections, respects pins and publisher bindings, and checks existing dependents. Conflicts, missing providers and required cycles fail before installation changes. Compatible optional providers can affect activation order without creating a cycle.

```text
plugins/
├── versions/<id>/<version>/<target>/<sha256>/
├── userdata/<id>/settings.toml
├── userdata/<id>/data/
├── cache/downloads/
├── cache/source/<id>/
├── transactions/
└── plugins.lock.toml
```

The generated lock has `schema-version`, a revision and `[[plugins]]` entries. Each entry records its marketplace, exact `release` and selected `asset`, enabled state, pin, update policy, and direct source/revision when applicable. It is the active graph, not a manifest edited in place. Do not hand-edit it during client operation.

The installer stages downloads and extraction. It checks digests, archive traversal, links, size limits and catalogue agreement. Source compilation runs before commitment. Affected plugins stop in reverse dependency order. Immutable packages and the lock commit before enabled plugins start in dependency order. Failed activation restores the previous graph. Startup recovers interrupted transactions before loading plugins. The installer never overwrites loaded DLLs.

Updates preserve settings and data. Defaults form an overlay. Cached rollback restores package selections and dependencies. Rollback cannot undo arbitrary effects from plugin code. Purging data is a separate explicit uninstall choice.

Update policies are `inherit`, `off`, `manual` and `automatic`. Pins prevent version changes. Remote checks honor the offline state and a 24-hour metadata cache. Startup checks use a short randomized delay. The installer defers live-session updates until disconnection. It creates a new plan before applying those updates.

## Authoring and local development

Use the templates and `PluginPack` in DMCBK-Plugins. Build against pinned NuGet packages with no MCC, DMCBK or UMPK checkout. For unpublished preview development, supply an explicit local package feed and use an isolated NuGet cache whenever replacing packages at the same preview version.

Local folder, archive, direct URL and Git imports use the same manifest checks and immutable installation layout. Git imports record the selected commit. These paths are development sources. Marketplace dependencies still need explicit source bindings.

The host must supply accessible compilation reference assemblies for source plugins. Standard MCC distributions keep managed assemblies accessible. Single-file or embedded hosts must explicitly provide reference assets.

## Review an installation plan

1. Add a publisher binding to the registry file.
2. Create a plan for the requested plugin version.
3. Display every change to the user.
4. Apply the plan after the user accepts the changes.

```toml
schema-version = 2

[[marketplaces]]
id = "official"
source = "https://publisher.example/mcc-marketplace.toml"
auto-update = "off"
```

The following code assumes the host composed Marketplace and Plugins as shown in [hosting](hosting.md).

```csharp
using DMCBK.Marketplace;

MarketplaceService market = client.GetModule<MarketplaceService>();
InstallPlan plan = await market.PlanInstallAsync(
    new ResolutionRequest("example-plugin", "official", ExactVersion: "2.0.0"));

foreach (PluginChange change in plan.Changes)
    Console.WriteLine($"{change.Id}: {change.PreviousVersion} -> {change.Version} ({change.Target})");

// Call this only after your host receives approval for the displayed plan.
PluginOperationResult applied = await market.ApplyAsync(plan);
```

The example URL and checksums are placeholders. Replace them with real release metadata before installation. `ApplyAsync` checks the current lock again. A changed lock invalidates the plan, so the host must request a new plan.

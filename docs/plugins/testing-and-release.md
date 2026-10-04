# Test and release a plugin

Compilation checks syntax and references. Runtime tests also check loading, callbacks, assembly identity and cleanup.

## Test a source plugin

1. Create a .NET 10 test project.
2. Add `DMCBK.Testing` version `0.1.0-preview.1`.
3. Load the real source entry through `PluginTestHost`.
4. Check the individual plugin's loaded state.
5. Run an in-memory session.

The [complete test example](../guides/plugins.md#test-without-a-live-server) checks a session callback without a live Minecraft server.

Use `WaitForAsync` for conditions driven by the event thread. Avoid fixed sleeps that assume a callback has already run.

| Test | What it should prove |
| --- | --- |
| Load | Manifest, compilation and activation succeed |
| Reconnect | Each new session gets fresh registrations |
| Stop and unload | Owned commands, subscriptions and tasks disappear |
| Settings | Defaults, normalization and user values work |
| Dependency absence | Optional functionality degrades without a crash |
| Exported contracts | Provider and consumer see the same CLR contract types |
| Native loading | The packaged native library loads on the declared target |
| Game behavior | A controlled server accepts the intended action |

A source example that compiles in an author project can still fail runtime compilation due to missing explicit usings or references.

## Package source or compiled output

```text
session-counter/
├── plugin.toml
├── SessionCounter.cs             # source asset only
├── SessionCounter.dll            # compiled asset only
├── SessionCounter.deps.json      # when required for dependency resolution
├── lib/                          # private helpers
├── lang/en.toml
├── man/en/session-counter.md
└── settings.toml                 # optional defaults
```

Source and compiled assets have separate manifests and archives. Each archive declares its own `kind`, `entry` and `target`.

For a compiled asset, change these manifest fields:

```toml
kind = "compiled"
target = "any"
entry = "SessionCounter.dll"
```

1. Build the author project in Release mode.
2. Copy the entry DLL and required private dependencies into a staging directory.
3. Exclude DMCBK, UMPK and host framework assemblies.
4. Add the matching manifest and resources.
5. Archive the staging contents with the manifest at the root.
6. Calculate the archive's SHA-256 checksum.

Compiling a complex project belongs in the author repository. Runtime installation does not run arbitrary project builds or NuGet restores.

## Select runtime targets

| Platform | Targets |
| --- | --- |
| Portable managed code | `any` |
| Windows | `win-x86`, `win-x64`, `win-arm64` |
| Linux with glibc | `linux-x64`, `linux-arm64`, `linux-arm` |
| Linux with musl | `linux-musl-x64`, `linux-musl-arm64`, `linux-musl-arm` |
| macOS | `osx-x64`, `osx-arm64` |

Target selection follows the running process. An x86 process on Windows x64 selects `win-x86`.

Linux x86 and macOS 32-bit are not supported targets. A cross-build does not prove that a native library executes on its target.

Most managed plugins need one `any` archive. Native plugins need separate assets for their supported targets.

## Publish a marketplace release

1. Assign a new SemVer plugin version.
2. Build and test every declared asset.
3. Generate archives and checksums.
4. Upload the immutable release assets.
5. Check each uploaded archive and checksum.
6. Add the release entry to the plugin's catalogue.
7. Publish the catalogue update after its assets are available.

Use [marketplace v2](../marketplace-v2.md) for the complete index, release catalogue and lock schemas.

A release can contain source assets, compiled assets or both. Installation downloads one selected asset per plugin in the resolved dependency graph.

Selection prefers an exact compiled target, then compiled `any`. Source-only releases compile automatically. Mixed releases need explicit source fallback permission when compiled assets do not match.

`api-version` constrains the API contract. `dmcbk` and `umpk` constrain library versions. `[hosts]` can restrict compatible application identities and versions.

Plugin versions are independent of DMCBK and application versions. A changed published payload or compatibility declaration needs a new release version.

Yank a broken release instead of deleting its history. Keep prior immutable assets available for reproducibility and rollback.

## Development paths

Use an explicit development folder for local iteration. Keep user data in a separate plugin root.

Marketplace paths reject symlink ancestors. On macOS, use a physical path instead of a `/var` alias when running development imports. See [limitations](../reference/limitations.md).

Reload after changing code or resources. Unload can be delayed by retained references or native handles, so never overwrite an installed loaded DLL.

Return to the [plugin index](index.md).

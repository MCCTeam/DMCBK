# Session Journal tutorial plugin

This project is the final artifact from the [eight-chapter plugin guide](../../../docs/plugins/tutorial/index.md).

It counts session starts, saves the count, reads a validated increment setting, registers `journal-count`, and exposes `journal_count` to Beacon.

## Build and verify

Run these commands from the DMCBK repository root:

```sh
dotnet build samples/PluginAuthoring/SessionJournal -c Release
dotnet run --project samples/PluginAuthoring/VerifyPlugin -- samples/PluginAuthoring/SessionJournal
```

Use the [local package feed](../../../docs/getting-started/installation.md) when this preview is not available from your configured NuGet sources. The project uses packages only. It can build outside a DMCBK checkout.

The expected final line is:

```text
PASS: load, two fresh sessions, storage, reload, Beacon and command cleanup.
```

The verifier creates two clients with in-memory protocol sessions. They share a temporary user-data root. It checks source compilation, session counting, data persistence across clients, reload, localized command output, the Beacon bridge, and command removal after unload.

The harness closes its transport after one session. This test does not prove same-client reconnect or live-server gameplay.

## Create a source asset

Copy `plugin.toml`, `SessionJournal.cs`, `defaults`, `lang`, and `man` into a clean staging directory. Archive those contents with the manifest at the root.

The author project and `read-count.bcn` are development aids. The plugin source loader compiles only the manifest's entry file.

## Create a compiled asset

1. Build in Release mode.
2. Copy `SessionJournal.dll` and `SessionJournal.deps.json` into staging.
3. Copy the same manifest and resource directories.
4. Set `kind = "compiled"`.
5. Set `entry = "SessionJournal.dll"`.
6. Keep `target = "any"`.
7. Run the verifier with the staging path.
8. Archive the verified staging contents.

Do not include host-provided DMCBK, UMPK, or framework DLLs.

## Change the setting

The packaged default is `defaults/settings.toml`. The runtime writes user settings under `userdata/session-journal/settings.toml`.

Set `Increment` from 1 to 100. Reload the plugin after editing. The persisted count belongs in `userdata/session-journal/data/storage.toml`.

The supplied verifier expects the sample's default increment of one.

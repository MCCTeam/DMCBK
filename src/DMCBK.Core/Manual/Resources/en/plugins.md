# Plugins

Plugins extend a DMCBK client with commands, automation, integrations and Beacon functions. This host uses schema-2 manifests and versioned marketplaces. Older marketplace formats and binaries using the previous MCC SDK require migration.

## Install and inspect

```text
/plugins marketplace add MCCTeam/DMCBK-Plugins
/plugins search fishing
/plugins install auto-fishing@official yes
/plugins install auto-fishing@official 2.0.0 yes
/plugins info auto-fishing
/plugins deps auto-fishing
/plugins list
```

Choose a publisher explicitly when the same identity appears in multiple marketplaces. Exact versions never silently change to another version. The resolver checks API, DMCBK, UMPK, application, framework and host capability constraints before planning changes. Required plugin dependencies are resolved together; existing dependents and pins remain constraints.

Installation selects one archive for each changed plugin in the required graph. A matching compiled platform asset is preferred, then portable compiled `any`. Source-only releases compile automatically. Mixed releases require `--source-fallback` before using source when compiled assets do not match; `--source` explicitly prefers source. An incompatible release reports why it cannot be installed.

Targets cover Windows x86/x64/ARM64, supported Linux glibc and musl architectures, macOS x64/ARM64, and portable managed `any`. The process architecture determines the asset; Windows x86 processes select x86 even on x64 machines.

The shared confirmation shows every affected plugin, its version, publisher, target and digest. Downloads are staged, checked and validated before activation. Activation failures restore the previous package graph. Plugins run in-process and should be obtained from publishers you choose to trust.

## Runtime controls

```text
/plugins enable auto-fishing
/plugins disable auto-fishing
/plugins reload auto-fishing
/plugins unload auto-fishing
/plugins settings auto-fishing regen
/plugins settings auto-fishing reset
/plugins validate
/plugins doctor
/plugins ui
```

Enable and disable state lives in the installation lock. Immutable manifests have no enabled flag. Settings regeneration preserves values; reset writes defaults. Settings and data remain separate from versioned packages. Required dependencies load before their dependents, and affected dependents stop first during changes.

Use `plugins new <id>` to scaffold a local source plugin or `plugins load <path>` for explicit development loading. Development imports also accept folders, archive URLs and Git sources with schema-2 manifests; Git revisions are recorded immutably. Installed packages do not overwrite the development source folder.

## Updates and rollback

```text
/plugins outdated
/plugins update auto-fishing yes
/plugins update all yes
/plugins pin auto-fishing
/plugins unpin auto-fishing
/plugins rollback <transaction-id>
/plugins uninstall auto-fishing
/plugins uninstall auto-fishing purge
```

Pins preserve selected versions during manual and automatic updates. Updates preserve user settings and data. Cached rollback restores package versions and dependency selection; it cannot reverse arbitrary effects performed by plugin code. Uninstall normally retains user data, while `purge` explicitly deletes it after removal.

Marketplace policies are configured through `plugins marketplace auto-update <name> off|check|apply`. Per-plugin lock policies are `inherit`, `off`, `manual` and `automatic`. Remote checks use a short startup delay and a 24-hour metadata cache. Offline mode and local sources skip unattended remote checks. Changes found during a live connection wait until disconnection, then are replanned against current pins and policy.

## Marketplace files and storage

The schema-2 `mcc-marketplace.toml` index points to `catalog/<id>.toml`. Catalogues hold all releases, dependency ranges and hashed source or compiled assets. Published payloads are immutable; new payloads require new versions. Yanked releases remain in history and are excluded from ordinary installation and updates.

```text
plugins/
  versions/<id>/<version>/<target>/<sha256>/
  userdata/<id>/settings.toml
  userdata/<id>/data/
  cache/downloads/
  cache/source/<id>/
  transactions/
  plugins.lock.toml
```

The host chooses the plugin root. The lock records exact releases, selected assets, publisher bindings, enabled state, pins and update policy. Packages are immutable. Startup recovers interrupted transactions before activating plugins. Loaded DLLs are never overwritten.

The TUI plugin manager uses the same marketplace service as these commands. It offers installed plugin details, settings, release/version selection, dependency changes, source preference, pinning and rollback. For author contracts and manifests, read `man writing-plugins` and DMCBK's marketplace-v2 documentation.

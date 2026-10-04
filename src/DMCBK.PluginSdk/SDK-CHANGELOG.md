# DMCBK.PluginSdk changelog

## 1.0

Initial DMCBK contract, released with DMCBK 0.1.0-preview.1. Existing MCC SDK binaries must be rebuilt.

- IPlugin configuration, activation and bounded deactivation; reconnect-aware session scopes.
- Host-neutral API, library, engine, application, runtime target and capability information.
- Schema-2 immutable manifests, semantic version ranges and required/optional plugin dependencies.
- Commands, settings, persistent storage, localization, manuals and crash isolation.
- Messaging, typed services, exported contracts, channels, packet observation and scheduling.
- Beacon extension functions, variables, custom events and exported script calls.
- Explicit source reference contracts and DMCBK_API_1_0 conditional compilation symbols.

The same API major and at least the declared minor are required. DMCBK, UMPK and optional application ranges are checked independently. Prereleases follow semantic version ordering.

See the [marketplace reference](../../docs/marketplace-v2.md) and [plugin guide](../../docs/guides/plugins.md) for formats and examples.

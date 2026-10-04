# Make DMCBK plugins

A plugin extends a client through `DMCBK.PluginSdk.IPlugin`. It can add commands, react to sessions, store settings and expose services to other plugins or Beacon.

Plugin authors restore NuGet packages. They do not need MCC source, a Git submodule or a DMCBK source checkout.

| Guide | What you will build or learn |
| --- | --- |
| [First plugin](getting-started.md) | A complete source plugin and manifest |
| [Lifecycle and commands](lifecycle-and-commands.md) | Session ownership, reconnect cleanup and a Brigadier command |
| [Settings, storage and localization](settings-and-resources.md) | User options, durable data, translated text and manuals |
| [Dependencies and assembly loading](dependencies-and-loading.md) | Required providers, optional services and exported contracts |
| [Beacon integration](beacon-integration.md) | A function that scripts can call |
| [Testing and release](testing-and-release.md) | In-memory tests, source/compiled packaging and platform assets |

Start with one source file for a small plugin. Use a compiled project when you need several source files, NuGet dependencies or native libraries.

All examples use API `1.0`, schema `2`, DMCBK `0.1.0-preview.1`, UMPK `0.9.0-beta.4` and .NET 10.

Marketplace v2 rejects old manifests. Existing MCC binary plugins must be rebuilt against DMCBK. See [marketplace v2](../marketplace-v2.md) for installation and release formats.

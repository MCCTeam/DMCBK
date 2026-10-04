# Configuration

Core can use code-only configuration. `UseServer`, `UseAccount` and `UseFeatures` supply typed values. It does not create files as a side effect of construction.

The Configuration package adds TOML files and persistence. Its public API currently uses names such as `DmcbkConfigurationLoader` and `DmcbkConfiguration`. These types live in DMCBK assemblies.

## Load files explicitly

1. Select a writable configuration directory.
2. Register `UseConfigurationsFolder` with that directory.
3. Set `generateMissing` to `true` only when you want default files.
4. Complete the account and server settings before connecting.

```csharp
using DMCBK.Core;

string folder = Path.Combine(Path.GetTempPath(), "dmcbk-config-example");

await using Client client = new ClientBuilder()
    .UseConfigurationsFolder(folder, generateMissing: true)
    .UseCommands()
    .Build();
```

An explicit `generateMissing: true` can create default files. An existing active account or server entry can override connection defaults. The example creates a client but does not connect.

| File | Purpose |
| --- | --- |
| `client.toml` | Connection, gameplay, chat, localization, logging and plugin options |
| `accounts.toml` | Accounts, token-cache paths and proxy configuration |
| `servers.toml` | Named servers and active server selection |

Terminal preferences such as emoji glyphs and console layout belong to the application. They are not portable Core settings.

## Feature dependencies

Physics needs terrain. Pathfinding needs physics. A programmatic feature selection normalizes those dependencies upward. Disabling only terrain while leaving physics enabled can therefore enable terrain again.

Set all dependent features when you want a smaller client:

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("ChatOnly")
    .ConfigureFeatures(features =>
    {
        features.Terrain = false;
        features.Physics = false;
        features.Pathfinding = false;
        features.Inventory = false;
        features.Entities = false;
    })
    .Build();
```

File configuration validates explicit switches together. Read diagnostics before accepting a loaded configuration. The loader can reload files and persist account, server and language changes. An application must decide when a reload should affect its client.

## Storage ownership

1. Keep account files and token caches outside Git.
2. Use a separate data root for each independent client.
3. Pass plugin and marketplace paths explicitly.
4. Keep user settings outside immutable plugin packages.

Script file operations use the configured source folder. Plugins use the root selected through `PluginOptions`. Marketplace locks record package selection separately from user data. See [marketplace storage](../marketplace-v2.md#planning-and-transactions).

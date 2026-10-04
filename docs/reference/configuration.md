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

Script discovery and editing use the host-selected script source directory. Beacon data helpers use its shared `data` subdirectory. Each helper file has a 1 MiB size limit. Plugins use the root selected through `PluginOptions`. Marketplace locks record package selection separately from user data. See [marketplace storage](../marketplace-v2.md#planning-and-transactions).

## Understand TOML before editing

TOML is a text format for settings. A line assigns a value to a key. A table groups related keys.

```toml
[Connection]
Host = "localhost"
Port = 25565
Version = "auto"
```

This fragment belongs in `client.toml`. It changes the connection defaults. It is not a complete account configuration.

Strings need quotation marks. Boolean values are `true` or `false`. A line that begins with `#` is a comment. Use the generated spelling and table names. Marketplace manifests use different names, such as `schema-version`.

## Create a local test configuration

Use an offline account only with a private server that permits offline login. Microsoft account authentication needs a host authentication interface.

1. Create a separate configuration directory.
2. Generate the default files with the earlier example.
3. Replace `accounts.toml` with this content.

```toml
Active = "local-bot"
SessionCache = "memory"
ProfileKeyCache = "memory"
CacheDirectory = "cache"

[[Account]]
Name = "local-bot"
Kind = "offline"
Login = "GuideBot"
```

4. Replace `servers.toml` with this content.

```toml
Active = "local-test"

[[Server]]
Name = "local-test"
Host = "localhost"
Port = 25565
Version = "auto"
Kind = "normal"
```

`Name` is the local label for an entry. `Login` is the offline player name. `Active` selects an entry by its local label. The selected server takes precedence over `[Connection]` defaults.

`Version = "auto"` lets connection setup determine the protocol. Use a supported explicit version when automatic detection cannot identify your server correctly.

## Inspect load warnings

The convenience builder loads files immediately. Use the loader directly when your application must inspect warnings before accepting a configuration.

```csharp
using DMCBK.Core;
using DMCBK.Core.Configuration;

string folder = Path.GetFullPath("client-data/configuration");
var loader = new DmcbkConfigurationLoader(folder);
ConfigurationLoadResult loaded = loader.Load(generateMissing: false);
foreach (ConfigurationWarning warning in loaded.Warnings)
    Console.WriteLine(warning.Message);

if (loaded.Warnings.Count != 0)
    throw new InvalidOperationException("Correct the configuration warnings before connecting.");

await using Client client = new ClientBuilder()
    .UseConfiguration(loaded.Config)
    .UseConfigurationStorage(folder)
    .Build();

Console.WriteLine($"Endpoint: {loaded.Config.ResolvedHost}:{loaded.Config.ResolvedPort}");
```

This example deliberately refuses warnings. Your application can choose a different policy. The loader can recover from missing, unreadable or malformed files with defaults. A successful return therefore does not mean the loader accepted every supplied setting.

`loaded.Generated` reports whether the explicit generation request wrote missing files. `loaded.Config` is the resulting configuration snapshot. `loaded.Warnings` contains recoverable diagnostics, including unknown keys and invalid values.

`UseConfiguration` applies the snapshot. `UseConfigurationStorage` attaches persistence for the same folder. It does not load that folder again.

## Reload and save deliberately

`loader.Reload()` reads the files again and returns a new snapshot. It also raises `loader.Reloaded`. Previously returned snapshots keep their previous values.

A reload of a loader does not mean that every running module now uses the new snapshot. The host must decide how to apply it. Command configuration has an explicit `ReloadConfiguration` method. Connection changes can require a new connection. Plugin settings use their own settings service.

`SaveAccount` and `SaveServer` update their respective files. They can make the saved entry active. These methods serialize the file again, so custom comments in those files can change. `TrySaveLanguage` preserves the surrounding `client.toml` text while changing the language line.

1. Read the new snapshot.
2. Review all warnings.
3. Identify the affected modules.
4. Apply supported changes through their APIs.
5. Reconnect when the endpoint or account changes.

## Configuration sections

| Section | What it controls | What it does not do |
| --- | --- | --- |
| `Connection` | Endpoint defaults, version, timeout and reconnect policy | Start an application-owned input loop |
| `Gameplay` | Terrain, inventory, entities, physics and pathfinding | Guarantee server permission for an action |
| `Chat` | Outgoing chat behavior and message parsing options | Prove that another player received a message |
| `ClientSettings` | Settings announced to the server | Implement a graphical interface |
| `Localization` | Library language selection | Translate plugin messages automatically |
| `Logging` | Library logging preferences | Replace a host logger implementation |
| `Diagnostics` | Diagnostic recording options | Make diagnostic files safe to publish without review |
| `Plugins` | Runtime limits and plugin preferences | Attach the optional Plugins module |
| `Permissions` | Command prefix and owner settings | Provide a security sandbox for plugins |
| `Variables` | Initial string variables | Persist all runtime changes automatically |

Generate the current defaults to see the complete schema and comments. Prefer those files over copying settings from an older MCC configuration.

## Diagnose configuration errors

| Problem | Explanation | Action |
| --- | --- | --- |
| A setting appears to do nothing | The key can be unknown or a selected entry can override it | Read warnings and inspect the resolved values |
| Files appear in an unexpected directory | A relative path depends on the process directory | Pass an absolute folder path |
| The client uses the wrong account | `Active` selects a different account entry | Check `accounts.toml` and the resolved account |
| Terrain becomes enabled again | Physics or pathfinding still needs terrain | Disable the dependent features together |
| A file edit does not change the running client | The client retains its earlier snapshot | Reload and apply the supported change explicitly |
| Defaults appear after a syntax error | The loader reports the error and uses defaults | Correct the file before connecting |

## Name client variables safely

The client variable store holds text values and compares names without case sensitivity. Use letters, digits and underscores in names, such as `session_journal_sessions`.

The current store truncates a supplied name at its first unsupported character. For example, `guide.count` and `guide.status` both become `guide`. They would overwrite the same value. Use `guide_count` and `guide_status` instead.

```toml
[Variables]
guide_label = "Local test"
guide_count = "0"
```

This table belongs in `client.toml`. Variable values are strings. Commands can expand `%guide_label%` through the same store.

These client variables differ from Beacon globals, Beacon map fields and plugin storage keys. Those APIs have their own naming and persistence rules.

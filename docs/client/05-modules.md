# Chapter 5: Add optional modules

A package makes code available to your project. A builder extension attaches that code to one client. This distinction keeps Core usable in small hosts.

## Attach Commands and Beacon

1. Add both packages.

```bash
dotnet add package DMCBK.Commands --version 0.1.0-preview.7
dotnet add package DMCBK.Beacon --version 0.1.0-preview.7
```

2. Attach Commands before Beacon.

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseUsername("GuideBot")
    .UseCommands()
    .UseBeacon()
    .Build();

Console.WriteLine(string.Join(", ", client.AvailableCapabilities.Order()));
```

Construction installs the services and local script command. It does not start a connection. The printed capabilities include `commands` and `beacon`.

A Core-only client has no command dispatcher. Accessing `client.Commands` without Commands reports an unavailable module. Beacon uses command registration, so its builder extension requires Commands first.

## Distinguish local commands from server commands

`client.Commands.DispatchAsync("set name=value")` invokes a local client command. `client.Game.Chat.SendAsync("/time query daytime")` sends a command to the Minecraft server.

`HandleInputAsync` routes user input according to the configured prefix. Use that API when your application offers a command-entry box. Use `DispatchAsync` when your code already selects a local command.

A command result has `Status`, `Message`, `IsSuccess` and optional `Data`. Render the message or typed data. Do not assume every result is text-only.

## Add plugins and a marketplace

This complete composition example creates services without connecting:

```csharp
using DMCBK.Core;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;

string dataRoot = Path.Combine(Path.GetTempPath(), "dmcbk-modules", Guid.NewGuid().ToString("N"));
string pluginRoot = Path.Combine(dataRoot, "plugins");

await using Client client = new ClientBuilder()
    .UseUsername("GuideBot")
    .UseApplication(new HostApplication("guide-client", "1.0.0", new HashSet<string>()))
    .UseCommands()
    .UseBeacon()
    .UsePlugins(new PluginOptions(pluginRoot))
    .UseMarketplace(new MarketplaceOptions(pluginRoot, Path.Combine(dataRoot, "marketplaces.toml"))
    {
        Runtime = current => current.GetModule<IPluginInstallationHost>()
    })
    .Build();

client.Scripts.SetMuted(false);
PluginHost plugins = client.GetModule<PluginHost>();
await plugins.LoadAllAsync();

foreach (var plugin in plugins.List())
    Console.WriteLine($"{plugin.Id}: loaded={plugin.Loaded}");
```

Add Core, Commands, Beacon, Plugins and Marketplace packages for this example. PluginSdk is a dependency of Plugins. An independent author project should reference the SDK directly when it uses author contracts.

Initialize Beacon before plugin activation when plugin activation registers functions for pre-session script calls. Beacon initializes lazily. `SetMuted(false)` creates the script engine for this case.

`LoadAllAsync` reports an aggregate operation result. Inspect each plugin's `Loaded` state and diagnostics. One successful aggregate operation does not prove that every discovered plugin activated.

The host identity has a stable application ID and its own version. Plugin host restrictions use those values. Your application version is separate from the DMCBK package version.

## Select storage explicitly

Use a persistent directory in an installed application. Plugin version directories contain package files. User settings and data belong outside those directories.

Marketplace installation resolves dependencies and selects one asset for each plugin. It does not download every historical version or platform build.

See the [plugin guide](../plugins/index.md) for authoring and activation. See [marketplace v2](../marketplace-v2.md) for catalogues, asset selection and transactions.

## Continue with scripts

A Beacon script uses a `.bcn` file. The Beacon language still uses the `mcc` verb for local client commands. The file extension and language verb are separate concepts.

The [Beacon guide](../beacon/index.md) explains script creation, capabilities, tests and host integration.

Continue to [Chapter 6](06-lifecycle.md).

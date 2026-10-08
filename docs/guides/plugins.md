# Write and test a plugin

For the detailed guide and reference pages, see [the full documentation](../plugins/index.md).

A plugin implements `DMCBK.PluginSdk.IPlugin`. The SDK contains author contracts. `DMCBK.Plugins` loads packages and compiles source entries. Plugin authors do not need MCC source.

## Write the entry class

1. Create a .NET 10 class library.
2. Add `DMCBK.PluginSdk` version `0.1.0-preview.2`.
3. Add an entry class that implements `IPlugin`.
4. Add the matching `plugin.toml` manifest.

```csharp
using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class SessionCounter : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-counter";
        descriptor.Version = "1.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
    }

    public Task ActivateAsync(PluginContext context)
    {
        int sessions = 0;
        context.SessionStarted += (_, _) =>
        {
            sessions++;
            context.Variables.Set("session_counter_sessions", sessions.ToString());
        };
        return Task.CompletedTask;
    }
}
```

`Configure` declares identity and settings. It must not perform I/O or game actions. `ActivateAsync` can run before the first session exists. Subscribe to `SessionStarted` for work that must repeat after reconnect.

Session scopes release registered packet observers, commands, channels, timers and movement leases when the session ends. `DeactivateAsync` must release resources that the plugin creates itself, such as an HTTP client or background task. It must respect its cancellation token.

## Source package

Save the entry class as `SessionCounter.cs`. Use this manifest:

```toml
schema-version = 2
id = "session-counter"
version = "1.0.0"
kind = "source"
target = "any"
entry = "SessionCounter.cs"
framework = "net10.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.2 <0.2.0"
umpk = ">=0.9.0-beta.4 <0.10.0"
needs = ["commands"]
```

Runtime source loading compiles one entry `.cs` file. Private helper assemblies can accompany it. It does not restore NuGet dependencies or build arbitrary project files. A compiled package uses `kind = "compiled"` and names its entry DLL. See [marketplace formats](../marketplace-v2.md) for dependencies and platform assets.

## Load a local plugin

This example uses an explicit development folder. It needs the Commands and Plugins packages. `pluginFolder` must contain the manifest and entry file.

```csharp
using DMCBK.Core;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

string pluginFolder = Path.GetFullPath("session-counter");
string pluginRoot = Path.GetFullPath("plugin-data");

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("KitBot")
    .UseCommands()
    .UsePlugins(new PluginOptions(pluginRoot)
    {
        DevelopmentFolders = [pluginFolder]
    })
    .Build();

var runtime = client.GetModule<PluginHost>();
var loaded = await runtime.LoadAllAsync();
if (!loaded.Success)
    throw new InvalidOperationException(loaded.Message);

if (!runtime.List().Single(plugin => plugin.Id == "session-counter").Loaded)
    throw new InvalidOperationException("The plugin did not activate.");
```

Start the client after loading the plugins. The host must provide accessible managed reference assemblies for source compilation. A host that embeds assemblies must supply `PluginOptions.CompilationReferences` explicitly.

## Test without a live server

The testing package creates an in-memory protocol session. This example compiles a real source plugin and checks its session callback.

```csharp
using DMCBK.Testing;

await using PluginTestHost host = PluginTestHost.Create();
host.AddSourcePlugin("example", """
using System.Threading.Tasks;
using DMCBK.PluginSdk;
public sealed class Example : IPlugin
{
    public void Configure(PluginDescriptor descriptor) => descriptor.Id = "example";
    public Task ActivateAsync(PluginContext context)
    {
        context.SessionStarted += (_, _) => context.Variables.Set("example_started", "yes");
        return Task.CompletedTask;
    }
}
""");

var loaded = await host.LoadAsync();
if (!loaded.Success)
    throw new InvalidOperationException(loaded.Message);

if (!host.Plugins.List().Single().Loaded)
    throw new InvalidOperationException("The plugin did not load.");

await host.RunSessionAsync(async _ =>
{
    bool started = await host.WaitForAsync(
        () => host.Client.Variables.Get("example_started") == "yes");
    if (!started)
        throw new InvalidOperationException("The session callback did not run.");
});
```

`WaitForAsync` waits for the session callback on the client event thread.

This test checks runtime loading and the callback against a scripted protocol session. It does not replace live server tests for game behavior or native execution on another operating system.

## Settings and shared services

| Contract | Use |
| --- | --- |
| `descriptor.WithSettings<T>()` | Declare the plugin's typed settings. |
| `context.Settings` | Load, check and save user settings. |
| `context.Storage` | Store plugin data in its assigned directory. |
| `context.Strings` | Read the plugin's localized messages. |
| `context.Messenger` | Exchange messages with other plugins. |
| `context.Services` | Publish and request typed plugin services. |
| `context.Commands` | Register plugin commands with a lifetime scope. |
| `context.Beacon` | Add script functions and events. |
| `context.Host` | Read API, library, application and capability information. |

For exported contracts, declare provider assemblies under `[exports]`. Declare the provider as a required dependency in consumers. Use [DMCBK-Plugins](https://github.com/MCCTeam/DMCBK-Plugins) for author templates and packaging examples.

## Continue with the chaptered plugin guide

This page is a compact overview. Use the [plugin documentation](../plugins/index.md) for the full learning path and reference topics. The chaptered guide creates a project, adds a manifest and builds a real plugin. It then adds settings, durable data, a command, session behavior and Beacon integration.

A source file is not a marketplace. A plugin project is not an installable archive. A loaded plugin is not necessarily connected to a server. The full guide explains each stage and provides checks for the result.

## Understand the trust model

A plugin runs inside your application's process. It can access files and network resources with the application's permissions. A separate load context controls assembly loading. It does not limit operating system access.

Review source or trust the publisher before installing a plugin. Check immutable archive hashes during installation. A checksum identifies an expected payload. It does not establish that the code is safe.

## Choose a development workflow

| Workflow | Use when | What to test |
| --- | --- | --- |
| Single source file | The plugin is small and uses host-provided APIs | Actual runtime compilation and activation |
| Compiled class library | The plugin has several files or private dependencies | Archive contents, assembly identity and runtime loading |
| Native dependency | The plugin requires an OS-specific library | Execution on each declared process target |
| Exported contracts | Another plugin needs typed services | Shared contract identity and dependency order |

Compile your author project to catch C# errors. Also load the resulting package through the plugin runtime. The author project's successful build alone does not test runtime source references or private dependency resolution.

## Keep identity consistent

Use one stable lowercase plugin ID in the descriptor, manifest, catalogue, dependency declarations and resource paths. Give a changed published payload a new plugin version.

Do not use the application version as the plugin API version. DMCBK, UMPK, the host application and the plugin have separate versions. The manifest checks each relevant compatibility constraint.

## Plan cleanup before activation

List every resource your plugin creates. Include event handlers, commands, timers, workers, channels, services and disposable objects. Assign each resource to a session scope or plugin lifetime.

Use session callbacks for work that repeats after reconnect. Release plugin-owned resources during deactivation. Respect cancellation for asynchronous cleanup. Keep packet callbacks short so they do not delay the client event path.

The testing guide demonstrates loading, multiple sessions and unload. Run those checks before publishing an update.

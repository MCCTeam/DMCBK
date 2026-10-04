# Write and test a plugin

For the detailed guide and reference pages, see [the full documentation](../plugins/index.md).

A plugin implements `DMCBK.PluginSdk.IPlugin`. The SDK contains author contracts. `DMCBK.Plugins` loads packages and compiles source entries. Plugin authors do not need MCC source.

## Write the entry class

1. Create a .NET 10 class library.
2. Add `DMCBK.PluginSdk` version `0.1.0-preview.1`.
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
            context.Variables.Set("session-counter.sessions", sessions.ToString());
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
dmcbk = ">=0.1.0-preview.1 <0.2.0"
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
        context.SessionStarted += (_, _) => context.Variables.Set("example.started", "yes");
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
        () => host.Client.Variables.Get("example.started") == "yes");
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

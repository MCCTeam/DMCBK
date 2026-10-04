# Host a client

DMCBK does not choose a terminal or UI framework. Your host supplies login interaction, command output and optional views. A worker can use the default host. A desktop application can implement the same interfaces with windows and dialogs.

## Compose modules

Module factories run in the order you register them. Commands must precede Beacon and plugin loading. Marketplace installation needs the previously attached plugin runtime.

```csharp
using DMCBK.Core;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;

string dataRoot = Path.Combine(Path.GetTempPath(), "dmcbk-example");
string pluginRoot = Path.Combine(dataRoot, "plugins");

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("KitBot")
    .UseApplication(new HostApplication(
        "my-client", "1.0.0", new HashSet<string>()))
    .UseCommands()
    .UseBeacon()
    .UsePlugins(new PluginOptions(pluginRoot))
    .UseMarketplace(new MarketplaceOptions(
        pluginRoot, Path.Combine(dataRoot, "marketplaces.toml"))
    {
        Runtime = current => current.GetModule<IPluginInstallationHost>()
    })
    .Build();
```

This example needs Core, Commands, Beacon, Plugins and Marketplace packages. Construction does not connect to a server. The host loads plugins with `PluginHost.LoadAllAsync` before it starts the client. The [plugin guide](guides/plugins.md#load-a-local-plugin) shows that sequence.

Each client owns its module instances and manual catalogue. Disposal releases modules in reverse registration order. The host retains ownership of a logger factory or HTTP client that it supplies.

## Supply command output

`IHostInterface` supplies authentication interaction and presentation services. Optional properties have default implementations. This host buffers command output for a UI to read:

```csharp
using DMCBK.Core;
using DMCBK.Core.Commands;
using Umpk.Auth;

public sealed class BufferedHost : IHostInterface
{
    public BufferedCommandOutput Output { get; } = new();
    public IUserPrompt? Prompt => null;
    public IAuthInteraction? AuthInteraction => null;
    public ICommandOutput CommandOutput => Output;
}
```

Pass the host to `UseHostInterface`. A rich host can also implement `IHostUi` for images, inventory views, book editors, dialogs and manual documents. Methods return `false` or `null` when the host cannot provide a view. Commands then use their text fallback.

## Authentication

`UseUsername` creates an offline identity. Online accounts use `UseAccount` with `MccAccountKind.MicrosoftDeviceCode`, `MicrosoftBrowser` or `Yggdrasil`.

1. Implement UMPK's `IAuthInteraction` in your host.
2. Return the implementation from `IHostInterface.AuthInteraction`.
3. Select the account with `UseAccount`.
4. Set `UseTokenStorePath` if tokens must persist between runs.

Without an interaction implementation, the default host cannot complete an interactive online login. Without a token-store path, online authentication uses memory storage. See [UMPK authentication](https://github.com/MCCTeam/UMPK/blob/master/docs/guides/authentication.md) for the interaction contract.

## Lifecycle and reconnects

1. Subscribe to client events before calling `StartAsync`.
2. Use a cancellation token for connection and game actions.
3. Wait for player placement before reading position-dependent state.
4. Call `ReconnectAsync` for an explicit reconnect.
5. Call `StopAsync` when the host shuts down.
6. Dispose the client.

`StatusChanged` reports connection states. `LastDisconnect` contains the latest disconnect information. Automatic reconnects require a policy through `UseReconnectPolicyProvider` or typed configuration. A plugin's `ActivateAsync` runs once per activation. Its `SessionStarted` handler runs again after reconnect.

## Desktop and mobile

A desktop host can use Avalonia, WinUI or another .NET UI framework. A mobile host must use a platform that supports .NET 10 and the selected dependencies. DMCBK does not include a finished desktop or mobile application.

1. Run network work outside the UI thread.
2. Dispatch UI changes through your framework's dispatcher.
3. Select writable directories for tokens, settings and plugin data.
4. Implement login interaction for the platform.
5. Stop the client when the application suspends or exits.

Runtime C# compilation and dynamic assembly loading depend on platform restrictions. A platform with mandatory Native AOT cannot use the current plugin runtime unchanged. Test the selected modules on the actual device.

## Web applications

The [WebBackend sample](../samples/WebBackend/Program.cs) hosts DMCBK inside ASP.NET Core. A browser talks to your backend. The backend owns the Minecraft connection.

Ordinary browser code cannot open the Minecraft TCP connection used here. DMCBK does not include a browser transport relay. An HTTP or WebSocket API also needs host-level authentication and access controls before you expose game actions to other users.

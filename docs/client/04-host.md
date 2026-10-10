# Chapter 4: Add configuration and a host

The application owns its configuration directory, command presentation and login interaction. DMCBK accepts these choices through typed options and host interfaces.

## Add TOML configuration

1. Add the configuration package.

```bash
dotnet add package DMCBK.Configuration --version 0.1.0-preview.7
```

2. Select an application data directory.
3. Create a loader for that directory.
4. Inspect its warnings before building the client.

This complete example creates defaults in a unique temporary directory:

```csharp
using DMCBK.Core;
using DMCBK.Core.Configuration;

string folder = Path.Combine(Path.GetTempPath(), "dmcbk-guide", Guid.NewGuid().ToString("N"));
var loader = new DmcbkConfigurationLoader(folder);
ConfigurationLoadResult loaded = loader.Load(generateMissing: true);

foreach (ConfigurationWarning warning in loaded.Warnings)
    Console.WriteLine(warning.Message);

await using Client client = new ClientBuilder()
    .UseConfiguration(loaded.Config)
    .UseUsername("GuideBot")
    .UseServer("localhost", 25565)
    .UseConfigurationStorage(folder)
    .Build();

Console.WriteLine($"Configuration folder: {folder}");
```

The example constructs a client. It does not connect. It creates `client.toml`, `accounts.toml`, `servers.toml` and an ignore file. It leaves that directory for inspection.

`generateMissing: true` is explicit. Loading existing files does not overwrite them. The loader can recover from invalid input with defaults and warnings. Your application must decide which warnings prevent connection.

The explicit `UseUsername` and `UseServer` calls follow `UseConfiguration`. They override the loaded account and endpoint. Reverse that order if the configuration must win.

For production, use a persistent host-selected directory. Do not use a random temporary directory for saved accounts or settings.

## Reload deliberately

`loader.Reload()` creates a new configuration snapshot. The old client does not automatically apply every changed field.

Some modules expose explicit reload operations. Changing account, endpoint or engine composition can require a reconnect or a new client. Decide this behavior in your host.

See the [configuration reference](../reference/configuration.md) for files, precedence and feature dependencies.

## Present local command output

Commands can produce a final result and a longer body. Your host must display both.

```csharp
using DMCBK.Core;
using DMCBK.Core.Commands;

await using Client client = new ClientBuilder()
    .UseUsername("GuideBot")
    .UseCommands()
    .Build();

var execution = await client.Commands.DispatchCapturedAsync("help");
Console.Write(execution.Output);
Console.WriteLine(execution.Result.Message ?? execution.Result.Status.ToString());
```

The example runs without a server. `DispatchCapturedAsync` captures body text for this call. It is useful for a request/response host.

For continuous output, implement `ICommandOutput.WriteLine`. Return the implementation from `IHostInterface.CommandOutput`. A desktop host can append text to a view. A worker can write text to a log.

`BufferedCommandOutput` is a simple list buffer. It is useful for sequential tests. Use a synchronized queue when multiple producers and consumers access output concurrently.

## Support Microsoft device-code login

`UseUsername` selects offline authentication. Online servers require an online flow.

The [DeviceCodeHost sample](../../samples/ClientGuide/DeviceCodeHost.cs) contains a complete host for Microsoft's device-code prompt. It prints the verification URL and code. It rejects browser-code and Yggdrasil flows that it does not implement.

To use that host in the complete ClientGuide sample:

1. Replace `UseUsername(username)` with `UseAccount(...)`.
2. Add `UseHostInterface(new DeviceCodeHost())`.
3. Select a private token directory with `UseTokenStorePath`.
4. Connect to a server that permits the authenticated account.
5. Complete the displayed Microsoft login prompt.

The account construction is:

```csharp
using DMCBK.Core;

var account = new DmcbkAccount
{
    Kind = DmcbkAccountKind.MicrosoftDeviceCode,
    User = "your-account-login-hint"
};

await using Client client = new ClientBuilder()
    .UseAccount(account)
    .UseServer("localhost")
    .Build();

Console.WriteLine(account.Kind);
```

This construction example does not connect. It deliberately omits login interaction. Copying the account alone cannot complete an interactive login.

A token-store path lets subsequent runs reuse and refresh credentials. Keep that directory private and outside Git. Never display token contents in command output.

The device-code host compiles with the guide sample. Automated guide checks do not sign in to a real Microsoft account. Authentication requires the user's interaction and account permissions.

## Supply logging

`UseLoggerFactory` accepts an `ILoggerFactory`. The default factory discards logs. Configuring logging fields alone does not create a console or file logger for your host.

The [HeadlessClient logger](../../samples/HeadlessClient/ConsoleLoggerFactory.cs) demonstrates a small console implementation. A production host can use the normal Microsoft logging providers or its chosen logging library.

Keep the logger factory alive while the client uses it. The host disposes a factory that it supplies.

Continue to [Chapter 5](05-modules.md).

# Commands and Beacon

Commands route application input to registered actions. Beacon runs `.mcc` scripts with game events and scheduled work. Both are optional modules, and both belong to one client.

## Use commands

1. Add the `DMCBK.Commands` package.
2. Register `UseCommands` on the builder.
3. Dispatch an internal command with `DispatchCapturedAsync`.
4. Check the result before using its output.

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("KitBot")
    .UseCommands()
    .Build();

var (result, output) = await client.Commands.DispatchCapturedAsync("help");
Console.WriteLine(output);
Console.WriteLine(result.Message);
Console.WriteLine(result.IsSuccess);
```

Internal dispatch does not need a leading slash. `HandleInputAsync` applies the configured command prefix and can route ordinary text to server chat. Use `CompleteAsync` or `CompleteInputAsync` when your UI needs completion candidates. `DescribeCommands` returns command metadata for a help screen.

Game commands need a live session and the corresponding gameplay features. For example, inventory commands require inventory handling. A failed command returns a typed `CmdStatus` instead of claiming success.

## Run Beacon without a server

The offline runner is useful for script editing and tests. It captures local `show` output. It does not prove that a game action succeeds against a real server.

```csharp
using DMCBK.Core.Beacon;

var report = await BeaconOfflineRunner.RunSourceAsync(
    "hello.mcc", "# beacon 1\n# needs: chat.send\nshow 2 + 3\n", seed: 42, tickSeconds: 0);

if (!report.Ok)
    throw new InvalidOperationException("The script failed.");

foreach (string line in report.Output)
    Console.WriteLine(line);
```

## Attach live scripts

1. Add the `DMCBK.Beacon` package.
2. Register `UseCommands` before `UseBeacon`.
3. Lint the source with `client.Scripts.Lint`.
4. Run valid source with `client.Scripts.RunAsync`.
5. Stop the script with `client.Scripts.Stop` when required.

`client.Scripts` exposes the script service:

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("KitBot")
    .UseCommands()
    .UseBeacon()
    .Build();

var lint = client.Scripts.Lint("hello.mcc", "# beacon 1\n# needs: chat.send\nshow 2 + 3\n");
if (lint.Ok)
    await client.Scripts.RunAsync("hello", "# beacon 1\n# needs: chat.send\nshow 2 + 3\n");
```

Source held in memory does not require a configuration directory. File discovery and editor operations use the explicit configuration folder. `SaveDocumentAsync` checks script diagnostics and detects an external file change before it writes. See [configuration](../reference/configuration.md).

Beacon game verbs need a live session. Plugin extensions can add functions, variables and custom events through `PluginContext.Beacon`. [Embedded script manuals](../../src/DMCBK.Core/Manual/Resources/en/scripts.md) describe the language where available.

# Commands and Beacon

For the detailed guide and reference pages, see [the full documentation](../beacon/index.md).

Commands route application input to registered actions. Beacon runs `.bcn` scripts with game events and scheduled work. Both are optional modules, and both belong to one client.

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
    "hello.bcn", "# beacon 1\nshow 2 + 3\n", seed: 42, tickSeconds: 0);

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

var lint = client.Scripts.Lint("hello.bcn", "# beacon 1\nshow 2 + 3\n");
if (!lint.Ok)
    throw new InvalidOperationException("Correct the script diagnostics before running it.");

var run = await client.Scripts.RunAsync("hello", "# beacon 1\nshow 2 + 3\n");
if (!run.Success)
    throw new InvalidOperationException("The script failed at runtime.");
client.Scripts.Stop("hello");
```

Source held in memory does not require a configuration directory. File discovery and editor operations use the explicit configuration folder. `SaveDocumentAsync` checks script diagnostics and detects an external file change before it writes. See [configuration](../reference/configuration.md).

Beacon game verbs need a live session. Plugin extensions can add functions, variables and custom events through `PluginContext.Beacon`. [Embedded script manuals](../../src/DMCBK.Core/Manual/Resources/en/scripts.md) describe the language where available.

## Decide how input should behave

A host can offer internal commands, server commands and ordinary chat in one input box. The command router classifies the line using the configured prefix. The direct dispatcher executes an internal command without that input classification.

Use `DispatchAsync` when your code already knows the line is an internal command. Use `HandleInputAsync` for text from your host's input interface. Use `DispatchCapturedAsync` when you need output text for a test or a custom interface.

The configured prefix can change. Do not hardcode a prefix in a graphical interface. Read `client.Commands.ActivePrefix` and display it near the input field.

## Read command results correctly

Captured output and the result message are separate. A command can print several lines and return a short result message. Render both when each contains useful text.

| Status | Meaning | Host response |
| --- | --- | --- |
| `Done` | The command completed successfully | Display its output or data |
| `Fail` | The command failed | Display the diagnostic message |
| `FailNeedInventory` | Inventory handling is unavailable | Explain the missing feature |
| `FailNeedEntity` | Entity handling is unavailable | Explain the missing feature |
| `FailNeedTerrain` | Terrain or movement support is unavailable | Explain the missing feature |
| `FailChunkNotLoad` | Required world data is absent | Wait for the relevant chunk or choose another action |
| `NotRun` | The command did not execute | Treat it as incomplete |

`IsSuccess` checks for `Done`. Do not infer success from an empty message or a positive-looking output line. `Data` can contain an immutable result snapshot. Check the expected type before using it.

## Inspect command descriptions

This complete offline example lists the command names attached to one client. It also dispatches the built-in help command.

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("GuideBot")
    .UseCommands()
    .Build();

foreach (var command in client.Commands.ListCommands())
    Console.WriteLine(command.Name);

var (result, output) = await client.Commands.DispatchCapturedAsync("help");
if (!result.IsSuccess)
    throw new InvalidOperationException(result.Message);
Console.WriteLine(output);
```

The client does not connect. Command discovery and help work offline. Game commands can still require a session and feature gates.

## Add completion to an interface

`CompleteAsync` returns candidate strings for internal command input. `CompleteInputAsync` accepts the raw input line and cursor position. It returns candidates plus the replacement span in that raw line.

Keep the original input buffer until you apply a selected candidate. Replace only the returned range. Do not append the candidate blindly or remove the prefix twice. A completion candidate is a suggestion, not permission to execute a command.

Use cancellation when your UI sends another completion request. Avoid displaying an older response over a newer input buffer.

## Register commands with an owner

A host command belongs to the module lifetime. A scoped command returns a disposable registration. Dispose that registration when its owner closes.

Plugin commands use the plugin SDK's scopes. Session commands disappear when their session ends. Plugin-wide commands can remain available while disconnected. This distinction lets a settings command work without a server while a packet-dependent command stays session-scoped.

Read [plugin lifecycle and commands](../plugins/lifecycle-and-commands.md) for complete registrations. The chaptered plugin guide adds a command to its running example.

## Choose Beacon execution mode

The offline runner executes source with a simulated host and clock. It suits expressions, formatting, timers and deterministic tests. A live client script engine connects game events and actions to the current session.

`show` writes local output. A chat action sends a server-facing request. A local output check does not prove server chat delivery.

Beacon requires the `# beacon 1` header. Declare each required capability in `# needs:`. A capability declaration asks for access. It does not enable a missing host module or server feature.

Use `.bcn` for script files. The Beacon `mcc` verb still dispatches an internal command. The file extension does not rename that language verb.

## Start with a complete guide

The [Beacon guide](../beacon/index.md) explains expressions, events, game actions, state and plugin integration. Its chaptered tutorial builds a working script through small steps. The [client guide](../client/index.md) explains how to host it.

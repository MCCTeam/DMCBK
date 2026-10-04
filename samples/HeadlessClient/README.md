# Headless client

This sample embeds Core with no terminal UI package. It supplies an `ILoggerFactory`, prints incoming chat and performs one action after connection.

Use the [client guide](../../docs/client/index.md) if this is your first DMCBK application.

## Run

1. Start an offline-mode Minecraft Java server that you control.
2. Restore the project.
3. Run the sample.

```bash
dotnet restore samples/HeadlessClient
dotnet run --project samples/HeadlessClient -- HostSample localhost:25565 auto
```

Arguments: offline username, server address and version. `auto` detects the version through a status ping. An exact version pins the protocol and skips detection.

The default username is `HostSample`. The default endpoint is `localhost:25565`.

For local package previews, use [local installation](../../docs/getting-started/installation.md#use-local-packages).

## Behavior

The sample connects, waits up to 15 seconds for player placement, reads health and food, and sends one chat line. It waits three seconds for incoming chat before stopping.

A 60-second lifetime token bounds the run. If placement does not arrive, the sample reports position and game mode as unknown. It does not display default coordinates as a real measurement.

Incoming chat prints as `[chat] ...`. Lifecycle and diagnostic messages use the host logger. The line `Server was successfully joined` confirms connection readiness.

| Exit code | Meaning |
| --- | --- |
| 0 | The action completed and the client stopped. |
| 1 | Arguments or the selected version are invalid. |
| 2 | Version resolution failed. |
| 3 | Connection failed, timed out or ended unexpectedly. |
| 4 | Login or authentication failed. |
| 5 | A post-connection action failed. |

This example uses offline authentication. Online-mode servers need an account flow and an `IAuthInteraction` host. See [Chapter 4](../../docs/client/04-host.md).

## Files

`Program.cs` owns the application sequence. `ConsoleLoggerFactory.cs` provides a minimal console logger. The host owns and disposes that factory.

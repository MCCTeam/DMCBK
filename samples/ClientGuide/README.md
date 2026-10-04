# Client guide sample

This sample accompanies the [chaptered client guide](../../docs/client/index.md). It provides a real server mode and a bounded in-memory protocol check.

## Restore packages

The project uses NuGet references. It does not reference sibling source projects.

From the DMCBK repository root:

```bash
dotnet restore samples/ClientGuide
```

For locally packed previews, use the [local package procedure](../../docs/getting-started/installation.md#use-local-packages). Use a new package cache when replacing the same preview version.

## Run without a Minecraft server

```bash
dotnet run --project samples/ClientGuide -- --self-test
```

The executable checks a local command, login/configuration/play, incoming chat, health snapshots, outgoing chat bytes and clean shutdown. Each successful check prints `PASS:`.

The fake server does not place a player or send terrain. The check does not perform Microsoft login or real network interoperability tests.

## Connect to your server

1. Start an offline-mode Minecraft Java server that you control.
2. Run the following command.
3. Replace the version with the server's actual version.

```bash
dotnet run --project samples/ClientGuide -- GuideBot localhost 25565 1.21.5
```

Arguments: username, hostname, port and Minecraft version. The application pins the version, connects, waits for placement, reads player state and sends one message. It stops after three seconds.

Press Ctrl+C to cancel. The entire operation has a 45-second deadline.

| Exit code | Meaning |
| --- | --- |
| 0 | The action and shutdown completed. |
| 1 | Connection, action or cancellation failed. |
| 2 | Arguments are invalid. |

## Files

| File | Purpose |
| --- | --- |
| `Program.cs` | Arguments, bounded connection/action and clean shutdown |
| `ClientObserver.cs` | Chat/status observation with owned registrations |
| `ClientChecks.cs` | Assertions against a real protocol session in memory |
| `DeviceCodeHost.cs` | Optional Microsoft device-code presentation |

The default mode uses an offline account. Read [Chapter 4](../../docs/client/04-host.md#support-microsoft-device-code-login) before adapting it for an online account.

# Chapter 2: Connect and send chat

This chapter creates a complete client. It connects to a local server, displays incoming chat and sends one message.

## Prepare a server

1. Start a Minecraft Java server that you control.
2. Select offline mode for this local test.
3. Check that the server listens on port `25565`.
4. Wait for the server to finish startup.

The offline username in this example is `GuideBot`. `UseUsername` does not sign in to Microsoft. It cannot authenticate to a server that requires an online account.

The server's status response must identify a version that UMPK supports. The builder detects that version automatically. Chapter 6 explains how to select a version explicitly.

## Write the application

1. Replace `Program.cs` with this complete file.
2. Save the file.

```csharp
using DMCBK.Core;
using Umpk.Text;

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));

await using Client client = new ClientBuilder()
    .UseServer("localhost", 25565)
    .UseUsername("GuideBot")
    .Build();

client.Game.Chat.MessageReceived += (_, message) =>
    Console.WriteLine(message.Message.ToPlainText(client.Translations));

try
{
    await client.StartAsync(lifetime.Token);
    Console.WriteLine("Connected.");

    bool placed = await client.Game.Player.WaitForSpawnAsync(lifetime.Token);
    if (!placed)
        throw new InvalidOperationException("The session ended before player placement.");

    PlayerStatus status = await client.Game.Player.GetStatusAsync(lifetime.Token);
    Console.WriteLine($"Health: {status.Health}. Food: {status.Food}.");
    Console.WriteLine($"Position: {status.Position}");

    await client.Game.Chat.SendAsync("Hello from GuideBot", lifetime.Token);
    Console.WriteLine("Chat sent.");
    await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token);
}
finally
{
    await client.StopAsync();
    Console.WriteLine("Stopped.");
}
```

## Understand the sequence

`ClientBuilder` collects options. `Build()` creates the client and its modules. It does not connect to a server.

`StartAsync` authenticates when required and starts the connection. Its completion does not prove that the server placed the player. `WaitForSpawnAsync` waits for that separate event.

A cancellation token bounds the entire exercise to 45 seconds. The token also stops individual game actions. A cancelled operation usually throws `OperationCanceledException`.

`finally` runs when the action succeeds or fails. `StopAsync` stops the connection and automatic reconnect work. `await using` then disposes the client.

## Run the application

1. Run the application.
2. Check the application output.
3. Check the server chat.

```bash
dotnet run
```

The application prints `Connected.`, player data, `Chat sent.` and `Stopped.`. Incoming messages depend on the server. The server should receive `Hello from GuideBot`.

Do not interpret `SendAsync` completion as proof that other players saw the message. It reports the send operation. The server can filter or reject chat.

## Run the complete sample instead

From the DMCBK repository root:

```bash
dotnet run --project samples/ClientGuide -- GuideBot localhost 25565 1.21.5
```

The four arguments are username, hostname, port and exact Minecraft version. Use your server's actual version. This sample pins the version and skips automatic detection.

The complete sample adds argument checks, Ctrl+C handling, event-registration cleanup and readable failures.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Version resolution fails | Check the address, port and status-ping support. Select the actual version explicitly if required. |
| Server refuses the connection | Start the server. Check its listening port. |
| Server rejects login | Check offline versus online authentication, whitelist and the server's account rules. |
| Player placement times out | Check server startup and logs. Connection alone does not supply a valid position. |
| Chat is absent | Check server chat settings and permissions. A server can suppress messages. |

Continue to [Chapter 3](03-game.md).

# Your first client

This quick example connects an offline account, displays chat and sends one message. For explanations and a complete tested application, use the [chaptered client guide](../client/index.md).

## Prepare the exercise

1. Create a project with the [installation procedure](installation.md).
2. Start an offline-mode Minecraft Java server that you control.
3. Check that it listens on port `25565`.
4. Replace `Program.cs` with the following file.

```csharp
using DMCBK.Core;
using Umpk.Text;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

await using Client client = new ClientBuilder()
    .UseServer("localhost", 25565)
    .UseUsername("KitBot")
    .Build();

client.Game.Chat.MessageReceived += (_, message) =>
    Console.WriteLine(message.Message.ToPlainText(client.Translations));

try
{
    await client.StartAsync(timeout.Token);
    Console.WriteLine("Connected.");

    if (!await client.Game.Player.WaitForSpawnAsync(timeout.Token))
        throw new InvalidOperationException("The session ended before player placement.");

    PlayerStatus status = await client.Game.Player.GetStatusAsync(timeout.Token);
    Console.WriteLine($"Health: {status.Health}. Position: {status.Position}");

    await client.Game.Chat.SendAsync("Hello from DMCBK", timeout.Token);
    await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
}
finally
{
    await client.StopAsync();
}
```

5. Run the application.

```bash
dotnet run
```

The server should receive `Hello from DMCBK`. The application displays player data and any incoming chat. Server settings can filter or reject chat.

## What each stage does

`Build()` creates an idle client. It does not connect. `StartAsync` starts a session. The default builder detects the Minecraft version through a status ping.

`WaitForSpawnAsync` waits for player placement. Connection success alone does not make position or game mode valid. Check placement before using that data.

The token limits the exercise to 45 seconds. The `finally` block stops the client even when an action fails. `await using` releases the client and its modules.

The default host has no login interaction or command-output UI. This example displays chat through its own event handler. `UseUsername` selects an offline account.

## Run an included sample

The complete guide sample checks arguments and handles Ctrl+C:

```bash
dotnet run --project samples/ClientGuide -- KitBot localhost 25565 1.21.5
```

Use the actual server version. The [HeadlessClient sample](../../samples/HeadlessClient/README.md) also adds an explicit logger.

You can run the guide check without a server:

```bash
dotnet run --project samples/ClientGuide -- --self-test
```

See [testing and distribution](../client/07-test-and-deploy.md) for what that check proves.

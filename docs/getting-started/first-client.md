# Your first client

This example connects an offline account, prints incoming chat and sends one message. Use an offline-mode server that you control. Public online-mode servers require Microsoft authentication.

## Create the client

1. Create the project with the [installation procedure](installation.md).
2. Copy this code into `Program.cs`.
3. Start your server on port `25565`.
4. Run the application.

```csharp
using DMCBK.Core;
using Umpk.Text;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await using Client client = new ClientBuilder()
    .UseServer("localhost", 25565)
    .UseUsername("KitBot")
    .Build();

client.Game.Chat.MessageReceived += (_, message) =>
    Console.WriteLine(message.Message.ToPlainText(client.Translations));

try
{
    await client.StartAsync(timeout.Token);
    await client.Game.Chat.SendAsync("Hello from DMCBK", timeout.Token);
    await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
}
finally
{
    await client.StopAsync();
}
```

The default host does not display login prompts or command output. This example uses only offline authentication and game APIs. The application prints chat through its own `Console.WriteLine` call.

The builder detects the server version with a status ping. You can use `UseVersion` with a UMPK `JavaVersion` when automatic detection cannot work.

## Read player state

The connection can reach play before the server places the player. `WaitForSpawnAsync` makes that distinction explicit.

Insert this code after `StartAsync` in the previous example:

```csharp
if (await client.Game.Player.WaitForSpawnAsync(timeout.Token))
{
    var status = await client.Game.Player.GetStatusAsync(timeout.Token);
    Console.WriteLine(status);
}
```

Game actions can fail when the session ends or the host disables a required feature. Keep the caller's cancellation token on each action. See [hosting](../hosting.md) for lifecycle and reconnect guidance.

## Run the included sample

The headless sample adds logging and checks its exit status. Its arguments are username, server address and Minecraft version.

```bash
dotnet run --project samples/HeadlessClient -- KitBot localhost:25565 auto
```

The [web backend sample](../../samples/WebBackend/Program.cs) exposes client operations through an ASP.NET Core host. It is a backend example. It does not connect a browser directly to a Minecraft TCP server.

# Chapter 3: Observe game state

DMCBK groups game operations under `client.Game`. A read returns a snapshot. An action asks the live session to change something.

A snapshot is not a writable connection to the server. Changing a local collection does not change the player's inventory or world.

## Select the right surface

| Surface | Examples of available work |
| --- | --- |
| `Game.Chat` | Receive chat, send chat and request server command completion |
| `Game.Player` | Read health, abilities, held items, tab list and scoreboards |
| `Game.World` | Read blocks, search loaded terrain and perform block actions |
| `Game.Entities` | Read tracked entities and request entity interaction |
| `Game.Inventory` | Read containers and perform supported inventory actions |
| `Game.Movement` | Rotate, move and request navigation |
| `Game.Session` | Read connection facts and send client settings |
| `Game.Dialogs` | Read and answer supported server dialogs |
| `Game.Maps` | Read known filled maps and their pixels |

Check the [GameApi definition](../../src/DMCBK.Core/GameApi.cs) before using an action. A server version, gameplay feature or loaded chunk can limit it.

## Read player data after placement

Chapter 2 already waits for placement before reading position. Keep that wait in any position-dependent operation.

`PlayerStatus.HasSpawned` tells you whether the session received placement. Before placement, position and game mode can contain defaults. Those values do not describe a measurement.

Health updates and placement are separate data streams. A test can receive health while `HasSpawned` remains false. The guide's executable check demonstrates this distinction.

## Display incoming chat

The server sends structured text components. These can contain translation keys, formatting and nested text.

`message.Message.ToPlainText(client.Translations)` converts the component to plain text. It uses the client's translation source. A graphical host can preserve structure and render its own text style.

The [ClientObserver sample](../../samples/ClientGuide/ClientObserver.cs) owns chat and status event registrations. Its `Dispose()` method removes them. Use that pattern when a window, view or service can stop observing before the client stops.

Keep event handlers short. Send work to a queue when it needs slow I/O. Avoid `async void` handlers for operations whose exceptions must reach your application.

## Create a read-only game report

This complete program reads inventory, nearby entities and world time. It does not move the player or change items.

```csharp
using DMCBK.Core;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using Client client = new ClientBuilder()
    .UseUsername("ReportBot")
    .UseServer("localhost")
    .Build();

try
{
    await client.StartAsync(deadline.Token);
    if (!await client.Game.Player.WaitForSpawnAsync(deadline.Token))
        throw new InvalidOperationException("The session ended before player placement.");

    PlayerInventorySnapshot inventory =
        await client.Game.Inventory.GetPlayerInventoryAsync(deadline.Token);
    Console.WriteLine($"Selected hotbar index: {inventory.HeldSlot}");

    for (int slot = 0; slot < inventory.Slots.Count; slot++)
    {
        ItemStackInfo item = inventory.Slots[slot];
        if (!item.IsEmpty)
            Console.WriteLine($"Slot {slot}: {item.Count} x {item.ItemId}");
    }

    var entities = await client.Game.Entities.NearbyAsync(16, deadline.Token);
    foreach (var entity in entities)
        Console.WriteLine($"Entity {entity.Id}: {entity.TypeId} at {entity.Position}");

    WorldTimeInfo time = await client.Game.World.GetTimeAsync(deadline.Token);
    Console.WriteLine($"World age: {time.WorldAge}. Time of day: {time.TimeOfDay}.");
}
finally
{
    await client.StopAsync();
}
```

Inventory slot numbers and hotbar indexes are different. `HeldSlot` uses indexes 0 through 8. Inventory snapshots expose the server inventory layout.

The report displays the state received so far. Player placement does not prove that every inventory or entity packet arrived. A newly connected empty report can change after more packets arrive.

Nearby entity reads need entity tracking. Inventory reads need inventory tracking. The default builder enables both. The chat-only composition below disables them.

Read-only reports are a useful first exercise. Before adding item clicks, digging or navigation, test each action's outcome and cancellation behavior.

## Choose a smaller feature set

A chat-only client can disable expensive tracking and movement features:

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseUsername("ChatBot")
    .UseServer("localhost")
    .ConfigureFeatures(features =>
    {
        features.Terrain = false;
        features.Physics = false;
        features.Pathfinding = false;
        features.Inventory = false;
        features.Entities = false;
    })
    .Build();

Console.WriteLine($"Terrain enabled: {client.Features.Terrain}");
```

The example constructs a client. It does not connect.

Physics requires terrain. Pathfinding requires physics. Programmatic feature selection enables implied dependencies. Disable all three when you want terrain disabled.

Features describe engine tracking. They are different from optional modules. Disabling terrain does not remove Commands. Attaching Beacon does not enable every game action.

## Handle missing data and failed actions

A live server sends only the state visible to the client. A world search cannot find a block in a chunk that the client never received.

Game actions can fail after a disconnect. Some APIs return an absence value. Others report a typed exception or an outcome. Read the selected API's result contract.

Catch `DmcbkFeatureDisabledException` when the host intentionally disabled a required game feature. Catch `DmcbkNotInSessionException` for APIs that require a live session. Chat sending currently reports `InvalidOperationException` when no live chat session exists.

Check results before displaying success. An inventory request, respawn request or movement request does not always mean the server accepted the requested result.

## Reconnect and event buses

`client.Game.Chat.MessageReceived` belongs to the stable DMCBK chat facade. It binds to the new connection after reconnect.

`client.Game.Events` returns the current UMPK session's event bus. Do not store that bus across reconnect. Register fresh session subscriptions and dispose old subscriptions.

Continue to [Chapter 4](04-host.md).

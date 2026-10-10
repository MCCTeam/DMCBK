# Chapter 4: Answer an event

An event is a notification from the host. A chat event contains a player name and a message. An event handler is a block that runs for a matching notification.

You will answer the exact message `!hello`. You will first simulate chat with `DMCBK.Testing`.

## Create a handler

1. Create `greeter.bcn`.
2. Copy this complete script into the file.

```beacon
# beacon 1
# needs: chat.send
on chat as e when trim(lower(e.message)) is "!hello"
  whisper e.player "Hello, {e.player}!"
end on
```

`# needs: chat.send` declares the permission used by `whisper`. It does not establish a connection.

`as e` gives the event record a local name. `e.message` reads the message. `e.player` reads the sender. `when` filters the record before the body runs.

`lower` makes the comparison case-insensitive. `trim` removes surrounding whitespace. Exact equality avoids treating `!hello someone` as the same request.

Running this file with the offline runner produces no greeting. Loading registers the handler. The greeting needs a chat event.

## Test the event

1. Add `DMCBK.Testing` to the runner project.

```sh
dotnet add package DMCBK.Testing --version 0.1.0-preview.5
```

2. Replace `Program.cs` with this complete test program.

```csharp
using DMCBK.Core.Beacon;
using DMCBK.Testing;

string path = Path.GetFullPath("greeter.bcn");
string source = await File.ReadAllTextAsync(path);
var host = new ScriptTestHost();
var engine = new BeaconEngine(host);
try
{
    var loaded = await engine.RunScriptAsync("greeter", source, fileName: path);
    if (!loaded.Success)
        throw new InvalidOperationException("The script did not load.");

    var fired = await engine.FireEventAsync(
        "chat", BeaconEventFields.Chat("Alice", " !HELLO "));
    if (fired.Handlers.Any(handler => handler.Result is { Success: false }))
        throw new InvalidOperationException("A handler failed.");
    if (!host.Whispers.Contains(("Alice", "Hello, Alice!")))
        throw new InvalidOperationException("The expected whisper is missing.");

    host.Whispers.Clear();
    await engine.FireEventAsync(
        "chat", BeaconEventFields.Chat("Alice", "!hello someone"));
    if (host.Whispers.Count != 0)
        throw new InvalidOperationException("The filter accepted an unrelated message.");
    Console.WriteLine("Chat checks passed.");
}
finally
{
    engine.RemoveScript("greeter");
}
```

3. Execute `dotnet run`.

Expected output is `Chat checks passed.` The test proves field access, filtering, and the requested whisper. It does not send a Minecraft packet.

A real host sends chat through its session adapter. It must parse incoming chat and deliver the corresponding event. Use the client in [Chapter 9](09-complete-helper.md) for that path.

Next: [Chapter 5: Add scheduled work](05-time.md).

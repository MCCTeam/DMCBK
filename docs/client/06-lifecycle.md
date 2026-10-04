# Chapter 6: Handle reconnect and shutdown

A client can outlive several server sessions. Treat those lifetimes separately. Configuration and modules can remain attached while each connection's resources change.

## Observe status

Subscribe to `StatusChanged` before `StartAsync`. The status vocabulary includes `Created`, `Authenticating`, `Connecting`, `Configuring`, `Playing`, `Reconnecting` and `Disconnected`.

An offline account does not need the authentication step. Do not require every connection to visit every status.

`LastDisconnect` describes the latest ended connection. It can contain a server kick or a transport failure. Use the reason when the host presents a failure.

## Select a version explicitly

Automatic detection uses a status ping. A server can block or change that ping.

This construction example pins the actual server version:

```csharp
using DMCBK.Core;
using Umpk.Data.Java;

if (!JavaVersions.TryGetByName("1.21.5", out var version))
    throw new InvalidOperationException("The selected version is unavailable.");

await using Client client = new ClientBuilder()
    .UseUsername("GuideBot")
    .UseServer("localhost")
    .UseVersion(version)
    .Build();

Console.WriteLine(version.Version.Name);
```

Pinning skips the detection ping. Select the server's real version. A wrong protocol version can fail during login or packet decoding.

## Request a reconnect

Use `ReconnectAsync(ct: token)` to reconnect to the selected server. Use its `ServerSelection` argument to select another endpoint.

Call the operation from application code after an explicit user action or policy decision. Do not start a reconnect inside every incoming packet callback.

After reconnect, wait for player placement again. A previous session's `HasSpawned` result does not prove placement in the new session.

Some operations bind to one session. Cancel those operations when that session ends. Do not retain `Game.Events` subscriptions from the old session.

This complete exercise performs one explicit reconnect. It needs the same local offline server as Chapter 2.

```csharp
using DMCBK.Core;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using Client client = new ClientBuilder()
    .UseUsername("ReconnectBot")
    .UseServer("localhost")
    .Build();

try
{
    await client.StartAsync(deadline.Token);
    if (!await client.Game.Player.WaitForSpawnAsync(deadline.Token))
        throw new InvalidOperationException("The first session did not place the player.");

    await client.ReconnectAsync(ct: deadline.Token);
    if (!await client.Game.Player.WaitForSpawnAsync(deadline.Token))
        throw new InvalidOperationException("The second session did not place the player.");

    await client.Game.Chat.SendAsync("Reconnected successfully.", deadline.Token);
    Console.WriteLine("The second session placed the player and sent chat.");
}
finally
{
    await client.StopAsync();
}
```

The client object remains the same. The underlying Minecraft session changes. A session-specific event bus or cancellation token from the first connection does not belong to the second connection.

## Configure automatic reconnect

A programmatic host can supply a policy without TOML:

```csharp
using DMCBK.Core;
using DMCBK.Core.Configuration;

var policy = new ConfigReconnectPolicyProvider(new ReconnectConfig
{
    MaxAttempts = 3,
    DelaySeconds = 2,
    BackoffFactor = 2,
    MaxDelaySeconds = 10,
    RetryOnKick = false
});

await using Client client = new ClientBuilder()
    .UseUsername("GuideBot")
    .UseServer("localhost")
    .UseReconnectPolicyProvider(policy)
    .Build();

Console.WriteLine(client.Status);
```

This example only constructs the client. The policy permits at most three automatic attempts after an unexpected disconnect. It refuses kicks.

The default has no automatic reconnect. `MaxAttempts = 0` disables it. A negative maximum permits unlimited attempts. Use bounded attempts while learning and testing.

## Bound asynchronous work

Use a cancellation token for connection, placement waits and game actions. The ClientGuide sample has a 45-second deadline.

Its Ctrl+C handler sets `e.Cancel = true` and cancels the token. This lets application code enter `finally` instead of ending immediately.

For a long-running worker, use the host's shutdown token. Use shorter linked tokens for individual requests. A slow action should not block shutdown indefinitely.

## Release resources in ownership order

1. Stop creating new application work.
2. Cancel outstanding application operations.
3. Await operations that must finish.
4. Call `StopAsync`.
5. Remove application event registrations.
6. Dispose the client.
7. Dispose host-owned services.

`StopAsync` ends the connection and reconnect supervisor work. Client disposal releases module resources in reverse registration order.

The `await using` pattern ensures disposal even after an exception. It does not undo arbitrary external actions that a plugin or host already performed.

Continue to [Chapter 7](07-test-and-deploy.md).

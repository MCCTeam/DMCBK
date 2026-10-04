using DMCBK.Core;
using DMCBK.Samples.ClientGuide;
using Umpk.Data.Java;

if (args is ["--self-test"])
{
    await ClientChecks.RunAsync();
    return 0;
}

if (args is not [string username, string host, string portText, string versionText]
    || !ushort.TryParse(portText, out ushort port) || port == 0
    || !JavaVersions.TryGetByName(versionText, out var version))
{
    Console.Error.WriteLine("Usage: ClientGuide <username> <host> <port> <version>");
    Console.Error.WriteLine("Or: ClientGuide --self-test");
    return 2;
}

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
ConsoleCancelEventHandler cancel = (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};
Console.CancelKeyPress += cancel;

try
{
    await using Client client = new ClientBuilder()
        .UseUsername(username)
        .UseServer(host, port)
        .UseVersion(version)
        .UseCommands()
        .Build();

    using var observer = new ClientObserver(client);
    try
    {
        await client.StartAsync(lifetime.Token);
        Console.WriteLine("Connected.");
        if (!await client.Game.Player.WaitForSpawnAsync(lifetime.Token))
            throw new InvalidOperationException("The session ended before player placement.");

        PlayerStatus status = await client.Game.Player.GetStatusAsync(lifetime.Token);
        Console.WriteLine($"Player: health={status.Health}, food={status.Food}, position={status.Position}");
        await client.Game.Chat.SendAsync("Hello from the DMCBK client guide.", lifetime.Token);
        Console.WriteLine("Chat sent.");
        await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token);
        return 0;
    }
    finally
    {
        await client.StopAsync();
        Console.WriteLine("Stopped.");
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The operation was cancelled or timed out.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Client failed: {error.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}

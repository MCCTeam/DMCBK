using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Shutdown signaling: fires once, never throws, never disconnects by itself.</summary>
public sealed class ClientShutdownTests
{
    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .Build();

    [Fact]
    public async Task ShutdownRequested_CompletesOnRequest()
    {
        await using Client client = BuildClient();
        Assert.False(client.ShutdownRequested.IsCompleted);
        client.RequestShutdown();
        Assert.True(client.ShutdownRequested.IsCompleted);
        await client.ShutdownRequested;
    }

    [Fact]
    public async Task RequestShutdown_IsIdempotent()
    {
        await using Client client = BuildClient();
        client.RequestShutdown();
        Task first = client.ShutdownRequested;
        client.RequestShutdown();
        Assert.Same(first, client.ShutdownRequested);
        Assert.True(client.ShutdownRequested.IsCompleted);
    }

    [Fact]
    public async Task RequestShutdown_DoesNotDisconnectByItself()
    {
        await using Client client = BuildClient();
        client.RequestShutdown();
        Assert.NotEqual(Umpk.Client.ClientStatus.Playing, client.Status);
    }
}

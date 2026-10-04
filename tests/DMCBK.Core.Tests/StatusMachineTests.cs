using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Status-machine transitions with an unconnected client: connecting to a closed local port fails fast with a typed <see cref="ConnectFailedException"/> and drives Created -> Connecting -> Disconnected.
/// An explicit version is pinned so the path never touches the network for a ping.
/// </summary>
public sealed class StatusMachineTests
{
    // Port 1 on loopback has nothing listening, so the connect is refused immediately (no network round trip).
    private const ushort ClosedPort = 1;

    private static Client BuildUnreachable()
    {
        Assert.True(JavaVersions.TryGetByName("1.21.5", out JavaVersion version));
        return new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("127.0.0.1", ClosedPort)
            .UseUsername("p1_skel")
            .UseVersion(version)
            .Build();
    }

    [Fact]
    public async Task StartAsync_ConnectRefused_Throws_And_TransitionsToDisconnected()
    {
        await using Client client = BuildUnreachable();
        var transitions = new List<ClientStatus>();
        client.StatusChanged += (_, e) => transitions.Add(e.Current);

        Assert.Equal(ClientStatus.Created, client.Status);

        await Assert.ThrowsAsync<ConnectFailedException>(() => client.StartAsync());

        Assert.Equal(ClientStatus.Disconnected, client.Status);
        Assert.Contains(ClientStatus.Connecting, transitions);
        Assert.Equal(ClientStatus.Disconnected, transitions[^1]);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_Throws()
    {
        await using Client client = BuildUnreachable();

        await Assert.ThrowsAsync<ConnectFailedException>(() => client.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());
    }

    [Fact]
    public async Task Chat_SendBeforeStart_Throws()
    {
        await using Client client = BuildUnreachable();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Game.Chat.SendAsync("hello"));
    }
}

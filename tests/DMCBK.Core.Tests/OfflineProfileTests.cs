using Umpk;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Offline profile construction and builder validation.</summary>
public sealed class OfflineProfileTests
{
    [Fact]
    public void ComputeProfile_IsDeterministic_AndNamed()
    {
        GameProfile first = OfflineIdentity.ComputeProfile("p1_skel");
        GameProfile second = OfflineIdentity.ComputeProfile("p1_skel");

        Assert.Equal("p1_skel", first.Name);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void ComputeProfile_DifferentNames_YieldDifferentUuids()
    {
        Guid a = OfflineIdentity.ComputeUuid("alice");
        Guid b = OfflineIdentity.ComputeUuid("bob");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// A client with no server is idle, not invalid: it builds, its commands and plugins run, and only a dial with nowhere to go is refused.
    /// That is what lets a host reach its prompt with an empty servers.toml instead of exiting during startup.
    /// </summary>
    [Fact]
    public async Task Builder_MissingEndpoint_BuildsAnIdleClient()
    {
        Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("p1_skel").Build();
        await using (client.ConfigureAwait(false))
        {
            Assert.Equal(Umpk.Client.ClientStatus.Created, client.Status);
            await Assert.ThrowsAsync<DmcbkNoServerConfiguredException>(() => client.StartAsync());
            await Assert.ThrowsAsync<DmcbkNoServerConfiguredException>(() => client.ReconnectAsync());
        }
    }

    [Fact]
    public void Builder_MissingUsername_Throws()
    {
        ClientBuilder builder = new ClientBuilder().UseCommands().UseBeacon().UseServer("localhost");
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Builder_UsernameTooLong_Throws()
    {
        ClientBuilder builder = new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("localhost")
            .UseUsername(new string('x', 17));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }
}

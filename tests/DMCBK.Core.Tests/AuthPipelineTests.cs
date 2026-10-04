using Umpk;
using Umpk.Auth;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Auth-pipeline coverage that needs no live server or network: the account model, the account-kind resolution table, the token-store round-trip, and the fail-fast guard when an online account has no host auth interaction.
/// </summary>
public sealed class AuthPipelineTests
{
    [Fact]
    public void Offline_Account_IsNotOnline()
    {
        DmcbkAccount account = DmcbkAccount.Offline("steve");
        Assert.Equal(DmcbkAccountKind.Offline, account.Kind);
        Assert.Equal("steve", account.User);
        Assert.False(account.IsOnline);
    }

    [Theory]
    [InlineData(DmcbkAccountKind.MicrosoftDeviceCode)]
    [InlineData(DmcbkAccountKind.MicrosoftBrowser)]
    [InlineData(DmcbkAccountKind.Yggdrasil)]
    public void OnlineKinds_AreOnline(DmcbkAccountKind kind)
        => Assert.True(new DmcbkAccount { Kind = kind, User = "u" }.IsOnline);

    [Theory]
    [InlineData(DmcbkAccountKind.Offline, AuthFlowKind.Offline)]
    [InlineData(DmcbkAccountKind.MicrosoftDeviceCode, AuthFlowKind.MicrosoftDeviceCode)]
    [InlineData(DmcbkAccountKind.MicrosoftBrowser, AuthFlowKind.MicrosoftBrowser)]
    [InlineData(DmcbkAccountKind.Yggdrasil, AuthFlowKind.Yggdrasil)]
    public void ToFlowKind_MapsEveryAccountKind(DmcbkAccountKind kind, AuthFlowKind expected)
        => Assert.Equal(expected, DmcbkAuthMapping.ToFlowKind(kind));

    /// <summary>
    /// The one branch MCC still owns for the Yggdrasil URL mapping: which account kinds resolve to a provider base at all (only Yggdrasil; offline and Microsoft accounts always use Mojang defaults), and that the delegation to <see cref="Umpk.Auth.YggdrasilEndpoints.EnsureTrailingSlash"/> actually runs end to end.
    /// The URL algebra itself (trailing-slash normalization, the sessionserver/ sub-API resolution) is UMPK's own contract now, pinned in <c>Umpk.Auth.Tests.YggdrasilEndpointTests</c>.
    /// </summary>
    [Fact]
    public void ProviderBaseUrl_IsNullForNonYggdrasilAndSlashTerminatedForYggdrasil()
    {
        Assert.Null(DmcbkAuthMapping.ProviderBaseUrl(DmcbkAccount.Offline("steve")));
        Assert.Null(DmcbkAuthMapping.ProviderBaseUrl(new DmcbkAccount { Kind = DmcbkAccountKind.MicrosoftDeviceCode, User = "u" }));

        var yggdrasil = new DmcbkAccount
        {
            Kind = DmcbkAccountKind.Yggdrasil,
            User = "u",
            AuthServerBaseUrl = new Uri("https://auth.example/api/yggdrasil"),
        };

        Assert.Equal(new Uri("https://auth.example/api/yggdrasil/"), DmcbkAuthMapping.ProviderBaseUrl(yggdrasil));
    }

    [Fact]
    public async Task StartAsync_OnlineAccount_NoHostInteraction_ThrowsInteractionUnavailable()
    {
        Assert.True(JavaVersions.TryGetByName("1.21.5", out JavaVersion version));

        // Default host (NullHostInterface) exposes no auth interaction; the guard fires before any network.
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("127.0.0.1", 1)
            .UseAccount(new DmcbkAccount { Kind = DmcbkAccountKind.MicrosoftDeviceCode, User = "steve@example.com" })
            .UseVersion(version)
            .Build();

        await Assert.ThrowsAsync<DmcbkAuthInteractionUnavailableException>(() => client.StartAsync());
        Assert.Equal(ClientStatus.Disconnected, client.Status);
    }

    [Fact]
    public async Task TokenStore_RoundTrips_ASession_ThroughAFileStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mcc-p2-tokenstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            ITokenStore store = new FileTokenStore(dir, TokenProtectors.CreateDefault());
            var session = new JavaSession(
                new GameProfile(Guid.NewGuid(), "steve"),
                "access-token-value",
                DateTimeOffset.UtcNow.AddHours(1),
                "refresh-token-value",
                AuthKind.Microsoft);

            await store.SetAsync("session:STEVE", session, default);
            JavaSession? read = await store.GetAsync<JavaSession>("session:STEVE", default);

            Assert.NotNull(read);
            Assert.Equal(session.Profile, read!.Profile);
            Assert.Equal(session.AccessToken, read.AccessToken);
            Assert.Equal(session.RefreshToken, read.RefreshToken);
            Assert.Equal(session.Kind, read.Kind);

            await store.RemoveAsync("session:STEVE", default);
            Assert.Null(await store.GetAsync<JavaSession>("session:STEVE", default));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Builder_UseUsername_ProducesOfflineAccount_AndBuilds()
    {
        // Offline sugar still works and still validates the 16-char offline username limit.
        Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("localhost")
            .UseUsername("p2_offline")
            .Build();
        Assert.NotNull(client);

        Assert.Throws<InvalidOperationException>(() => new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("localhost")
            .UseUsername(new string('x', 17))
            .Build());
    }
}

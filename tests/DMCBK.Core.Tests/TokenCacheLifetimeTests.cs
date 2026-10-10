using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk;
using Umpk.Auth;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class TokenCacheLifetimeTests
{
    [Fact]
    public async Task MemoryCache_ResumesSessionAcrossConnectionAttempts()
    {
        DmcbkSessionFactory factory = Factory(CacheMode.Memory);
        JavaSession session = Session("FirstPlayer");
        await factory.CreateTokenStore().SetAsync("session:PLAYER@EXAMPLE.COM", session, default);

        using var reconnect = new MinecraftAuthFlow(new MinecraftAuthOptions { TokenStore = factory.CreateTokenStore() });

        Assert.Equal(session, await reconnect.TryResumeAsync("player@example.com", default));
    }

    [Fact]
    public async Task MemoryCache_DoesNotLeakBetweenClientsOrAccounts()
    {
        DmcbkSessionFactory first = Factory(CacheMode.Memory);
        DmcbkSessionFactory second = Factory(CacheMode.Memory);
        await first.CreateTokenStore().SetAsync("session:FIRST@EXAMPLE.COM", Session("FirstPlayer"), default);

        using var otherClient = new MinecraftAuthFlow(new MinecraftAuthOptions { TokenStore = second.CreateTokenStore() });
        using var otherAccount = new MinecraftAuthFlow(new MinecraftAuthOptions { TokenStore = first.CreateTokenStore() });

        Assert.Null(await otherClient.TryResumeAsync("first@example.com", default));
        Assert.Null(await otherAccount.TryResumeAsync("second@example.com", default));
    }

    [Fact]
    public async Task DisabledCache_DoesNotResumeThePreviousAttempt()
    {
        DmcbkSessionFactory factory = Factory(CacheMode.None);
        await factory.CreateTokenStore().SetAsync("session:PLAYER@EXAMPLE.COM", Session("FirstPlayer"), default);
        using var reconnect = new MinecraftAuthFlow(new MinecraftAuthOptions { TokenStore = factory.CreateTokenStore() });

        Assert.Null(await reconnect.TryResumeAsync("player@example.com", default));
    }

    [Fact]
    public async Task DiskCache_ResumesAcrossClientInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dmcbk-auth-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            JavaSession session = Session("FirstPlayer");
            await Factory(CacheMode.Disk, directory).CreateTokenStore().SetAsync("session:PLAYER@EXAMPLE.COM", session, default);
            using var restarted = new MinecraftAuthFlow(new MinecraftAuthOptions { TokenStore = Factory(CacheMode.Disk, directory).CreateTokenStore() });

            Assert.Equal(session, await restarted.TryResumeAsync("player@example.com", default));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static JavaSession Session(string name) => new(
        new GameProfile(Guid.NewGuid(), name), "FAKE_ACCESS", DateTimeOffset.UtcNow.AddHours(24), "FAKE_REFRESH", AuthKind.Microsoft);

    private static DmcbkSessionFactory Factory(CacheMode mode, string? path = null)
    {
        var gameSession = new GameSession();
        var translations = new HostTranslations();
        return new DmcbkSessionFactory(
            DmcbkAccount.Offline("cache_test"), path, null, new ClientFeatures(), NullLoggerFactory.Instance,
            new NullHostInterface(), new DmcbkConfiguration { Accounts = new AccountsConfig { SessionCache = mode } },
            null, false, false, new ChatApi(), new InventoryApi(gameSession, translations), gameSession, translations);
    }
}

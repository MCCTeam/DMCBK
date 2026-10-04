using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Configuration;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// What <c>connect</c> can read.
/// A colon is outside Brigadier's unquoted character set, so the reader stopped at it and <c>host:port</c> failed on trailing data, and the resolver then only matched a name in servers.toml. Nothing here dials: the account is resolved before the server, so an unknown account proves the line was read without opening a socket.
/// </summary>
public sealed class ConnectCommandTests
{
    [Theory]
    [InlineData("connect 127.0.0.1:25601 nobody")]
    [InlineData("connect play.example.net:25566 nobody")]
    [InlineData("connect localhost nobody")]
    [InlineData("connect \"a server\" nobody")]
    public async Task Connect_ReadsTheAddressFormsItDocuments(string line)
    {
        await using Client client = BuildClient();

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("nobody", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing data", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", "localhost", 25565)]
    [InlineData("127.0.0.1:25601", "127.0.0.1", 25601)]
    [InlineData("play.example.net:25566", "play.example.net", 25566)]
    public void ResolveServer_TakesABareAddressWhenNoConfiguredNameMatches(string value, string host, int port)
    {
        ConfiguredServer? resolved = SessionCommandHelpers.ResolveServer(new DmcbkConfiguration(), value);

        Assert.NotNull(resolved);
        Assert.Equal(host, resolved.Host);
        Assert.Equal(port, resolved.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":25565")]
    [InlineData("host:")]
    [InlineData("host:notaport")]
    [InlineData("host:99999")]
    public void ResolveServer_RefusesWhatIsNeitherANameNorAnAddress(string value)
        => Assert.Null(SessionCommandHelpers.ResolveServer(new DmcbkConfiguration(), value));

    [Fact]
    public void ResolveServer_PrefersAConfiguredNameOverReadingItAsAHost()
    {
        var config = new DmcbkConfiguration
        {
            Servers = new ServersConfig
            {
                Servers = [new ConfiguredServer { Name = "home", Host = "10.0.0.5", Port = 25599 }],
            },
        };

        ConfiguredServer? resolved = SessionCommandHelpers.ResolveServer(config, "home");

        Assert.Equal("10.0.0.5", resolved!.Host);
        Assert.Equal(25599, resolved.Port);
    }

    [Fact]
    public async Task Connect_WithAThirdTokenAnswersWithTheUsage()
    {
        await using Client client = BuildClient();

        CmdResult result = await client.Commands.DispatchAsync("connect localhost nobody extra");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("connect", result.Message, StringComparison.Ordinal);
    }

    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon().UseConfiguration(new DmcbkConfiguration
        {
            ResolvedHost = "localhost",
            ResolvedPort = 25565,
            ResolvedVersion = "auto",
            ResolvedAccount = new ConfiguredAccount { Name = "Tester", Kind = DmcbkAccountKind.Offline },
        }).Build();
}

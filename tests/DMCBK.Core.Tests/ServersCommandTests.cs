using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Coverage for <c>/servers add</c>: the happy path writes servers.toml and selects the entry, bad input fails without touching the file, and a missing configurations folder is refused rather than guessed at.
/// Uses throwaway folders and never connects.
/// </summary>
public sealed class ServersCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcc-srv-cfg", Guid.NewGuid().ToString("N"));

    public ServersCommandTests()
    {
        Directory.CreateDirectory(_folder);
        new MccConfigurationLoader(_folder, loggerFactory: NullLoggerFactory.Instance).Load(generateMissing: true);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string ServersFile => ConfigurationPaths.ServersFile(_folder);

    private Client BuildClient(MccConfiguration config)
        => new ClientBuilder().UseCommands().UseBeacon().UseConfiguration(config with
        {
            ResolvedHost = "localhost",
            ResolvedPort = 25565,
            ResolvedVersion = "auto",
            ResolvedAccount = new ConfiguredAccount { Name = "Tester", Kind = MccAccountKind.Offline },
        }).UseModule<DMCBK.Core.Configuration.IConfigurationStorage>(_ => new MccConfigurationLoader(config.SourceFolder ?? Path.GetTempPath())).Build();

    private Client BuildClient()
        => BuildClient(new MccConfiguration { SourceFolder = _folder });

    [Fact]
    public async Task Add_SavesEntrySelectsItAndConfirms()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("servers add Home localhost:25565");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("Saved server 'Home' (localhost:25565).", result.Message, StringComparison.Ordinal);

        string file = File.ReadAllText(ServersFile);
        Assert.Contains("Home", file, StringComparison.Ordinal);
        Assert.Contains("localhost", file, StringComparison.Ordinal);
        Assert.Contains("Active = \"Home\"", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WithoutPort_DefaultsTo25565()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("servers add Hypixel mc.hypixel.net");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("(mc.hypixel.net:25565)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_OverwritesAnExistingName()
    {
        await using Client client = BuildClient();
        await client.Commands.DispatchAsync("servers add Home localhost:1");
        CmdResult result = await client.Commands.DispatchAsync("servers add Home localhost:2");

        Assert.Equal(CmdStatus.Done, result.Status);
        string file = File.ReadAllText(ServersFile);
        Assert.Contains("Port = 2", file, StringComparison.Ordinal);
        Assert.DoesNotContain("Port = 1", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_QuotedNameWithSpaces_RoundTrips()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("servers add \"My Home\" localhost");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("My Home", File.ReadAllText(ServersFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("servers add Home localhost:abc")]
    [InlineData("servers add Home localhost:")]
    [InlineData("servers add Home :25565")]
    [InlineData("servers add Home \"has space\"")]
    public async Task Add_BadAddress_RefusesAndWritesNothing(string line)
    {
        await using Client client = BuildClient();
        string before = File.ReadAllText(ServersFile);

        CmdResult result = await client.Commands.DispatchAsync(line);

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(before, File.ReadAllText(ServersFile));
    }

    [Fact]
    public async Task Add_MissingAddress_ShowsUsage()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("servers add Home");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("servers add", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_SavedNameAsAddress_PointsAtTheLiteral()
    {
        await using Client client = BuildClient();
        await client.Commands.DispatchAsync("servers add Home localhost");

        CmdResult result = await client.Commands.DispatchAsync("servers add Other Home");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("already a saved server name", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WithoutAConfigurationFolder_SaysSoRatherThanGuessing()
    {
        await using Client client = BuildClient(new MccConfiguration());

        CmdResult result = await client.Commands.DispatchAsync("servers add Home localhost");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("nowhere to save", result.Message, StringComparison.Ordinal);
    }
}

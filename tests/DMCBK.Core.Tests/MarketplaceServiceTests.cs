using System.Net;
using DMCBK.Core.Plugins;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class MarketplaceServiceTests
{
    [Fact]
    public async Task ComposedModulesImportLocalSourcePersistPolicyAndRestoreOnRestart()
    {
        using var fixture = new Fixture();
        await using (Client client = fixture.Client())
        {
            PluginActionResult installed = await client.PluginMarket!.InstallAsync(fixture.Source, assumeYes: true);
            Assert.True(installed.Success, installed.Message);
            Assert.True(Assert.Single(client.PluginHost!.List()).Loaded);
            Assert.True((await client.PluginHost.DisableAsync("probe")).Success);
            Assert.True((await client.PluginMarket.PinAsync("probe")).Success);
            Assert.DoesNotContain("enabled", File.ReadAllText(Path.Combine(fixture.Source, "plugin.toml")));
            Assert.Equal("persistent", File.ReadAllText(Path.Combine(fixture.Root, "plugins", "userdata", "probe", "data", "value.txt")));
            PluginMarketInfo? info = await client.PluginMarket.InfoAsync("probe");
            Assert.Equal("any", info!.Target); Assert.Equal(64, info.AssetHash!.Length);
        }
        await using (Client restarted = fixture.Client())
        {
            await restarted.GetModule<PluginHost>().LoadAllAsync();
            PluginInfo plugin = Assert.Single(restarted.PluginHost!.List());
            Assert.False(plugin.Enabled); Assert.False(plugin.Loaded);
            Assert.True((await restarted.PluginMarket!.InfoAsync("probe"))!.Pinned);
            Assert.True((await restarted.PluginHost.EnableAsync("probe")).Success);
            Assert.Equal("ready", restarted.Variables.Get("probe"));
        }
        Assert.Equal(0, fixture.Http.Requests);
    }

    [Fact]
    public async Task DirectImportRequiresReviewAndChangedPayloadRequiresANewVersion()
    {
        using var fixture = new Fixture(); await using Client client = fixture.Client();
        Assert.False((await client.PluginMarket!.InstallAsync(fixture.Source)).Success);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "plugins", "plugins.lock.toml")));
        Assert.True((await client.PluginMarket.InstallAsync(fixture.Source, assumeYes: true)).Success);
        File.AppendAllText(Path.Combine(fixture.Source, "Probe.cs"), "\n// changed");
        PluginActionResult changed = await client.PluginMarket.InstallAsync(fixture.Source, assumeYes: true);
        Assert.False(changed.Success);
        Assert.Equal("1.0.0", Assert.Single(client.PluginHost!.List()).Version);
    }

    [Fact]
    public async Task RollbackHistoryShowsCompletedGraphsAndRestoresEnabledState()
    {
        using var fixture = new Fixture(); await using Client client = fixture.Client();
        Assert.True((await client.PluginMarket!.InstallAsync(fixture.Source, assumeYes: true)).Success);
        Assert.True((await client.PluginHost!.DisableAsync("probe")).Success);
        IReadOnlyList<PluginRollbackInfo> history = await client.PluginMarket.RollbackHistoryAsync();
        Assert.Equal(2, history.Count);
        PluginRollbackInfo change = Assert.Single(history, item => item.PreviousVersions.Count > 0);
        Assert.Equal(["probe 1.0.0"], change.PreviousVersions);
        Assert.True((await client.PluginMarket.RollbackAsync(change.TransactionId)).Success);
        Assert.True(Assert.Single(client.PluginHost.List()).Enabled);
        Assert.Equal("persistent", File.ReadAllText(Path.Combine(fixture.Root, "plugins", "userdata", "probe", "data", "value.txt")));
        Assert.Equal(0, fixture.Http.Requests);
    }

    [Fact]
    public async Task VersionHistoryIncludesYankedAndIncompatibleReleasesWithoutPayloadDownloads()
    {
        using var fixture = new Fixture(); await using Client client = fixture.Client();
        string catalogueRoot = Path.Combine(fixture.Root, "catalogue"); Directory.CreateDirectory(Path.Combine(catalogueRoot, "catalog"));
        File.WriteAllText(Path.Combine(catalogueRoot, "mcc-marketplace.toml"), Tomlet.TomletMain.TomlStringFrom(new MarketplaceIndex
        {
            SchemaVersion = 2,
            Id = "publisher",
            Name = "Test",
            Plugins = [new() { Id = "probe", Releases = "catalog/probe.toml" }],
        }));
        var catalogue = new ReleaseCatalogue { SchemaVersion = 2, Id = "probe", Releases = [] };
        foreach (string version in new[] { "1.0.0", "2.0.0", "3.0.0-beta.1" })
            catalogue.Releases.Add(new()
            {
                Version = version,
                ApiVersion = version == "2.0.0" ? "2.0" : "1.0",
                Dmcbk = "*",
                Umpk = "*",
                Framework = "net10.0",
                Yanked = version == "1.0.0",
                Assets = [new() { Kind = "source", Target = "any", Url = "https://assets.test/" + version + ".zip", Sha256 = new string('a', 64) }]
            });
        File.WriteAllText(Path.Combine(catalogueRoot, "catalog", "probe.toml"), Tomlet.TomletMain.TomlStringFrom(catalogue));
        Assert.True((await client.PluginMarket!.MarketplaceAddAsync(catalogueRoot)).Success);
        IReadOnlyList<PluginReleaseInfo> history = await client.PluginMarket.VersionsAsync("probe", "publisher");
        Assert.Equal(["3.0.0-beta.1", "2.0.0", "1.0.0"], history.Select(release => release.Version));
        Assert.True(history[2].Yanked);
        Assert.NotNull(history[1].Incompatibility);
        Assert.Null(history[0].Incompatibility);
        Assert.Equal("any", Assert.Single(history[0].Assets).Target);
        Assert.Empty(await client.PluginMarket.VersionsAsync("probe", "other-publisher"));
        Assert.Equal(0, fixture.Http.Requests);
    }

    [Fact]
    public void PackageDefaultsOverlayUserValuesWithoutRewritingTheUserFile()
    {
        using var fixture = new Fixture(); string user = Path.Combine(fixture.Root, "settings.toml"), defaults = Path.Combine(fixture.Root, "defaults.toml");
        File.WriteAllText(user, "Count = 7\n[Nested]\nUser = \"kept\"\n");
        File.WriteAllText(defaults, "Count = 3\nNewValue = \"new\"\n[Nested]\nUser = \"default\"\nAdded = 4\n");
        string before = File.ReadAllText(user);
        var settings = new PluginSettings(user, NullLogger.Instance, null, defaults);
        TestSettings value = settings.Load<TestSettings>();
        Assert.Equal(7, value.Count); Assert.Equal("new", value.NewValue); Assert.Equal("kept", value.Nested.User); Assert.Equal(4, value.Nested.Added);
        Assert.Equal(before, File.ReadAllText(user));
    }

    [Fact]
    public void IncompatibleOptionalProvidersAreUnavailableThroughServicesAndMessaging()
    {
        var services = new PluginServiceHub(NullLogger.Instance) { IsExchangeable = _ => true, CanCommunicate = (_, _) => false };
        using IDisposable registration = services.Register<ITestService>("provider", new TestService());
        Assert.False(services.TryGet<ITestService>("consumer", out _));
        services.CanCommunicate = (_, _) => true;
        Assert.True(services.TryGet<ITestService>("consumer", out _));
        var messages = new PluginMessengerHub(NullLogger.Instance) { IsExchangeable = _ => true, CanCommunicate = (_, _) => false };
        int received = 0; using IDisposable subscription = messages.Subscribe<string>("consumer", _ => received++);
        messages.Publish("provider", "message"); Assert.Equal(0, received);
        messages.CanCommunicate = (_, _) => true; messages.Publish("provider", "message"); Assert.Equal(1, received);
    }

    [Fact]
    public async Task ConfirmationCarriesTheExactAssetAndDecliningDoesNotCommit()
    {
        using var fixture = new Fixture(); await using Client client = fixture.Client();
        InstallConfirmation? review = null;
        client.PluginMarket!.Confirm = (confirmation, _) => { review = confirmation; return ValueTask.FromResult(false); };
        Assert.False((await client.PluginMarket.InstallAsync(fixture.Source)).Success);
        Assert.NotNull(review);
        PluginPlannedChange change = Assert.Single(review.Changes!);
        Assert.Equal("probe", change.Id); Assert.Equal("development", change.Marketplace);
        Assert.Equal("source", change.Kind); Assert.Equal("any", change.Target);
        Assert.Equal(64, change.Sha256.Length); Assert.Contains(change.Sha256, review.Text);
        Assert.Null(change.PreviousVersion);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "plugins", "plugins.lock.toml")));
        Assert.Empty(client.PluginHost!.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRetainsUserDataUnlessExplicitlyPurged(bool purge)
    {
        using var fixture = new Fixture(); await using Client client = fixture.Client();
        Assert.True((await client.PluginMarket!.InstallAsync(fixture.Source, assumeYes: true)).Success);
        string user = Path.Combine(fixture.Root, "plugins", "userdata", "probe");
        Assert.True((await client.PluginMarket.UninstallAsync("probe", purge)).Success);
        Assert.Empty(client.PluginHost!.List());
        Assert.Equal(!purge, Directory.Exists(user));
        if (!purge) Assert.Equal("persistent", File.ReadAllText(Path.Combine(user, "data", "value.txt")));
    }

    public sealed class TestSettings { public int Count { get; set; } public string NewValue { get; set; } = ""; public NestedSettings Nested { get; set; } = new(); }
    public sealed class NestedSettings { public string User { get; set; } = ""; public int Added { get; set; } }
    private interface ITestService { }
    private sealed class TestService : ITestService { }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dmcbk-service-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public NoHttp Http { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Source);
            File.WriteAllText(Path.Combine(Source, "plugin.toml"), """
                schema-version = 2
                id = "probe"
                version = "1.0.0"
                kind = "source"
                target = "any"
                entry = "Probe.cs"
                framework = "net10.0"
                api-version = "1.0"
                dmcbk = "*"
                umpk = "*"
                """);
            File.WriteAllText(Path.Combine(Source, "Probe.cs"), """
                using System.IO;
                using System.Threading.Tasks;
                using DMCBK.PluginSdk;
                public sealed class Probe : IPlugin
                {
                    public void Configure(PluginDescriptor descriptor) => descriptor.Id = "probe";
                    public Task ActivateAsync(PluginContext context)
                    {
                        context.Variables.Set("probe", "ready");
                        File.WriteAllText(context.Storage.GetPath("value.txt"), "persistent");
                        return Task.CompletedTask;
                    }
                }
                """);
        }
        public Client Client() => new ClientBuilder().UseUsername("Tester").UseCommands().UseBeacon()
            .UsePlugins(new(Path.Combine(Root, "plugins")))
            .UseMarketplace(new(Path.Combine(Root, "plugins"), Path.Combine(Root, "marketplaces.toml"))
            { Runtime = client => client.GetModule<PluginHost>(), HttpClient = new HttpClient(Http, disposeHandler: false) }).Build();
        public void Dispose() { Http.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    }
}

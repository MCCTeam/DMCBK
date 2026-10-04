using System.Net;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using DMCBK.Testing;
using Tomlet;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginAutoUpdateV2Tests
{
    [Fact]
    public async Task RecentMetadataDoesNotRefetch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Market.StartAutoUpdateAsync();
        Assert.Empty(fixture.Transport.Requests);
        Assert.Equal("1.0.0", fixture.Version);
    }

    [Fact]
    public async Task DailyCheckFetchesMetadataWithoutDownloadingPayloads()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        await fixture.Market.StartAutoUpdateAsync();
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.DoesNotContain(fixture.Transport.Requests, path => path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Equal("1.0.0", fixture.Version);
        Assert.Equal([TimeSpan.FromSeconds(5)], fixture.Delays);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OfflineAndDisabledMarketsSkipNetworkAndDelay(bool offline, bool disabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(25)); fixture.Offline = offline;
        if (disabled) Assert.True((await fixture.Market.MarketplaceAutoUpdateAsync("publisher", "off")).Success);
        await fixture.Market.StartAutoUpdateAsync();
        Assert.Empty(fixture.Transport.Requests); Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task ApplyInstallsTheSelectedNewAsset()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Market.MarketplaceAutoUpdateAsync("publisher", "apply");
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        await fixture.Market.StartAutoUpdateAsync();
        Assert.Equal("1.1.0", fixture.Version);
        Assert.Single(fixture.Transport.Requests, path => path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.True(Assert.Single(fixture.Host.Plugins.List()).Loaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptOutAndPinsPreserveTheInstalledVersion(bool pinned)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Market.MarketplaceAutoUpdateAsync("publisher", "apply");
        if (pinned) await fixture.Market.PinAsync("probe");
        else
        {
            var installer = new MarketplaceInstaller(fixture.Host.PluginsRoot, fixture.Http,
                fixture.Host.Plugins, new(HostInfo.FromClient(fixture.Host.Client), []));
            await installer.ApplyAsync(await installer.PlanPolicyAsync("probe", updatePolicy: "off"));
        }
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        await fixture.Market.StartAutoUpdateAsync();
        Assert.Equal("1.0.0", fixture.Version);
        Assert.DoesNotContain(fixture.Transport.Requests, path => path.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveSessionDefersApplicationUntilDisconnected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Market.MarketplaceAutoUpdateAsync("publisher", "apply");
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        await fixture.Host.RunSessionAsync(async _ =>
        {
            await fixture.Market.StartAutoUpdateAsync();
            Assert.Equal("1.0.0", fixture.Version);
            Assert.DoesNotContain(fixture.Transport.Requests, path => path.EndsWith(".zip", StringComparison.Ordinal));
        });
        // Reload briefly removes the previous plugin from the runtime list.
        Assert.True(await fixture.Host.WaitForAsync(() =>
            fixture.Host.Plugins.List().Any(plugin =>
                plugin.Id == "probe" && plugin.Version == "1.1.0" && plugin.Loaded)));
        Assert.Equal("1.1.0", fixture.Version);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class Transport : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = [];
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsoluteUri; Requests.Add(path);
            return Task.FromResult(new HttpResponseMessage(Routes.ContainsKey(path) ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            { Content = new ByteArrayContent(Routes.GetValueOrDefault(path) ?? []) });
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public PluginTestHost Host { get; } = PluginTestHost.Create();
        public Transport Transport { get; } = new();
        public HttpClient Http { get; }
        public MarketplaceService Market { get; }
        public Clock Clock { get; } = new();
        public List<TimeSpan> Delays { get; } = [];
        public bool Offline { get; set; }
        public string Version => Host.Plugins.List().Single().Version;
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "dmcbk-autoupdate-" + Guid.NewGuid().ToString("N"));
        private Fixture()
        {
            Directory.CreateDirectory(_workspace);
            Http = new(Transport, disposeHandler: false);
            Market = new(Host.Client, Host.PluginsRoot, Path.Combine(_workspace, "registry.toml"), Http, Host.Plugins)
            {
                AutomaticUpdates = new()
                {
                    Time = Clock,
                    Offline = () => Offline,
                    RandomSeconds = (_, _) => 5,
                    Delay = (delay, _) => { Delays.Add(delay); return Task.CompletedTask; },
                },
            };
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var catalogue = new ReleaseCatalogue { SchemaVersion = 2, Id = "probe" };
            string source = Path.Combine(fixture._workspace, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Probe.cs"), """
                using System.Threading.Tasks;
                using DMCBK.PluginSdk;
                public sealed class Probe : IPlugin
                {
                    public void Configure(PluginDescriptor descriptor) => descriptor.Id = "probe";
                    public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
                }
                """);
            foreach (string version in new[] { "1.0.0", "1.1.0" })
            {
                File.WriteAllText(Path.Combine(source, "plugin.toml"), $$"""
                    schema-version = 2
                    id = "probe"
                    version = "{{version}}"
                    kind = "source"
                    target = "any"
                    entry = "Probe.cs"
                    framework = "net10.0"
                    api-version = "1.0"
                    dmcbk = "*"
                    umpk = "*"
                    """);
                PackedPlugin packed = PluginPackageBuilder.Pack(source, source, Path.Combine(fixture._workspace, "packages"),
                    "source", "any", new Uri("https://market.test/assets/"));
                catalogue.Releases.Add(packed.Release);
                fixture.Transport.Routes[packed.Release.Assets.Single().Url] = File.ReadAllBytes(packed.ArchivePath);
            }
            var index = new MarketplaceIndex
            {
                SchemaVersion = 2,
                Id = "publisher",
                Name = "Test",
                Plugins = [new() { Id = "probe", Releases = "catalog/probe.toml" }]
            };
            fixture.Transport.Routes["https://market.test/mcc-marketplace.toml"] = System.Text.Encoding.UTF8.GetBytes(TomletMain.TomlStringFrom(index));
            fixture.Transport.Routes["https://market.test/catalog/probe.toml"] = System.Text.Encoding.UTF8.GetBytes(TomletMain.TomlStringFrom(catalogue));
            Assert.True((await fixture.Market.MarketplaceAddAsync("https://market.test/mcc-marketplace.toml")).Success);
            Assert.True((await fixture.Market.MarketplaceAutoUpdateAsync("publisher", "check")).Success);
            Assert.True((await fixture.Market.InstallAsync("probe", "1.0.0", assumeYes: true)).Success);
            fixture.Transport.Requests.Clear();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Market.DisposeAsync(); await Host.DisposeAsync(); Http.Dispose(); Transport.Dispose();
            Directory.Delete(_workspace, true);
        }
    }
}

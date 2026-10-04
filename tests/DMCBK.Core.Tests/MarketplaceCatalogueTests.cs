using System.Net;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Tomlet;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class MarketplaceCatalogueTests
{
    [Fact]
    public async Task FetchesMetadataOnlyAndKeepsLastValidSnapshotOnFailure()
    {
        using var fixture = new Fixture();
        fixture.Http.Documents["https://market.test/mcc-marketplace.toml"] = fixture.Index();
        fixture.Http.Documents["https://market.test/catalog/app.toml"] = fixture.Catalogue();
        MarketplaceSnapshot first = await fixture.Client.RefreshAsync(fixture.Binding);
        Assert.Single(first.Catalogues);
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.DoesNotContain(fixture.Http.Requests, path => path.EndsWith(".zip"));
        fixture.Http.Documents["https://market.test/catalog/app.toml"] = "schema-version = 1\nid = \"app\"";
        await Assert.ThrowsAsync<MarketplaceException>(() => fixture.Client.RefreshAsync(fixture.Binding));
        MarketplaceSnapshot? cached = await fixture.Client.ReadCachedAsync(fixture.Binding);
        Assert.Equal(first.FetchedAt, cached!.FetchedAt);
        Assert.Equal("2.0.0", Assert.Single(Assert.Single(cached.Catalogues).Catalogue.Releases).Version);
    }

    [Fact]
    public async Task ChangedCompatibilityCannotReplaceCachedPublishedMetadata()
    {
        using var fixture = new Fixture();
        fixture.Http.Documents[fixture.Binding.Source] = fixture.Index();
        fixture.Http.Documents["https://market.test/catalog/app.toml"] = fixture.Catalogue();
        MarketplaceSnapshot prior = await fixture.Client.RefreshAsync(fixture.Binding);
        ReleaseCatalogue changed = ReleaseCatalogue.Parse(fixture.Catalogue());
        changed.Releases[0].Requires["new-dependency"] = "^1.0.0";
        fixture.Http.Documents["https://market.test/catalog/app.toml"] = TomletMain.TomlStringFrom(changed);
        Assert.Equal("pack.immutable-release-conflict", (await Assert.ThrowsAsync<MarketplaceException>(
            () => fixture.Client.RefreshAsync(fixture.Binding))).Code);
        MarketplaceSnapshot cached = (await fixture.Client.ReadCachedAsync(fixture.Binding))!;
        Assert.Equal(prior.FetchedAt, cached.FetchedAt);
        Assert.Empty(cached.Catalogues[0].Catalogue.Releases[0].Requires);
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("plugin")]
    [InlineData("path")]
    public async Task RejectsChangedBindingsAndEscapingCataloguePaths(string fault)
    {
        using var fixture = new Fixture();
        fixture.Http.Documents["https://market.test/mcc-marketplace.toml"] = fixture.Index(
            publisher: fault == "publisher" ? "other" : "official",
            path: fault == "path" ? "../outside.toml" : "catalog/app.toml");
        fixture.Http.Documents["https://market.test/catalog/app.toml"] = fixture.Catalogue(fault == "plugin" ? "other" : "app");
        await Assert.ThrowsAsync<MarketplaceException>(() => fixture.Client.RefreshAsync(fixture.Binding));
        Assert.Null(await fixture.Client.ReadCachedAsync(fixture.Binding));
    }

    [Fact]
    public async Task LocalDirectoryAndFileUriUseTheSameRelativeCatalogue()
    {
        using var fixture = new Fixture();
        string folder = Path.Combine(fixture.Root, "source"); Directory.CreateDirectory(Path.Combine(folder, "catalog"));
        await File.WriteAllTextAsync(Path.Combine(folder, "mcc-marketplace.toml"), fixture.Index());
        await File.WriteAllTextAsync(Path.Combine(folder, "catalog", "app.toml"), fixture.Catalogue());
        foreach (string source in new[] { folder, new Uri(Path.Combine(folder, "mcc-marketplace.toml")).AbsoluteUri })
        {
            MarketplaceSnapshot snapshot = await fixture.Client.RefreshAsync(new() { Id = "official", Source = source });
            Assert.Equal("app", Assert.Single(snapshot.Catalogues).Catalogue.Id);
        }
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task EnforcesMetadataSizeBeforeCaching()
    {
        using var fixture = new Fixture();
        fixture.Http.Documents[fixture.Binding.Source] = new string(' ', 100);
        var bounded = new MarketplaceCatalogueClient(new HttpClient(fixture.Http), fixture.Root, metadataBytes: 32);
        MarketplaceException error = await Assert.ThrowsAsync<MarketplaceException>(() => bounded.RefreshAsync(fixture.Binding));
        Assert.Equal("catalogue.metadata-size", error.Code);
    }

    [Fact]
    public void LegacyRegistriesAndLocksCannotDefaultToVersionTwo()
    {
        Assert.Throws<MarketplaceException>(() => MarketplaceRegistry.Parse("marketplaces = []"));
        Assert.Throws<MarketplaceException>(() => InstallationLock.Parse("plugins = []"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dmcbk-catalogue-" + Guid.NewGuid().ToString("N"));
        public MemoryHttp Http { get; } = new();
        public MarketplaceBinding Binding { get; } = new() { Id = "official", Source = "https://market.test/mcc-marketplace.toml" };
        public MarketplaceCatalogueClient Client { get; }
        public Fixture() => Client = new(new HttpClient(Http), Root);
        public string Index(string publisher = "official", string path = "catalog/app.toml") => TomletMain.TomlStringFrom(new MarketplaceIndex
        {
            SchemaVersion = 2,
            Id = publisher,
            Name = "Official",
            Plugins = [new PluginIdentity { Id = "app", Releases = path }],
        });
        public string Catalogue(string id = "app") => TomletMain.TomlStringFrom(new ReleaseCatalogue
        {
            SchemaVersion = 2,
            Id = id,
            Releases = [new PluginRelease { Version = "2.0.0", ApiVersion = "1.0", Dmcbk = "*", Umpk = "*", Framework = "net10.0",
                Assets = [new ReleaseAsset { Kind = "source", Target = "any", Url = "https://assets.test/app.zip", Sha256 = new string('a', 64) }] }],
        });
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class MemoryHttp : HttpMessageHandler
    {
        public Dictionary<string, string> Documents { get; } = [];
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string uri = request.RequestUri!.AbsoluteUri; Requests.Add(uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Documents[uri]) });
        }
    }
}

using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class MarketplaceResolverTests
{
    private static readonly HostInfo Host = new("1.0", "0.1.0-preview.1", "0.9.0-beta.4", "mcc", "2.0.0", "linux-x64", new HashSet<string> { "commands" });

    [Fact]
    public void ParsesReleaseHistoryAndDependencyTables()
    {
        ReleaseCatalogue catalogue = ReleaseCatalogue.Parse("""
            schema-version = 2
            id = "example"
            [[releases]]
            version = "2.0.0"
            api-version = "1.0"
            dmcbk = ">=0.1.0-preview.1 <0.2.0"
            umpk = ">=0.9.0-beta.4 <0.10.0"
            framework = "net10.0"
            assets = [{ kind = "compiled", target = "any", url = "https://example.org/plugin.zip", sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }]
            [releases.requires]
            tools = "^2.1.0"
            [releases.optional]
            alerts = "^2.0.0"
            [releases.hosts]
            mcc = ">=2.0.0 <3.0.0"
            """);
        Assert.Equal("^2.1.0", Assert.Single(catalogue.Releases).Requires["tools"]);
        Assert.Empty(ReleaseCompatibility.Check(catalogue.Releases[0], Host));
        Assert.Throws<MarketplaceException>(() => MarketplaceIndex.Parse("id = \"old\"\nname = \"Legacy\""));
    }

    [Fact]
    public void ChoosesOnlyTheDetectedPlatformAsset()
    {
        PluginRelease release = Release("2.0.0");
        release.Assets = [Asset("compiled", "win-x86"), Asset("compiled", "linux-x64"), Asset("source", "any")];
        Assert.Equal("linux-x64", AssetSelector.Select(release, "linux-x64").Target);
        Assert.Equal("win-x86", AssetSelector.Select(release, "win-x86").Target);
        Assert.Equal("source", AssetSelector.Select(release, "linux-x64", new(PreferSource: true)).Kind);
        Assert.Throws<MarketplaceException>(() => AssetSelector.Select(release, "osx-arm64"));
        Assert.Equal("source", AssetSelector.Select(release, "osx-arm64", new(AllowSourceFallback: true)).Kind);
        release.Assets = [Asset("source", "any")];
        Assert.Equal("source", AssetSelector.Select(release, "osx-arm64").Kind);
    }

    [Fact]
    public void BacktracksWhenLatestConflictsWithAnInstalledDependent()
    {
        var resolver = Resolver(Source("app", Release("2.0.0", ("tools", "^2.0.0")), Release("1.0.0", ("tools", "^1.0.0"))),
            Source("consumer", Release("1.0.0", ("tools", "^1.0.0"))), Source("tools", Release("2.0.0"), Release("1.5.0")));
        DependencyResolution graph = resolver.Resolve(new("app", Update: true), [new("consumer", "official", "1.0.0"), new("tools", "official", "1.5.0")]);
        Assert.Equal("1.0.0", graph.Plugins.Single(plugin => plugin.Id == "app").Release.Version);
        Assert.Equal("1.5.0", graph.Plugins.Single(plugin => plugin.Id == "tools").Release.Version);
        Assert.Equal("tools", graph.Plugins[0].Id);
        Assert.Throws<MarketplaceException>(() => resolver.Resolve(new("app", ExactVersion: "2.0.0"), [new("consumer", "official", "1.0.0")]));
    }

    [Fact]
    public void PinsSurviveUpdatesAndExactRequestsNeverSubstitute()
    {
        var resolver = Resolver(Source("app", Release("2.0.0"), Release("1.0.0")));
        InstalledVersion[] installed = [new("app", "official", "1.0.0", Pinned: true)];
        Assert.Equal("1.0.0", Assert.Single(resolver.Resolve(new("app", Update: true), installed).Plugins).Release.Version);
        Assert.Throws<MarketplaceException>(() => resolver.Resolve(new("app", ExactVersion: "2.0.0"), installed));
        Assert.Throws<MarketplaceException>(() => resolver.Resolve(new("app", ExactVersion: "3.0.0")));
    }

    [Fact]
    public void RejectsRequiredCyclesWithoutInstallingAnything()
    {
        var resolver = Resolver(Source("a", Release("1.0.0", ("b", "*"))), Source("b", Release("1.0.0", ("a", "*"))));
        Assert.Throws<MarketplaceException>(() => resolver.Resolve(new("a")));
    }

    [Fact]
    public void OptionalDependenciesAreNotInstalledAndIncompatibleProvidersRemainUnavailable()
    {
        PluginRelease app = Release("1.0.0"); app.Optional["alerts"] = "^2.0.0";
        var resolver = Resolver(Source("app", app), Source("alerts", Release("1.0.0"), Release("2.0.0")));
        Assert.Single(resolver.Resolve(new("app")).Plugins);
        DependencyResolution graph = resolver.Resolve(new("app"), [new("alerts", "official", "1.0.0")]);
        Assert.Contains("app:alerts", graph.UnavailableOptionalDependencies);
        Assert.Equal("1.0.0", graph.Plugins.Single(plugin => plugin.Id == "alerts").Release.Version);
    }

    [Fact]
    public void CrossMarketplaceDependenciesRequireAnExistingBinding()
    {
        CatalogueSource other = Source("tools", Release("1.0.0")) with { MarketplaceId = "other" };
        var resolver = Resolver(Source("app", Release("1.0.0", ("tools", "*"))), other);
        Assert.Throws<MarketplaceException>(() => resolver.Resolve(new("app")));
        Assert.Equal("other", resolver.Resolve(new("app"), [new("tools", "other", "1.0.0")]).Plugins.First().MarketplaceId);
        var ambiguous = Resolver(Source("app", Release("1.0.0")), Source("app", Release("2.0.0")) with { MarketplaceId = "other" });
        Assert.Throws<MarketplaceException>(() => ambiguous.Resolve(new("app")));
    }

    [Fact]
    public void DefaultResolutionSkipsPrereleasesAndYankedCandidates()
    {
        PluginRelease yanked = Release("3.0.0"); yanked.Yanked = true;
        var resolver = Resolver(Source("app", yanked, Release("2.0.0-beta.1"), Release("1.0.0")));
        Assert.Equal("1.0.0", Assert.Single(resolver.Resolve(new("app")).Plugins).Release.Version);
        Assert.Equal("2.0.0-beta.1", Assert.Single(resolver.Resolve(new("app", IncludePrerelease: true)).Plugins).Release.Version);
    }

    [Fact]
    public void HostVersionsAndCapabilitiesRemainIndependent()
    {
        PluginRelease release = Release("1.0.0");
        release.Hosts["mcc"] = ">=3.0.0";
        release.Needs.Add("desktop-images");
        Assert.Contains(ReleaseCompatibility.Check(release, Host), failure => failure.Code == "compatibility.application");
        Assert.Contains(ReleaseCompatibility.Check(release, Host), failure => failure.Code == "compatibility.capability");
    }

    private static DependencyResolver Resolver(params CatalogueSource[] sources) => new(Host, sources);
    private static CatalogueSource Source(string id, params PluginRelease[] releases)
        => new("official", new() { SchemaVersion = 2, Id = id, Releases = [.. releases] });
    private static ReleaseAsset Asset(string kind, string target)
        => new() { Kind = kind, Target = target, Url = "https://example.org/" + target + ".zip", Sha256 = new('a', 64) };
    private static PluginRelease Release(string version, params (string Id, string Range)[] dependencies)
        => new()
        {
            Version = version,
            ApiVersion = "1.0",
            Dmcbk = ">=0.1.0-preview.1 <0.2.0",
            Umpk = ">=0.9.0-beta.4 <0.10.0",
            Framework = "net10.0",
            Assets = [Asset("compiled", "any")],
            Requires = dependencies.ToDictionary(pair => pair.Id, pair => pair.Range)
        };
}

using DMCBK.Marketplace;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ReleaseCatalogueBuilderTests
{
    [Fact]
    public void CombinesSourceAndCompiledAssetsWithoutMutatingInputs()
    {
        ReleaseCatalogue source = Catalogue("source", "any");
        ReleaseCatalogue binary = Catalogue("compiled", "win-x64");
        ReleaseCatalogue result = ReleaseCatalogueBuilder.Compose([source, binary, binary]);
        Assert.Equal(2, Assert.Single(result.Releases).Assets.Count);
        Assert.Single(Assert.Single(source.Releases).Assets);
        Assert.Single(Assert.Single(binary.Releases).Assets);
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("host")]
    [InlineData("capability")]
    [InlineData("engine")]
    [InlineData("hash")]
    public void RejectsConflictingFragments(string field)
    {
        ReleaseCatalogue first = Catalogue("source", "any");
        ReleaseCatalogue second = Catalogue("source", "any");
        Mutate(second.Releases[0], field);
        Assert.Throws<MarketplaceException>(() => ReleaseCatalogueBuilder.Compose([first, second]));
    }

    [Fact]
    public void PublishingPreservesHistoryAndPermitsYanking()
    {
        ReleaseCatalogue prior = Catalogue("source", "any");
        ReleaseCatalogue next = Catalogue("source", "any");
        next.Releases[0].Version = "2.0.0";
        ReleaseCatalogue history = ReleaseCatalogueBuilder.Publish(prior, next);
        Assert.Equal(["2.0.0", "1.0.0"], history.Releases.Select(release => release.Version));
        prior.Releases[0].Yanked = true;
        history = ReleaseCatalogueBuilder.Publish(history, prior);
        Assert.True(history.Releases.Single(release => release.Version == "1.0.0").Yanked);
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("host")]
    [InlineData("capability")]
    [InlineData("engine")]
    [InlineData("hash")]
    public void PublishedVersionsCannotChangeMetadataOrPayload(string field)
    {
        ReleaseCatalogue prior = Catalogue("source", "any");
        ReleaseCatalogue changed = Catalogue("source", "any");
        Mutate(changed.Releases[0], field);
        Assert.Equal("pack.immutable-release-conflict",
            Assert.Throws<MarketplaceException>(() => ReleaseCatalogueBuilder.Publish(prior, changed)).Code);
    }

    private static void Mutate(PluginRelease release, string field)
    {
        switch (field)
        {
            case "dependency": release.Requires["helper"] = "^2.0.0"; break;
            case "host": release.Hosts["mcc"] = "^2.0.0"; break;
            case "capability": release.Needs.Add("beacon"); break;
            case "engine": release.Umpk = ">=1.0.0"; break;
            case "hash": release.Assets[0].Sha256 = new string('b', 64); break;
        }
    }

    private static ReleaseCatalogue Catalogue(string kind, string target) => new()
    {
        SchemaVersion = 2,
        Id = "probe",
        Releases = [new() { Version = "1.0.0", ApiVersion = "1.0", Dmcbk = "*", Umpk = "*", Framework = "net10.0",
            Assets = [new() { Kind = kind, Target = target, Url = $"https://assets.test/{kind}-{target}.zip", Sha256 = new string('a', 64) }] }],
    };
}

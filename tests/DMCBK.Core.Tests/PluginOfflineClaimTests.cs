using DMCBK.Core.Configuration;
using DMCBK.Core.Plugins;
using DMCBK.Core.Tests.Fakes;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The manifest's <c>[services] offline</c> claim.
/// It gates nothing; the whole point is that a user can see it, so what has to hold is that it survives from the file to the listing and to the idle banner.
/// </summary>
public sealed class PluginOfflineClaimTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ProbePlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "bridge";

            public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
        }
        """;

    [Fact]
    public async Task ADeclaredClaimReachesTheListing()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("bridge", "Bridge.cs", Source, extraKeys: "[services]\noffline = true\n");
        await fixture.Host.LoadAllAsync();

        PluginInfo info = fixture.Host.List().Single();

        Assert.True(info.Offline);
        Assert.True(info.Loaded);
    }

    [Fact]
    public async Task AnUndeclaredClaimIsFalse()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("bridge", "Bridge.cs", Source);
        await fixture.Host.LoadAllAsync();

        Assert.False(fixture.Host.List().Single().Offline);
    }

    /// <summary>
    /// A reload re-reads the manifest into the record it already has.
    /// Every other manifest table is copied there by hand, so a table nobody copied would silently read as its default after one reload.
    /// </summary>
    [Fact]
    public async Task TheClaimSurvivesAReload()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("bridge", "Bridge.cs", Source, extraKeys: "[services]\noffline = true\n");
        await fixture.Host.LoadAllAsync();

        Assert.True((await fixture.Host.ReloadAsync("bridge")).Success);

        Assert.True(fixture.Host.List().Single().Offline);
    }

}

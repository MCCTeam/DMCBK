using DMCBK.Core.Plugins;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>plugins doctor</c>.
/// Most of what it says was already somewhere in the log; the point of the verb is that it is one pass, answerable at the prompt, over folders that are not plugins, plugins the loader refused, and the exception counters.
/// </summary>
public sealed class PluginDoctorTests
{

    [Fact]
    public void SkipsTheInstallationStoreDirectories()
    {
        using var fixture = new PluginRootFixture();
        foreach (string name in new[] { "versions", "userdata", "cache", "transactions" })
            Directory.CreateDirectory(Path.Combine(fixture.Root, name));
        Assert.Empty(fixture.Host.Doctor().Notes);
    }

    private const string Source = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ProbePlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "probe";

            public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
        }
        """;

    [Fact]
    public void NamesAFolderWithNoManifest()
    {
        using var fixture = new PluginRootFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bStats"));

        PluginDoctorReport report = fixture.Host.Doctor();

        PluginDoctorNote note = Assert.Single(report.Notes);
        Assert.Equal("bStats", note.Id);
        Assert.False(note.Error);
        Assert.Contains("plugin.toml", note.Detail);
    }

    [Fact]
    public void SkipsTheHostsOwnDotFolders()
    {
        using var fixture = new PluginRootFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".staging"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".marketplaces"));

        Assert.Empty(fixture.Host.Doctor().Notes);
    }

    [Fact]
    public async Task CarriesTheHostsOwnRefusalTextForAVersionGate()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("probe", "Probe.cs", Source, extraKeys: "[hosts]\ndmcbk = \">=99.0.0\"\n");
        await fixture.Host.LoadAllAsync();

        PluginDoctorReport report = fixture.Host.Doctor();

        PluginDoctorNote note = Assert.Single(report.Notes);
        Assert.Equal("probe", note.Id);
        Assert.True(note.Error);
        Assert.Contains(">=99.0.0", note.Detail);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task ReportsAVersionGateEvenOnAPluginNobodyTriedToLoad()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("probe", "Probe.cs", Source, enabled: false, apiVersion: "99.0");
        await fixture.Host.LoadAllAsync();

        PluginDoctorNote note = Assert.Single(fixture.Host.Doctor().Notes);

        Assert.True(note.Error);
        Assert.Contains("99.0", note.Detail);
    }

    [Fact]
    public async Task ReportsAMissingRequiredPlugin()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("probe", "Probe.cs", Source, extraKeys: "[requires]\nabsent = \"*\"\n");
        await fixture.Host.LoadAllAsync();

        PluginDoctorReport report = fixture.Host.Doctor();

        Assert.Contains(report.Notes, n => n.Id == "probe" && n.Error && n.Detail.Contains("absent"));
    }

    [Fact]
    public async Task CountsTheDiscoveredAndTheLoaded()
    {
        using var fixture = new PluginRootFixture();
        fixture.WritePlugin("probe", "Probe.cs", Source);
        fixture.WritePlugin("off", "Off.cs", Source.Replace("\"probe\"", "\"off\""), enabled: false);
        await fixture.Host.LoadAllAsync();

        PluginDoctorReport report = fixture.Host.Doctor();

        Assert.Equal(2, report.Discovered);
        Assert.Equal(1, report.Loaded);
    }

    [Fact]
    public async Task ReportsLeftoverCompileOutput()
    {
        using var fixture = new PluginRootFixture();
        string folder = fixture.WritePlugin("probe", "Probe.cs", Source);
        await fixture.Host.LoadAllAsync();

        string cache = Path.Combine(fixture.Root, "cache", "source", "probe");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "Probe.deadbeef.dll"), "old");

        PluginDoctorReport report = fixture.Host.Doctor();

        Assert.Contains(report.Notes, n => n.Id == "probe" && !n.Error && n.Detail.Contains("source"));
    }
}

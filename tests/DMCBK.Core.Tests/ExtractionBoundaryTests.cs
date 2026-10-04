using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ExtractionBoundaryTests
{
    [Fact]
    public async Task CoreOnlyHasNoOptionalModules()
    {
        await using Client client = new ClientBuilder().UseUsername("coreonly").Build();
        Assert.False(client.TryGetModule<ICommandDispatcher>(out _));
        Assert.Throws<NotSupportedException>(() => client.Commands);
        Assert.DoesNotContain(typeof(Client).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name is "DMCBK.Commands" or "DMCBK.Beacon" or "Microsoft.CodeAnalysis.CSharp" or "Samboy063.Tomlet");
        Assert.DoesNotContain(typeof(IPlugin).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name is "DMCBK.Plugins" or "DMCBK.Beacon" or "Microsoft.CodeAnalysis.CSharp");
    }

    [Fact]
    public async Task ReusingBuilderCreatesIndependentModules()
    {
        var builder = new ClientBuilder().UseUsername("isolated").UseCommands().UseBeacon();
        await using Client first = builder.Build();
        await using Client second = builder.Build();
        Assert.NotSame(first.Commands, second.Commands);
        Assert.NotSame(first.Scripts, second.Scripts);
        Assert.NotSame(first.Variables, second.Variables);
        Assert.Contains("commands", first.AvailableCapabilities);
        Assert.Contains("beacon", first.AvailableCapabilities);
        var result = await first.Scripts.RunAsync("test", "# beacon 1\nshow 42\n");
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("42", Assert.Single(result.LocalOutput));
        Assert.Empty(second.Scripts.Discover());
    }

    [Theory]
    [InlineData(">=0.1.0-preview.1 <0.2.0", "0.1.0-preview.1", true)]
    [InlineData(">=0.1.0-preview.2 <0.2.0", "0.1.0-preview.1", false)]
    [InlineData("=2.0.0", "2.0.0-beta.1", false)]
    [InlineData("^2.1.0", "2.2.0-beta.1", false)]
    [InlineData("^2.1.0", "2.2.0", true)]
    [InlineData(">=0.9.0-beta.4 <0.10.0", "0.9.0-beta.4", true)]
    public void PrereleaseRangesRespectFullVersionBounds(string range, string version, bool expected)
        => Assert.Equal(expected, SemVerRange.Parse(range).Satisfies(SemVer.Parse(version)));

    [Theory]
    [InlineData("win", "x86", false, "win-x86")]
    [InlineData("linux", "arm", true, "linux-musl-arm")]
    [InlineData("linux", "x64", false, "linux-x64")]
    [InlineData("osx", "arm64", false, "osx-arm64")]
    public void ProcessTargetSelection(string os, string architecture, bool musl, string expected)
        => Assert.Equal(expected, PlatformTarget.Resolve(os, architecture, musl));

    [Theory]
    [InlineData("linux", "x86")]
    [InlineData("osx", "x86")]
    public void UnsupportedProcessTargetsAreRejected(string os, string architecture)
        => Assert.Throws<PlatformNotSupportedException>(() => PlatformTarget.Resolve(os, architecture));

    [Fact]
    public void ManifestRequiresSchemaAndSafeEntry()
    {
        const string manifest = "schema-version = 2\nid = \"example\"\nversion = \"2.0.0\"\nkind = \"source\"\ntarget = \"any\"\nframework = \"net10.0\"\napi-version = \"1.0\"\ndmcbk = \"*\"\numpk = \"*\"\nentry = \"Example.cs\"\n";
        Assert.True(PluginManifest.TryParse(manifest, out _, out string? error), error);
        Assert.False(PluginManifest.TryParse(manifest.Replace("schema-version = 2", "schema-version = 1"), out _, out _));
        Assert.False(PluginManifest.TryParse(manifest.Replace("Example.cs", "../Example.cs"), out _, out _));
        Assert.False(PluginManifest.TryParse(manifest.Replace("kind = \"source\"", "kind = \"compiled\""), out _, out _));
    }
}

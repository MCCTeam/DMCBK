using DMCBK.Core.Plugins;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>plugins new &lt;id&gt;</c>.
/// The scaffold is the shape every convention in the SDK is written down in, so the load-bearing test is that what it writes passes the validator and then compiles and loads.
/// </summary>
public sealed class PluginScaffoldTests
{
    [Fact]
    public void WritesManifestEntryLanguageAndManual()
    {
        using var fixture = new PluginRootFixture();

        Assert.True(PluginScaffold.TryCreate(fixture.Root, "demo-x", out PluginScaffoldResult result, out string? error));
        Assert.Null(error);
        Assert.Equal("demo-x", result.Id);
        Assert.Equal(Path.Combine(fixture.Root, "DemoX"), result.Folder);
        Assert.Equal(
            [
                "plugin.toml",
                "DemoX.cs",
                Path.Combine("lang", "en.toml"),
                Path.Combine("man", "en", "demo-x.md"),
            ],
            result.Files);

        foreach (string file in result.Files)
            Assert.True(File.Exists(Path.Combine(result.Folder, file)), file);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Demo")]
    [InlineData("9lives")]
    [InlineData("demo_x")]
    [InlineData("demo-")]
    public void RefusesAnIdThatIsNotAPluginId(string id)
    {
        using var fixture = new PluginRootFixture();

        Assert.False(PluginScaffold.TryCreate(fixture.Root, id, out _, out string? error));
        Assert.Contains("lower-case", error);
    }

    [Fact]
    public void RefusesToOverwriteAnExistingFolder()
    {
        using var fixture = new PluginRootFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "DemoX"));

        Assert.False(PluginScaffold.TryCreate(fixture.Root, "demo-x", out _, out string? error));
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void WhatItWritesValidatesClean()
    {
        using var fixture = new PluginRootFixture();
        Assert.True(PluginScaffold.TryCreate(fixture.Root, "demo-x", out PluginScaffoldResult result, out _));

        PluginValidationReport report = PluginValidator.Validate(result.Folder);

        Assert.Empty(report.Problems);
        Assert.True(report.IsValid);
    }

    [Fact]
    public void PutsMessageKeysAboveTheSettingsTable()
    {
        using var fixture = new PluginRootFixture();
        Assert.True(PluginScaffold.TryCreate(fixture.Root, "demo-x", out PluginScaffoldResult result, out _));

        string english = File.ReadAllText(Path.Combine(result.Folder, "lang", "en.toml"));

        Assert.True(english.IndexOf("started =", StringComparison.Ordinal) > 0);
        Assert.True(
            english.IndexOf("started =", StringComparison.Ordinal)
            < english.IndexOf("[settings]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScaffoldsDisabledAndTheSourceCompilesWhenEnabled()
    {
        using var fixture = new PluginRootFixture();

        PluginActionResult scaffolded = fixture.Host.Scaffold("demo-x");
        Assert.True(scaffolded.Success);

        PluginInfo listed = Assert.Single(fixture.Host.List());
        Assert.Equal("demo-x", listed.Id);
        Assert.False(listed.Enabled);
        Assert.False(listed.Loaded);

        PluginActionResult enabled = await fixture.Host.EnableAsync("demo-x");

        Assert.True(enabled.Success, enabled.Message);
        Assert.True(fixture.Host.List().Single().Loaded);
    }
}

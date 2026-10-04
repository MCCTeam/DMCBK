using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Beacon;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ClientIsolationTests
{
    [Fact]
    public void PluginProvidersAndHooksBelongToEachEngine()
    {
        var (first, _, _) = ScriptTestHelpers.NewEngine();
        var (second, _, _) = ScriptTestHelpers.NewEngine();
        using IDisposable firstEvent = first.Bridge.RegisterEvent("first-provider", "custom_sale", [], "sale");
        using IDisposable secondEvent = second.Bridge.RegisterEvent("second-provider", "custom_sale", [], "sale");
        using (first.Environment.Enter())
        {
            Assert.True(BeaconProviders.TryGetEvent("custom_sale", out string owner));
            Assert.Equal("first-provider", owner);
            first.Bridge.WithdrawPlugin("first-provider");
            Assert.False(BeaconHookCatalog.IsKnown("custom_sale"));
        }
        using (second.Environment.Enter())
        {
            Assert.True(BeaconProviders.TryGetEvent("custom_sale", out string owner));
            Assert.Equal("second-provider", owner);
            Assert.True(BeaconHookCatalog.IsKnown("custom_sale"));
        }
        Assert.False(BeaconHookCatalog.IsKnown("custom_sale"));
    }

    [Fact]
    public async Task EnvironmentScopesFlowAcrossAwaitAndRestoreTheParent()
    {
        var environment = new BeaconEnvironment();
        using (environment.Enter())
        {
            await Task.Yield();
            BeaconHookCatalog.RegisterCustomHook("isolated_event");
            Assert.True(BeaconHookCatalog.IsKnown("isolated_event"));
        }
        Assert.False(BeaconHookCatalog.IsKnown("isolated_event"));
        using (environment.Enter()) Assert.True(BeaconHookCatalog.IsKnown("isolated_event"));
    }

    [Fact]
    public async Task ReusingABuilderDoesNotShareFeatureOptionsAndDisposalRunsOnce()
    {
        var builder = new ClientBuilder().UseUsername("tester").UseCommands();
        Client first = builder.Build(); await using Client second = builder.Build();
        Assert.NotSame(first.Features, second.Features);
        first.Features.Inventory = false;
        Assert.True(second.Features.Inventory);
        int shutdowns = 0; first.BeforeExit += (_, _) => shutdowns++;
        await Task.WhenAll(first.DisposeAsync().AsTask(), first.DisposeAsync().AsTask());
        Assert.Equal(1, shutdowns);
        Assert.Empty(first.AvailableCapabilities);
    }

    [Fact]
    public async Task ClientCultureDoesNotChangeProcessDefaultsOrLeakAcrossAsyncScopes()
    {
        var defaultCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        var defaultUi = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;
        var ambient = System.Globalization.CultureInfo.CurrentCulture;
        await using Client first = new ClientBuilder().UseUsername("first").UseConfiguration(
            new Configuration.DmcbkConfiguration { Localization = new Configuration.LocalizationConfig { Language = "de-DE" } }).Build();
        await using Client second = new ClientBuilder().UseUsername("second").UseConfiguration(
            new Configuration.DmcbkConfiguration { Localization = new Configuration.LocalizationConfig { Language = "en-US" } }).Build();
        Assert.Equal("de-DE", first.UiCulture.Name);
        Assert.Equal("en-US", second.UiCulture.Name);
        Assert.Same(defaultCulture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture);
        Assert.Same(defaultUi, System.Globalization.CultureInfo.DefaultThreadCurrentUICulture);
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;
        async Task<string> Format(Client client)
        {
            using (Localization.UiCulture.Enter(client.UiCulture))
            {
                if (Interlocked.Increment(ref arrived) == 2) rendezvous.SetResult();
                await rendezvous.Task;
                Assert.Equal(client.UiCulture, Localization.McStrings.Culture);
                return 1.5.ToString("F1");
            }
        }
        string[] results = await Task.WhenAll(Format(first), Format(second));
        Assert.Equal(new[] { "1,5", "1.5" }, results);
        Assert.Equal(ambient, System.Globalization.CultureInfo.CurrentCulture);
    }

    [Fact]
    public async Task PluginManualPagesAndCacheAreIndependentPerClient()
    {
        await using Client first = new ClientBuilder().UseUsername("first").Build();
        await using Client second = new ClientBuilder().UseUsername("second").Build();
        string root = Path.Combine(Path.GetTempPath(), "dmcbk-manual-" + Guid.NewGuid().ToString("N"));
        try
        {
            string one = Path.Combine(root, "one", "en"), two = Path.Combine(root, "two", "en");
            Directory.CreateDirectory(one); Directory.CreateDirectory(two);
            File.WriteAllText(Path.Combine(one, "shared-topic.md"), "# First client");
            File.WriteAllText(Path.Combine(two, "shared-topic.md"), "# Second client");
            Manual.ManualTopic[] topics = [new("shared-topic", Manual.ManualGroup.Plugins, "A plugin page")];
            IDisposable registration = first.Manuals.Register(new Manual.DirectoryManualSource(Path.GetDirectoryName(one)!, topics));
            using IDisposable other = second.Manuals.Register(new Manual.DirectoryManualSource(Path.GetDirectoryName(two)!, topics));
            Assert.Equal("# First client", first.Manuals.Read("shared-topic"));
            Assert.Equal("# Second client", second.Manuals.Read("shared-topic"));
            registration.Dispose();
            Assert.Null(first.Manuals.Read("shared-topic"));
            Assert.Equal("# Second client", second.Manuals.Read("shared-topic"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ImageRequestsCopyPixelsAndValidateDimensions()
    {
        byte[] source = [10, 20, 30]; var frame = new RgbImageFrame(1, 1, source); source[0] = 99;
        Assert.Equal(new RgbPixel(10, 20, 30), frame.GetPixel(0, 0));
        Assert.Throws<ArgumentException>(() => new RgbImageFrame(2, 1, source));
    }
}

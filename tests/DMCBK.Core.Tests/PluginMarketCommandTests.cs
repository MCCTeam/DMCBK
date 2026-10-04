using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using DMCBK.PluginSdk;
using DMCBK.Marketplace;
using System.Net;
using Tomlet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>plugins</c> command's marketplace grammar, dispatched through the real command service rather than called on the market directly.
/// What these check is that the words a user types reach the seam, and that the answers coming back are the ones the seam produced.
/// </summary>
public sealed class PluginMarketCommandTests
{
    private const string PluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class CommandedPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "widget";

            public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
        }
        """;

    [Fact]
    public async Task Install_FromAFolderWithYes_NeedsNoPrompt()
    {
        using var fixture = new CommandFixture();
        string source = fixture.WriteSourcePlugin("widget", "1.0.0");

        CmdResult result = await fixture.Client.Commands.DispatchAsync($"plugins install {source} yes");

        Assert.Equal(CmdStatus.Done, result.Message is null ? CmdStatus.Fail : result.Status);
        Assert.Contains("Installed plugin 'widget' version 1.0.0", result.Message);
        Assert.Equal("1.0.0", Assert.Single(fixture.ReadLock().Plugins).Release.Version);
    }

    [Fact]
    public async Task Install_WithAVersionAndYes_IsStillReadAsAVersion()
    {
        using var fixture = new CommandFixture();
        string source = fixture.WriteSourcePlugin("widget", "1.0.0");

        CmdResult result = await fixture.Client.Commands.DispatchAsync($"plugins install {source} ^1 yes");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("1.0.0", result.Message);
    }

    [Fact]
    public async Task Install_OfAnIdSaysNoMarketplacesAreAdded()
    {
        using var fixture = new CommandFixture();

        CmdResult bare = await fixture.Client.Commands.DispatchAsync("plugins install auto-eat");
        CmdResult qualified = await fixture.Client.Commands.DispatchAsync("plugins install auto-eat@official");

        Assert.Equal(CmdStatus.Fail, bare.Status);
        Assert.Contains("auto-eat", bare.Message);
        Assert.Contains("auto-eat", qualified.Message);
        Assert.Empty(fixture.ReadLock().Plugins);
    }

    [Fact]
    public async Task WithNoMarketAttached_TheVerbsSaySo()
    {
        using var fixture = new CommandFixture(attachMarket: false);

        foreach (string line in new[]
        {
            "plugins install ./somewhere",
            "plugins uninstall widget",
            "plugins update all",
            "plugins outdated",
            "plugins pin widget",
            "plugins unpin widget",
            "plugins info widget",
            "plugins list local",
        })
        {
            CmdResult result = await fixture.Client.Commands.DispatchAsync(line);
            Assert.Equal(CmdStatus.Fail, result.Status);
            Assert.Contains("no plugin market", result.Message);
        }

        // The loader verbs keep working without one: loading from disk is not a marketplace power.
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("plugins list")).Status);
    }

    [Fact]
    public async Task Outdated_AndUpdate_WalkTheWholeCycle()
    {
        using var fixture = new CommandFixture();
        string market = fixture.WriteMarketplace("updates", "widget", "1.0.0");
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync($"plugins market add {market}")).Status);
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("plugins install widget yes")).Status);
        Assert.Contains("up to date", (await fixture.Client.Commands.DispatchAsync("plugins outdated")).Message);
        fixture.WriteMarketplace("updates", "widget", "1.1.0");
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("plugins market refresh updates")).Status);
        Assert.Contains("widget 1.0.0 -> 1.1.0", (await fixture.Client.Commands.DispatchAsync("plugins outdated")).Message);
        Assert.Contains("widget", (await fixture.Client.Commands.DispatchAsync("plugins list outdated")).Message);
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("plugins update widget yes")).Status);
        Assert.Equal("1.1.0", Assert.Single(fixture.ReadLock().Plugins).Release.Version);
    }

    [Fact]
    public async Task IncompatibleReleaseIsExplainedBeforeInstallation()
    {
        using var fixture = new CommandFixture();
        string market = fixture.WriteMarketplace("incompatible", "widget", "2.0.0", "[hosts]\nmcc = \">=99.0.0\"\n");
        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync($"plugins market add {market}")).Status);
        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins search widget");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("99.0.0", result.Message);
        Assert.Equal(CmdStatus.Fail, (await fixture.Client.Commands.DispatchAsync("plugins install widget yes")).Status);
        Assert.Empty(fixture.ReadLock().Plugins);
    }

    [Fact]
    public async Task Info_ShowsWhereItCameFrom_AndDepsShowsBothDirections()
    {
        using var fixture = new CommandFixture();
        string source = fixture.WriteSourcePlugin("widget", "1.0.0");
        await fixture.Client.Commands.DispatchAsync($"plugins install {source} yes");

        CmdResult info = await fixture.Client.Commands.DispatchAsync("plugins info widget");
        Assert.Equal(CmdStatus.Done, info.Status);
        Assert.Contains("widget 1.0.0", info.Message);
        Assert.Contains("source", info.Message);
        Assert.Contains(Path.GetFullPath(source), info.Message);

        CmdResult deps = await fixture.Client.Commands.DispatchAsync("plugins deps widget");
        Assert.Equal(CmdStatus.Done, deps.Status);
        Assert.Contains("widget dependencies:", deps.Message);
        Assert.Contains("nothing", deps.Message);

        CmdResult missing = await fixture.Client.Commands.DispatchAsync("plugins deps nothing-here");
        Assert.Equal(CmdStatus.Fail, missing.Status);
    }

    [Fact]
    public async Task Pin_Unpin_AndUninstall_RunFromTheCommandLine()
    {
        using var fixture = new CommandFixture();
        string source = fixture.WriteSourcePlugin("widget", "1.0.0");
        await fixture.Client.Commands.DispatchAsync($"plugins install {source} yes");

        Assert.Contains("Pinned", (await fixture.Client.Commands.DispatchAsync("plugins pin widget")).Message);
        Assert.Contains(
            "Pinned", (await fixture.Client.Commands.DispatchAsync("plugins pin widget 1.0.0")).Message);
        Assert.Contains("Unpinned", (await fixture.Client.Commands.DispatchAsync("plugins unpin widget")).Message);

        CmdResult uninstall = await fixture.Client.Commands.DispatchAsync("plugins uninstall widget purge");
        Assert.Equal(CmdStatus.Done, uninstall.Status);
        Assert.Empty(fixture.ReadLock().Plugins);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "userdata", "widget")));
    }

    [Fact]
    public async Task List_FiltersByLocalEnabledAndDisabled()
    {
        using var fixture = new CommandFixture();
        await fixture.InstallByHand("handmade", enabled: true);
        await fixture.InstallByHand("sleeping", enabled: false);
        await fixture.Host.LoadAllAsync();

        CmdResult local = await fixture.Client.Commands.DispatchAsync("plugins list local");
        Assert.Contains("handmade", local.Message);

        CmdResult enabled = await fixture.Client.Commands.DispatchAsync("plugins list enabled");
        Assert.Contains("handmade", enabled.Message);
        Assert.DoesNotContain("sleeping", enabled.Message);

        CmdResult disabled = await fixture.Client.Commands.DispatchAsync("plugins list disabled");
        Assert.Contains("sleeping", disabled.Message);
        Assert.DoesNotContain("handmade", disabled.Message);
    }

    /// <summary>
    /// <c>install</c>'s argument used to be a quotable string, whose unquoted set is <c>[0-9A-Za-z_.+-]</c>, so every reference form except a bare id died on its first slash, colon or caret.
    /// These are the shapes that must survive the split.
    /// </summary>
    [Theory]
    [InlineData("./Downloads/my-plugin", "./Downloads/my-plugin", null, false)]
    [InlineData("/tmp/mcc/widget yes", "/tmp/mcc/widget", null, true)]
    [InlineData("someone/mcc-xray@v0.3", "someone/mcc-xray@v0.3", null, false)]
    [InlineData("https://codeberg.org/a/b.git#v1 yes", "https://codeberg.org/a/b.git#v1", null, true)]
    [InlineData("https://host/a.tar.gz ^1.2 yes", "https://host/a.tar.gz", "^1.2", true)]
    [InlineData("auto-eat ^1", "auto-eat", "^1", false)]
    [InlineData("\"/tmp/my plugin\" yes", "/tmp/my plugin", null, true)]
    public void InstallArguments_ReadEveryReferenceShape(
        string arguments, string expectedWhat, string? expectedVersion, bool expectedYes)
    {
        Assert.True(PluginsCommand.TryReadInstallArguments(
            arguments, out string what, out string? version, out bool assumeYes));
        Assert.Equal(expectedWhat, what);
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedYes, assumeYes);
    }

    [Theory]
    [InlineData("a b c d")]
    [InlineData("a b c")]
    [InlineData("\"unbalanced")]
    public void InstallArguments_RefuseATailThatIsNotAVersionAndAYes(string arguments)
        => Assert.False(PluginsCommand.TryReadInstallArguments(arguments, out _, out _, out _));

    [Fact]
    public async Task Install_FromAQuotedFolderWithASpace_Works()
    {
        using var fixture = new CommandFixture();
        string source = fixture.WriteSourcePlugin("widget", "1.0.0", folderName: "my plugin");

        CmdResult result = await fixture.Client.Commands.DispatchAsync($"plugins install \"{source}\" yes");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("Installed plugin 'widget' version 1.0.0", result.Message);
    }

    [Fact]
    public async Task Install_WithAnUnreadableTail_SaysHowTheVerbIsSpelled()
    {
        using var fixture = new CommandFixture();

        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins install a b c d");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("plugins install <what>", result.Message);
    }

    [Theory]
    [InlineData("./downloads/market", "./downloads/market", null)]
    [InlineData("MCCTeam/mcc-plugins", "MCCTeam/mcc-plugins", null)]
    [InlineData("MCCTeam/mcc-plugins as official", "MCCTeam/mcc-plugins", "official")]
    [InlineData("https://x.test/mcc-marketplace.toml as acme", "https://x.test/mcc-marketplace.toml", "acme")]
    [InlineData("\"/tmp/a folder/market\" as spaced", "/tmp/a folder/market", "spaced")]
    public void MarketplaceAdd_SplitsTheSourceFromTheName(string arguments, string source, string? name)
    {
        Assert.True(PluginsCommand.TryReadAddArguments(arguments, out string what, out string? given));
        Assert.Equal(source, what);
        Assert.Equal(name, given);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner/repo as")]
    [InlineData("owner/repo named thing")]
    [InlineData("owner/repo as one two")]
    public void MarketplaceAdd_RefusesAnythingElse(string arguments)
        => Assert.False(PluginsCommand.TryReadAddArguments(arguments, out _, out _));

    [Theory]
    [InlineData("discord", "discord", null)]
    [InlineData("discord in official", "discord", "official")]
    [InlineData("chat relay in official", "chat relay", "official")]
    [InlineData("in", "in", null)]
    public void Search_SplitsTheTextFromTheMarketplace(string arguments, string text, string? marketplace)
    {
        Assert.True(PluginsCommand.TryReadSearchArguments(arguments, out string what, out string? where));
        Assert.Equal(text, what);
        Assert.Equal(marketplace, where);
    }

    [Fact]
    public async Task Marketplace_ListIsEmptyUntilOneIsAdded_AndMarketIsTheSameNode()
    {
        using var fixture = new CommandFixture();

        CmdResult empty = await fixture.Client.Commands.DispatchAsync("plugins marketplace list");
        Assert.Equal(CmdStatus.Done, empty.Status);
        Assert.Contains("No marketplaces are added", empty.Message);

        string market = fixture.WriteMarketplace("test-market", "widget", "1.0.0");
        CmdResult added = await fixture.Client.Commands.DispatchAsync($"plugins marketplace add {market}");
        Assert.Equal(CmdStatus.Done, added.Status);
        Assert.Contains("test-market", added.Message);

        // The alias reaches the same node, not a shorter grammar.
        CmdResult listed = await fixture.Client.Commands.DispatchAsync("plugins market list");
        Assert.Equal(CmdStatus.Done, listed.Status);
        Assert.Contains("test-market", listed.Message);
        Assert.Contains("1 plugin(s)", listed.Message);
        Assert.Contains("auto-update off", listed.Message);
    }

    [Fact]
    public async Task Marketplace_AddWithAName_ThenPolicyRefreshAndRemove()
    {
        using var fixture = new CommandFixture();
        string market = fixture.WriteMarketplace("mine", "widget", "1.0.0");

        Assert.Equal(
            CmdStatus.Done,
            (await fixture.Client.Commands.DispatchAsync($"plugins market add {market} as mine")).Status);

        CmdResult policy = await fixture.Client.Commands.DispatchAsync("plugins marketplace auto-update mine check");
        Assert.Equal(CmdStatus.Done, policy.Status);
        Assert.Contains("'check'", policy.Message);

        CmdResult refreshed = await fixture.Client.Commands.DispatchAsync("plugins marketplace refresh mine");
        Assert.Equal(CmdStatus.Done, refreshed.Status);
        Assert.Contains("Refreshed", refreshed.Message);

        CmdResult removed = await fixture.Client.Commands.DispatchAsync("plugins marketplace remove mine");
        Assert.Equal(CmdStatus.Done, removed.Status);
        Assert.Contains("Removed marketplace 'mine'", removed.Message);
        Assert.Contains(
            "No marketplaces are added",
            (await fixture.Client.Commands.DispatchAsync("plugins marketplace list")).Message);
    }

    [Fact]
    public async Task Search_ReachesTheCatalogueAndNarrowsToOneMarketplace()
    {
        using var fixture = new CommandFixture();
        await fixture.Client.Commands.DispatchAsync(
            $"plugins marketplace add {fixture.WriteMarketplace("first", "widget", "1.0.0")}");
        await fixture.Client.Commands.DispatchAsync(
            $"plugins marketplace add {fixture.WriteMarketplace("second", "gadget", "2.0.0")}");

        CmdResult all = await fixture.Client.Commands.DispatchAsync("plugins search get");
        Assert.Equal(CmdStatus.Done, all.Status);
        Assert.Contains("widget@first", all.Message);
        Assert.Contains("gadget@second", all.Message);

        CmdResult narrowed = await fixture.Client.Commands.DispatchAsync("plugins search get in second");
        Assert.Contains("gadget@second", narrowed.Message);
        Assert.DoesNotContain("widget@first", narrowed.Message);

        CmdResult nothing = await fixture.Client.Commands.DispatchAsync("plugins search nothing-like-this");
        Assert.Equal(CmdStatus.Done, nothing.Status);
        Assert.Contains("nothing-like-this", nothing.Message);
    }

    [Fact]
    public async Task Install_OfAnIdFromAnAddedMarketplace_Installs()
    {
        using var fixture = new CommandFixture();
        await fixture.Client.Commands.DispatchAsync(
            $"plugins marketplace add {fixture.WriteMarketplace("test-market", "widget", "1.0.0")}");

        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins install widget yes");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("Installed plugin 'widget' version 1.0.0", result.Message);
        Assert.Equal("test-market", Assert.Single(fixture.ReadLock().Plugins).MarketplaceId);
    }

    [Fact]
    public async Task Marketplace_AddWithAnUnreadableTail_SaysHowTheVerbIsSpelled()
    {
        using var fixture = new CommandFixture();

        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins marketplace add owner/repo named x");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("plugins marketplace add <source> [as <name>]", result.Message);
    }

    /// <summary>A client with a plugin host, a market, and a temp plugins root, driven through commands.</summary>
    private sealed class CommandFixture : IDisposable
    {
        private readonly string _sources;
        private readonly HttpClient _http;
        private readonly AssetHandler _assets = new();
        public CommandFixture(bool attachMarket = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "dmcbk-plugin-command-" + Guid.NewGuid().ToString("N"));
            _sources = Path.Combine(Root, "sources");
            Directory.CreateDirectory(_sources);
            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester")
                .UseApplication(new HostApplication("mcc", "2.0.0", new HashSet<string>())).Build();
            Host = new PluginHost(Client, Root, NullLoggerFactory.Instance, Client.Translations, Client.Variables);
            _http = new HttpClient(_assets);
            if (attachMarket)
                Market = new MarketplaceService(Client, Root, Path.Combine(Root, "marketplaces.toml"), _http, Host);
        }
        public string Root { get; }
        public Client Client { get; }
        public PluginHost Host { get; }
        public MarketplaceService? Market { get; }
        public InstallationLock ReadLock() => File.Exists(Path.Combine(Root, "plugins.lock.toml"))
            ? InstallationLock.Parse(File.ReadAllText(Path.Combine(Root, "plugins.lock.toml"))) : new();

        public string WriteMarketplace(string name, string id, string version, string extra = "")
        {
            string folder = Path.Combine(_sources, "market-" + name);
            Directory.CreateDirectory(folder);
            string source = WriteSourcePlugin(id, version, extra, name + "-" + id + "-" + version);
            PackedPlugin packed = PluginPackageBuilder.Pack(source, source, Path.Combine(folder, "assets"), "source", "any", new Uri("https://assets.test/"));
            _assets.Files[packed.Release.Assets[0].Url] = packed.ArchivePath;
            string cataloguePath = Path.Combine(folder, id + ".toml");
            ReleaseCatalogue catalogue = File.Exists(cataloguePath) ? ReleaseCatalogue.Parse(File.ReadAllText(cataloguePath)) : new() { SchemaVersion = 2, Id = id };
            catalogue.Releases.Add(packed.Release);
            File.WriteAllText(cataloguePath, TomletMain.TomlStringFrom(catalogue));
            File.WriteAllText(Path.Combine(folder, "mcc-marketplace.toml"), TomletMain.TomlStringFrom(new MarketplaceIndex
            {
                SchemaVersion = 2,
                Id = name,
                Name = name,
                Plugins = [new() { Id = id, Description = "A widget.", Releases = id + ".toml" }],
            }));
            return folder;
        }

        public string WriteSourcePlugin(string id, string version, string extra = "", string? folderName = null)
        {
            string folder = Path.Combine(_sources, folderName ?? id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Entry.cs"), PluginSource.Replace("\"widget\"", "\"" + id + "\""));
            File.WriteAllText(Path.Combine(folder, "plugin.toml"),
                $"schema-version = 2\nid = \"{id}\"\nversion = \"{version}\"\nkind = \"source\"\ntarget = \"any\"\nentry = \"Entry.cs\"\nframework = \"net10.0\"\napi-version = \"1.0\"\ndmcbk = \"*\"\numpk = \"*\"\n{extra}");
            return folder;
        }

        public async Task InstallByHand(string id, bool enabled)
        {
            string source = WriteSourcePlugin(id, "1.0.0");
            var installed = await Market!.InstallAsync(source, assumeYes: true);
            Assert.True(installed.Success, installed.Message);
            if (!enabled) Assert.True((await Host.DisableAsync(id)).Success);
        }
        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult(); Market?.Dispose(); _http.Dispose();
            Directory.Delete(Root, true);
        }
        private sealed class AssetHandler : HttpMessageHandler
        {
            public Dictionary<string, string> Files { get; } = [];
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
                => Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsoluteUri, out string? path)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

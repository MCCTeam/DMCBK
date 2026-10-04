using DMCBK.Core;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Plugin-to-plugin dependencies: the <c>[requires]</c>, <c>[optional]</c> and <c>[exports]</c> manifest tables, the load order they imply, the four ways a dependency can refuse a load (missing, wrong version, not loaded, cycle), the unload and disable refusals with their chained command, and the reload cascade.
/// </summary>
public sealed class PluginDependencyTests
{
    /// <summary>
    /// Appends its id to the shared variable store on activation, so the order several plugins loaded in is observable without any of them knowing about the others.
    /// </summary>
    private const string OrderPluginTemplate = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class OrderPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "__ID__";

            public Task ActivateAsync(PluginContext context)
            {
                string order = context.Variables.Get("order") ?? "";
                context.Variables.Set("order", order + "__ID__;");
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public void Manifest_ParsesRequiresOptionalAndExports()
    {
        const string toml = """
            id = "dependent"
            version = "1.0.0"
            entry = "Dependent.cs"
            api-version = "1.0"

            [requires]
            map = "^1.2"

            [optional]
            chat-log = "*"

            [exports]
            assemblies = ["entry", "Extra.dll"]
            """;

        Assert.True(TestManifest.TryParse(toml, out PluginManifest manifest, out string? error), error);
        Assert.Equal("^1.2", manifest.Requires["map"]);
        Assert.Equal("*", manifest.Optional["chat-log"]);
        Assert.Equal(["entry", "Extra.dll"], manifest.Exports.Assemblies);

        Assert.True(manifest.RequiredRanges["map"].Satisfies(SemVer.Parse("1.3.0")));
        Assert.False(manifest.RequiredRanges["map"].Satisfies(SemVer.Parse("2.0.0")));
        Assert.True(manifest.OptionalRanges["chat-log"].IsAny);
    }

    [Fact]
    public void Manifest_WithUnparsableRequiresRange_Fails()
    {
        const string toml = """
            id = "dependent"
            entry = "Dependent.cs"

            [requires]
            map = "not a range"
            """;

        Assert.False(TestManifest.TryParse(toml, out _, out string? error));
        Assert.Contains("requires.map", error);
    }

    [Fact]
    public void Manifest_WithEmptyRequiresRange_Fails()
    {
        const string toml = """
            id = "dependent"
            entry = "Dependent.cs"

            [requires]
            map = ""
            """;

        Assert.False(TestManifest.TryParse(toml, out _, out string? error));
        Assert.Contains("requires.map", error);
    }

    [Fact]
    public void Graph_OrdersDependenciesFirst()
    {
        PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(
        [
            Manifest("a", requires: [("b", "*")]),
            Manifest("b", requires: [("c", "*")]),
            Manifest("c"),
        ]);

        Assert.Equal(["c", "b", "a"], graph.Order);
        Assert.Empty(graph.Cycles);
    }

    [Fact]
    public void Graph_OrdersOptionalDependenciesToo()
    {
        PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(
        [
            Manifest("a", optional: [("z", "*")]),
            Manifest("z"),
        ]);

        Assert.Equal(["z", "a"], graph.Order);
    }

    [Fact]
    public void Graph_NamesEveryMemberOfACycle()
    {
        PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(
        [
            Manifest("a", requires: [("b", "*")]),
            Manifest("b", requires: [("a", "*")]),
            Manifest("free"),
        ]);

        Assert.True(graph.Cycles.ContainsKey("a"));
        Assert.True(graph.Cycles.ContainsKey("b"));
        Assert.False(graph.Cycles.ContainsKey("free"));
        Assert.Equal(["a", "b"], graph.Cycles["a"]);
    }

    [Fact]
    public void Graph_DependentsAreOrderedDependentsFirst()
    {
        IReadOnlyList<string> dependents = PluginDependencyGraph.DependentsOf(
            "c",
            [
                Manifest("a", requires: [("b", "*")]),
                Manifest("b", requires: [("c", "*")]),
                Manifest("c"),
                Manifest("unrelated"),
            ]);

        Assert.Equal(["a", "b"], dependents);
    }

    [Fact]
    public async Task LoadAll_LoadsDependenciesFirst()
    {
        using var fixture = new HostFixture();
        // Written in the order that loads them backwards without a graph: discovery enumerates the directory, and "alpha" sorts before "omega".
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega");

        await fixture.Host.LoadAllAsync();

        Assert.Equal("omega;alpha;", fixture.Client.Variables.Get("order"));
    }

    [Fact]
    public async Task LoadAll_WithMissingRequiredPlugin_RefusesAndNamesIt()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "ghost = \"^1\"\n");

        await fixture.Host.LoadAllAsync();

        PluginInfo info = Assert.Single(fixture.Host.List());
        Assert.False(info.Loaded);
        Assert.Contains("ghost", info.Status);
        Assert.Contains("^1", info.Status);
    }

    [Fact]
    public async Task LoadAll_WithOutOfRangeDependency_RefusesAndNamesBothVersions()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \">=2.0.0\"\n");
        fixture.WriteOrderPlugin("omega", version: "1.4.0");

        await fixture.Host.LoadAllAsync();

        string status = fixture.Host.List().Single(p => p.Id == "alpha").Status!;
        Assert.Contains(">=2.0.0", status);
        Assert.Contains("1.4.0", status);
        Assert.Equal("omega;", fixture.Client.Variables.Get("order"));
    }

    [Fact]
    public async Task LoadAll_WithDisabledDependency_RefusesTheDependent()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega", enabled: false);

        await fixture.Host.LoadAllAsync();

        Assert.False(fixture.Host.List().Single(p => p.Id == "alpha").Loaded);
        Assert.Contains("plugins enable omega", fixture.Host.List().Single(p => p.Id == "alpha").Status);
    }

    [Fact]
    public async Task LoadAll_WithOptionalDependencyMissing_LoadsAnyway()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", optional: "ghost = \"^1\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.True(fixture.Host.List().Single(p => p.Id == "alpha").Loaded);
    }

    [Fact]
    public async Task LoadAll_WithACycle_RefusesEveryMember()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega", requires: "alpha = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        foreach (PluginInfo info in fixture.Host.List())
        {
            Assert.False(info.Loaded);
            Assert.Contains("cycle", info.Status);
        }

        Assert.Null(fixture.Client.Variables.Get("order"));
    }

    [Fact]
    public async Task Unload_WithLoadedDependent_IsRefusedWithTheChainedCommand()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega");
        await fixture.Host.LoadAllAsync();

        PluginActionResult result = await fixture.Host.UnloadAsync("omega");

        Assert.False(result.Success);
        Assert.Contains("plugins unload alpha; plugins unload omega", result.Message);
        Assert.True(fixture.Host.List().Single(p => p.Id == "omega").Loaded);
    }

    [Fact]
    public async Task Disable_WithLoadedDependent_IsRefusedWithTheChainedCommand()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega");
        await fixture.Host.LoadAllAsync();

        PluginActionResult result = await fixture.Host.DisableAsync("omega");

        Assert.False(result.Success);
        Assert.Contains("plugins disable alpha; plugins disable omega", result.Message);

        // The refusal must not have written enabled = false to the manifest either.
        Assert.Contains("enabled = true", File.ReadAllText(fixture.ManifestPath("omega")));
    }

    [Fact]
    public async Task Unload_OfTheDependentThenTheDependency_Succeeds()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega");
        await fixture.Host.LoadAllAsync();

        Assert.True((await fixture.Host.UnloadAsync("alpha")).Success);
        Assert.True((await fixture.Host.UnloadAsync("omega")).Success);
    }

    [Fact]
    public async Task Reload_CascadesToLoadedDependents()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("alpha", requires: "omega = \"*\"\n");
        fixture.WriteOrderPlugin("omega");
        await fixture.Host.LoadAllAsync();
        fixture.Client.Variables.Set("order", string.Empty);

        PluginActionResult result = await fixture.Host.ReloadAsync("omega");

        Assert.True(result.Success);
        Assert.Contains("alpha", result.Message);

        // omega comes back first, then its dependent: the cascade walks the unload order backwards.
        Assert.Equal("omega;alpha;", fixture.Client.Variables.Get("order"));
        Assert.All(fixture.Host.List(), info => Assert.True(info.Loaded));
    }

    [Fact]
    public async Task Reload_WithoutDependents_SaysSoWithoutMentioningACascade()
    {
        using var fixture = new HostFixture();
        fixture.WriteOrderPlugin("lonely");
        await fixture.Host.LoadAllAsync();

        PluginActionResult result = await fixture.Host.ReloadAsync("lonely");

        Assert.True(result.Success);
        Assert.DoesNotContain("dependent", result.Message);
    }

    private static PluginManifest Manifest(
        string id,
        (string Id, string Range)[]? requires = null,
        (string Id, string Range)[]? optional = null)
    {
        var toml = new System.Text.StringBuilder();
        toml.Append("id = \"").Append(id).Append("\"\nentry = \"").Append(id).Append(".cs\"\n");
        Append(toml, "requires", requires);
        Append(toml, "optional", optional);

        Assert.True(TestManifest.TryParse(toml.ToString(), out PluginManifest manifest, out string? error), error);
        return manifest;

        static void Append(System.Text.StringBuilder builder, string table, (string Id, string Range)[]? entries)
        {
            if (entries is null || entries.Length == 0)
                return;

            builder.Append('[').Append(table).Append("]\n");
            foreach ((string id, string range) in entries)
                builder.Append(id).Append(" = \"").Append(range).Append("\"\n");
        }
    }

    /// <summary>A temp plugins root, a non-started client, and a host over both.</summary>
    private sealed class HostFixture : IDisposable
    {
        public HostFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcc-plugin-deps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
            Host = new PluginHost(
                Client, Root, NullLoggerFactory.Instance, Client.Translations, Client.Variables);
            Host.InstallationSource = _ => Task.FromResult(TestPackages.Read(Root));
        }

        public string Root { get; }

        public Client Client { get; }

        public PluginHost Host { get; }

        public void WriteOrderPlugin(
            string id,
            string requires = "",
            string optional = "",
            string version = "1.0.0",
            bool enabled = true)
        {
            string folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "Entry.cs"), OrderPluginTemplate.Replace("__ID__", id, StringComparison.Ordinal));

            var manifest = new System.Text.StringBuilder();
            manifest.Append("id = \"").Append(id).Append("\"\n");
            manifest.Append("version = \"").Append(version).Append("\"\n");
            manifest.Append("entry = \"Entry.cs\"\napi-version = \"1.0\"\n");
            manifest.Append("enabled = ").Append(enabled ? "true" : "false").Append('\n');
            if (requires.Length > 0)
                manifest.Append("\n[requires]\n").Append(requires);

            if (optional.Length > 0)
                manifest.Append("\n[optional]\n").Append(optional);

            File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade(manifest.ToString()));
        }

        public string ManifestPath(string id) => Path.Combine(Root, id, PluginManifest.FileName);

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

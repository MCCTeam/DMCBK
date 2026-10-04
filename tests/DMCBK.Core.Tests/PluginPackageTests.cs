using System.IO.Compression;
using System.Net;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginPackageTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;
        public sealed class Probe : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "probe";
            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, session) =>
                {
                    context.Storage.Set("state", session.Session.State.ToString());
                    context.Storage.Save();
                };
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public void PackagesAreDeterministicAndChangedPayloadsRequireNewVersions()
    {
        using var fixture = new Fixture();
        PackedPlugin first = fixture.Pack(); PackedPlugin second = fixture.Pack();
        Assert.Equal(first.Release.Assets[0].Sha256, second.Release.Assets[0].Sha256);
        using ZipArchive archive = ZipFile.OpenRead(first.ArchivePath);
        using (var license = new StreamReader(archive.GetEntry("LICENSE.md")!.Open()))
            Assert.Equal("MIT License\nCopyright (c) Plugin author\n", license.ReadToEnd());
        Assert.All(archive.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));
        File.AppendAllText(Path.Combine(fixture.SourceFolder, "Probe.cs"), "\n// changed");
        Assert.Equal("pack.immutable-release-conflict", Assert.Throws<MarketplaceException>(() => fixture.Pack()).Code);
    }

    [Fact]
    public async Task PackagedSourcePassesPreparationAndRunsThroughARealSession()
    {
        using var fixture = new Fixture(); PackedPlugin package = fixture.Pack();
        await using PluginTestHost host = PluginTestHost.Create();
        using var http = new HttpClient(new ArchiveHttp(package.ArchivePath));
        var resolver = new DependencyResolver(HostInfo.FromClient(host.Client),
            [new("official", new() { SchemaVersion = 2, Id = "probe", Releases = [package.Release] })]);
        var installer = new MarketplaceInstaller(host.PluginsRoot, http, host.Plugins, resolver);
        await installer.ApplyAsync(await installer.PlanInstallAsync(new("probe")));
        Assert.True(Assert.Single(host.Plugins.List()).Loaded);
        await host.RunSessionAsync(async _ => Assert.True(await host.WaitForAsync(() => File.Exists(host.DataFile("probe", "storage.toml")))));
        Assert.Contains("state", await File.ReadAllTextAsync(host.DataFile("probe", "storage.toml")));
        Assert.False(Directory.Exists(Path.Combine(host.PluginsRoot, "versions", "probe", "1.0.0", "any", package.Release.Assets[0].Sha256, ".cache")));
    }

    [Fact]
    public async Task CompiledPortablePackageSharesContractsAndOmitsHostAssemblies()
    {
        using var fixture = new Fixture();
        CsPluginCompiler.CompileResult compiled = CsPluginCompiler.Compile(Path.Combine(fixture.SourceFolder, "Probe.cs"), []);
        Assert.True(compiled.Success, compiled.Error);
        fixture.SetCompiledManifest();
        File.WriteAllBytes(Path.Combine(fixture.SourceFolder, "Probe.dll"), compiled.Assembly!);
        File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(fixture.SourceFolder, "DMCBK.PluginSdk.dll"));
        PackedPlugin package = fixture.Pack("compiled");
        using (ZipArchive archive = ZipFile.OpenRead(package.ArchivePath)) Assert.Null(archive.GetEntry("DMCBK.PluginSdk.dll"));
        await using PluginTestHost host = PluginTestHost.Create();
        using var http = new HttpClient(new ArchiveHttp(package.ArchivePath));
        var installer = new MarketplaceInstaller(host.PluginsRoot, http, host.Plugins,
            new(HostInfo.FromClient(host.Client), [new("official", new() { SchemaVersion = 2, Id = "probe", Releases = [package.Release] })]));
        await installer.ApplyAsync(await installer.PlanInstallAsync(new("probe")));
        Assert.True(Assert.Single(host.Plugins.List()).Loaded);
        await host.RunSessionAsync(async _ => Assert.True(await host.WaitForAsync(() => File.Exists(host.DataFile("probe", "storage.toml")))));
    }

    [Theory]
    [InlineData(false, false, true, "compiled")]
    [InlineData(true, false, true, "source")]
    [InlineData(false, true, false, "source")]
    [InlineData(false, false, false, null)]
    public async Task MixedArchivesSelectAndDownloadOnlyThePermittedAsset(
        bool preferSource, bool fallback, bool portableBinary, string? selectedKind)
    {
        using var fixture = new Fixture();
        PackedPlugin source = fixture.Pack();
        CsPluginCompiler.CompileResult compiled = CsPluginCompiler.Compile(Path.Combine(fixture.SourceFolder, "Probe.cs"), []);
        Assert.True(compiled.Success, compiled.Error);
        File.WriteAllBytes(Path.Combine(fixture.SourceFolder, "Probe.dll"), compiled.Assembly!);
        await using PluginTestHost host = PluginTestHost.Create();
        string target = portableBinary ? "any" : HostInfo.FromClient(host.Client).RuntimeTarget == "win-x64" ? "linux-x64" : "win-x64";
        PackedPlugin binary = fixture.PackCompiled(target);
        ReleaseCatalogue catalogue = ReleaseCatalogueBuilder.Compose([new() { SchemaVersion = 2, Id = "probe", Releases = [source.Release] }, new() { SchemaVersion = 2, Id = "probe", Releases = [binary.Release] }]);
        var handler = new SelectedArchiveHttp(new Dictionary<string, string>
        {
            [source.Release.Assets.Single().Url] = source.ArchivePath,
            [binary.Release.Assets.Single().Url] = binary.ArchivePath,
        });
        using var http = new HttpClient(handler);
        var installer = new MarketplaceInstaller(host.PluginsRoot, http, host.Plugins,
            new(HostInfo.FromClient(host.Client), [new("official", catalogue)]));
        var request = new ResolutionRequest("probe") { Assets = new(preferSource, fallback) };
        if (selectedKind is null)
        {
            await Assert.ThrowsAsync<MarketplaceException>(() => installer.PlanInstallAsync(request));
            Assert.Empty(handler.Downloads);
            return;
        }
        PluginOperationResult installed = await installer.ApplyAsync(await installer.PlanInstallAsync(request));
        Assert.Equal(selectedKind, Assert.Single(installed.Selection.Plugins).Asset.Kind);
        string expected = selectedKind == "source" ? source.Release.Assets.Single().Url : binary.Release.Assets.Single().Url;
        Assert.Equal([expected], handler.Downloads);
        Assert.True(Assert.Single(host.Plugins.List()).Loaded);
        await host.RunSessionAsync(async _ => Assert.True(await host.WaitForAsync(() => File.Exists(host.DataFile("probe", "storage.toml")))));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcbk-pack-" + Guid.NewGuid().ToString("N"));
        public string SourceFolder { get; }
        public Fixture()
        {
            SourceFolder = Path.Combine(_root, "source"); Directory.CreateDirectory(SourceFolder);
            File.WriteAllText(Path.Combine(SourceFolder, "Probe.cs"), Source);
            File.WriteAllText(Path.Combine(SourceFolder, "LICENSE.md"), "MIT License\nCopyright (c) Plugin author\n");
            WriteManifest("source", "Probe.cs");
        }
        public void SetCompiledManifest() => WriteManifest("compiled", "Probe.dll");
        private void WriteManifest(string kind, string entry) => File.WriteAllText(Path.Combine(SourceFolder, "plugin.toml"), $"""
            schema-version = 2
            id = "probe"
            version = "1.0.0"
            kind = "{kind}"
            target = "any"
            entry = "{entry}"
            framework = "net10.0"
            api-version = "1.0"
            dmcbk = ">=0.1.0-preview.1 <0.2.0"
            umpk = ">=0.9.0-beta.4 <0.10.0"
            """);
        public PackedPlugin Pack(string kind = "source") => PluginPackageBuilder.Pack(SourceFolder, SourceFolder,
            Path.Combine(_root, "output"), kind, "any", new("https://example.org/releases/"));
        public PackedPlugin PackCompiled(string target) => PluginPackageBuilder.Pack(SourceFolder, SourceFolder,
            Path.Combine(_root, "output"), "compiled", target, new("https://example.org/releases/"), entry: "Probe.dll");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
    private sealed class SelectedArchiveHttp(Dictionary<string, string> archives) : HttpMessageHandler
    {
        public List<string> Downloads { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri; Downloads.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archives[url])) });
        }
    }
    private sealed class ArchiveHttp(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) });
    }
}

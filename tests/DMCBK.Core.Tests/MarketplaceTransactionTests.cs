using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class MarketplaceTransactionTests
{
    private static readonly HostInfo Host = new("1.0", "0.1.0-preview.1", "0.9.0-beta.4", "mcc", "2.0.0", "linux-x64", new HashSet<string> { "commands" });

    [Fact]
    public async Task DownloadsOneSelectedAssetAndPreservesUserDataAcrossUpdateAndRollback()
    {
        using var fixture = new Fixture();
        fixture.Add("app", "1.0.0"); fixture.Add("app", "2.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        PluginOperationResult first = await installer.ApplyAsync(await installer.PlanInstallAsync(new("app", ExactVersion: "1.0.0")));
        Assert.Single(fixture.Http.Requests);
        string data = Path.Combine(fixture.Root, "userdata", "app", "data"); Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "state.txt"), "persistent");
        PluginOperationResult second = await installer.ApplyAsync(await installer.PlanUpdateAsync(new("app")));
        Assert.Equal("2.0.0", Assert.Single(second.Selection.Plugins).Release.Version);
        Assert.Equal(2, fixture.Http.Requests.Count);
        await installer.ApplyAsync(await installer.PlanRollbackAsync(second.TransactionId));
        Assert.Equal("1.0.0", Assert.Single((await installer.ReadLockAsync()).Plugins).Release.Version);
        Assert.Equal("persistent", await File.ReadAllTextAsync(Path.Combine(data, "state.txt")));
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, first.Selection.Plugins[0].PackagePath)));
    }

    [Fact]
    public async Task DownloadsDependenciesAndActivatesInDependencyOrder()
    {
        using var fixture = new Fixture();
        fixture.Add("tools", "1.0.0"); fixture.Add("app", "1.0.0", requires: new() { ["tools"] = "^1.0.0" });
        MarketplaceInstaller installer = fixture.Installer();
        InstallPlan plan = await installer.PlanInstallAsync(new("app"));
        Assert.Equal(2, plan.Changes.Count);
        await installer.ApplyAsync(plan);
        Assert.Equal(["tools", "app"], fixture.Runtime.Activations.Last());
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.Equal(["tools", "app"], fixture.Runtime.Prepared.Last());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("manifest")]
    [InlineData("../escape.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("back\\slash.cs")]
    [InlineData("CON.cs")]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    public async Task RejectsInvalidPackagesBeforeChangingTheLockOrStoppingPlugins(string fault)
    {
        using var fixture = new Fixture();
        fixture.Add("app", "1.0.0", fault);
        MarketplaceInstaller installer = fixture.Installer();
        await Assert.ThrowsAnyAsync<Exception>(() => installer.ApplyAsync(installer.PlanInstallAsync(new("app")).GetAwaiter().GetResult()));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "plugins.lock.toml")));
        Assert.Empty(fixture.Runtime.Deactivations);
        Assert.Empty(fixture.Runtime.Activations);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.cs")));
    }

    [Fact]
    public async Task ActivationFailureRestoresThePreviousGraph()
    {
        using var fixture = new Fixture();
        fixture.Add("app", "1.0.0"); fixture.Add("app", "2.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        await installer.ApplyAsync(await installer.PlanInstallAsync(new("app", ExactVersion: "1.0.0")));
        fixture.Runtime.FailVersion = "2.0.0";
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.ApplyAsync(installer.PlanUpdateAsync(new("app")).GetAwaiter().GetResult()));
        Assert.Equal("1.0.0", Assert.Single((await installer.ReadLockAsync()).Plugins).Release.Version);
        Assert.Equal("1.0.0", fixture.Runtime.LastActiveVersion);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "transactions", "pending.toml")));
    }

    [Fact]
    public async Task FailedRollbackLeavesARecoverableJournal()
    {
        using var fixture = new Fixture();
        fixture.Add("app", "1.0.0"); fixture.Add("app", "2.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        await installer.ApplyAsync(await installer.PlanInstallAsync(new("app", ExactVersion: "1.0.0")));
        fixture.Runtime.FailAllActivation = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.ApplyAsync(installer.PlanUpdateAsync(new("app")).GetAwaiter().GetResult()));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "transactions", "pending.toml")));
        fixture.Runtime.FailAllActivation = false;
        Assert.True(await installer.RecoverAsync());
        Assert.False(await installer.RecoverAsync());
        Assert.Equal("1.0.0", Assert.Single((await installer.ReadLockAsync()).Plugins).Release.Version);
        Assert.Equal("1.0.0", fixture.Runtime.LastActiveVersion);
    }

    [Fact]
    public async Task StalePlansCannotOverwriteConcurrentChanges()
    {
        using var fixture = new Fixture(); fixture.Add("app", "1.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        InstallPlan first = await installer.PlanInstallAsync(new("app"));
        InstallPlan stale = await installer.PlanInstallAsync(new("app"));
        await installer.ApplyAsync(first);
        MarketplaceException error = await Assert.ThrowsAsync<MarketplaceException>(() => installer.ApplyAsync(stale));
        Assert.Equal("installation.plan-stale", error.Code);
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task PlansFreezeCatalogueMetadataAndPolicyLivesOutsideThePackage()
    {
        using var fixture = new Fixture(); PluginRelease release = fixture.Add("app", "1.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        InstallPlan plan = await installer.PlanInstallAsync(new("app"));
        release.Assets[0].Sha256 = new('b', 64); release.Version = "8.0.0";
        await installer.ApplyAsync(plan);
        await installer.ApplyAsync(await installer.PlanPolicyAsync("app", pinned: true, enabled: false));
        LockedPlugin locked = Assert.Single((await installer.ReadLockAsync()).Plugins);
        Assert.True(locked.Pinned); Assert.False(locked.Enabled); Assert.Equal("1.0.0", locked.Release.Version);
        InstallPlan removal = await installer.PlanUninstallAsync("app");
        PluginChange removed = Assert.Single(removal.Changes);
        Assert.Equal("installation.uninstall", removed.Reason); Assert.Empty(removed.Version);
        Assert.DoesNotContain("enabled", await File.ReadAllTextAsync(Path.Combine(fixture.Root, locked.PackagePath, "plugin.toml")));
    }

    [Fact]
    public async Task CancelledDownloadsAndSourcePreparationFailuresDoNotCommit()
    {
        using var fixture = new Fixture(); fixture.Add("app", "1.0.0");
        MarketplaceInstaller installer = fixture.Installer();
        InstallPlan plan = await installer.PlanInstallAsync(new("app"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ApplyAsync(plan, cancellation.Token));
        fixture.Runtime.FailPreparation = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ApplyAsync(plan));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "plugins.lock.toml")));
        Assert.Empty(fixture.Runtime.Deactivations);
    }

    [Fact]
    public async Task ArchiveLimitsRejectOversizedPayloads()
    {
        using var fixture = new Fixture(); fixture.Add("app", "1.0.0");
        MarketplaceInstaller installer = fixture.Installer(new(ExpandedBytes: 1));
        MarketplaceException error = await Assert.ThrowsAsync<MarketplaceException>(() => installer.ApplyAsync(installer.PlanInstallAsync(new("app")).GetAwaiter().GetResult()));
        Assert.Equal("installation.archive-expanded-size", error.Code);
        Assert.Empty(fixture.Runtime.Deactivations);
    }

    [Fact]
    public async Task ConcurrentWritersCommitOnlyOnePlan()
    {
        using var fixture = new Fixture(); fixture.Add("app", "1.0.0");
        MarketplaceInstaller first = fixture.Installer(), second = fixture.Installer();
        InstallPlan a = await first.PlanInstallAsync(new("app"));
        InstallPlan b = await second.PlanInstallAsync(new("app"));
        async Task<Exception?> Apply(MarketplaceInstaller installer, InstallPlan plan)
        {
            try { await installer.ApplyAsync(plan); return null; }
            catch (Exception exception) { return exception; }
        }
        Exception?[] results = await Task.WhenAll(Apply(first, a), Apply(second, b));
        Assert.Single(results, result => result is null);
        MarketplaceException rejection = Assert.IsType<MarketplaceException>(Assert.Single(results, result => result is not null));
        Assert.Equal("installation.plan-stale", rejection.Code);
        Assert.Single((await first.ReadLockAsync()).Plugins);
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task PurgeRejectsALinkedUserDataParent()
    {
        using var fixture = new Fixture();
        string outside = Path.Combine(Path.GetTempPath(), "dmcbk-purge-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "app"));
        string protectedFile = Path.Combine(outside, "app", "data.txt"); File.WriteAllText(protectedFile, "keep");
        string link = Path.Combine(fixture.Root, "userdata");
        Directory.CreateDirectory(fixture.Root);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            MarketplaceException error = await Assert.ThrowsAsync<MarketplaceException>(() => fixture.Installer().PurgeUserDataAsync("app"));
            Assert.Equal("installation.storage-link", error.Code);
            Assert.Equal("keep", File.ReadAllText(protectedFile));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dmcbk-transactions-" + Guid.NewGuid().ToString("N"));
        public AssetHttp Http { get; } = new();
        public TestRuntime Runtime { get; } = new();
        private readonly List<CatalogueSource> _sources = [];
        private readonly HttpClient _client;
        public Fixture() { _client = new(Http); }
        public MarketplaceInstaller Installer(PackageLimits? limits = null) => new(Root, _client, Runtime, new(Host, _sources), limits);
        public PluginRelease Add(string id, string version, string? fault = null, Dictionary<string, string>? requires = null)
        {
            string actualId = fault == "manifest" ? "different" : id;
            string manifest = $"""
                schema-version = 2
                id = "{actualId}"
                version = "{version}"
                kind = "source"
                target = "any"
                entry = "Plugin.cs"
                framework = "net10.0"
                api-version = "1.0"
                dmcbk = ">=0.1.0-preview.1 <0.2.0"
                umpk = ">=0.9.0-beta.4 <0.10.0"
                needs = ["commands"]
                """;
            if (requires is not null) manifest += "\n[requires]\n" + string.Join('\n', requires.Select(pair => $"{pair.Key} = \"{pair.Value}\""));
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write("plugin.toml", manifest); Write("Plugin.cs", "// source fixture");
                if (fault is not null && fault is not ("hash" or "manifest"))
                {
                    ZipArchiveEntry extra = Write(fault == "duplicate" ? "PLUGIN.CS" : fault == "symlink" ? "link" : fault, "extra");
                    if (fault == "symlink") extra.ExternalAttributes = 0xa000 << 16;
                }
                ZipArchiveEntry Write(string name, string text)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name);
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(text);
                    return entry;
                }
            }
            byte[] bytes = memory.ToArray(); string url = $"https://example.org/{id}/{version}.zip"; Http.Payloads[url] = bytes;
            var release = new PluginRelease
            {
                Version = version,
                ApiVersion = "1.0",
                Dmcbk = ">=0.1.0-preview.1 <0.2.0",
                Umpk = ">=0.9.0-beta.4 <0.10.0",
                Framework = "net10.0",
                Needs = ["commands"],
                Requires = requires ?? [],
                Assets = [new() { Kind = "source", Target = "any", Url = url, Sha256 = fault == "hash" ? new('a', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)) }]
            };
            CatalogueSource? source = _sources.Find(source => source.Catalogue.Id == id);
            if (source is null) _sources.Add(new("official", new() { SchemaVersion = 2, Id = id, Releases = [release] }));
            else source.Catalogue.Releases.Add(release);
            return release;
        }
        public void Dispose() { _client.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class AssetHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Payloads { get; } = [];
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); string url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payloads[url]) });
        }
    }

    private sealed class TestRuntime : IPluginInstallationHost
    {
        public List<string[]> Prepared { get; } = [];
        public List<string[]> Activations { get; } = [];
        public List<string[]> Deactivations { get; } = [];
        public string? FailVersion { get; set; }
        public bool FailAllActivation { get; set; }
        public bool FailPreparation { get; set; }
        public string? LastActiveVersion { get; private set; }
        public ValueTask PrepareAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken)
        { if (FailPreparation) throw new InvalidDataException("compilation failed"); Prepared.Add(graph.Select(plugin => plugin.Manifest.Id).ToArray()); return ValueTask.CompletedTask; }
        public ValueTask DeactivateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
        { Deactivations.Add(ids.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask ActivateAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken)
        {
            if (FailAllActivation || graph.Any(plugin => plugin.Manifest.Version == FailVersion)) throw new InvalidOperationException("activation failed");
            Activations.Add(graph.Where(plugin => plugin.Enabled).Select(plugin => plugin.Manifest.Id).ToArray());
            LastActiveVersion = graph.FirstOrDefault(plugin => plugin.Enabled)?.Manifest.Version;
            return ValueTask.CompletedTask;
        }
    }
}

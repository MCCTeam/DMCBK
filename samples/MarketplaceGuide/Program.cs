using System.Net;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using DMCBK.Testing;

string workspace = Path.Combine(Path.GetTempPath(), "dmcbk-market-guide-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
try
{
    string source = Path.Combine(workspace, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "Probe.cs"), """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;
        public sealed class Probe : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "guide-probe";
            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, _) => context.Variables.Set("guide_probe_started", "yes");
                return Task.CompletedTask;
            }
        }
        """);
    File.WriteAllText(Path.Combine(source, "plugin.toml"), """
        schema-version = 2
        id = "guide-probe"
        version = "1.0.0"
        kind = "source"
        target = "any"
        entry = "Probe.cs"
        framework = "net10.0"
        api-version = "1.0"
        dmcbk = ">=0.1.0-preview.1 <0.2.0"
        umpk = ">=0.9.0-beta.4 <0.10.0"
        """);
    string output = Path.Combine(workspace, "packages");
    PackedPlugin packed = PluginPackageBuilder.Pack(source, source, output,
        "source", "any", new Uri("https://example.invalid/releases/"));
    PackedPlugin repeated = PluginPackageBuilder.Pack(source, source, output,
        "source", "any", new Uri("https://example.invalid/releases/"));
    Require(packed.Release.Assets[0].Sha256 == repeated.Release.Assets[0].Sha256, "Deterministic packaging failed.");

    await using PluginTestHost host = PluginTestHost.Create();
    using var handler = new ArchiveHandler(packed.Release.Assets[0].Url, packed.ArchivePath);
    using var http = new HttpClient(handler);
    var catalogue = new ReleaseCatalogue { SchemaVersion = 2, Id = "guide-probe", Releases = [packed.Release] };
    var resolver = new DependencyResolver(HostInfo.FromClient(host.Client), [new("guide", catalogue)]);
    var installer = new MarketplaceInstaller(host.PluginsRoot, http, host.Plugins, resolver);
    InstallPlan plan = await installer.PlanInstallAsync(new("guide-probe", "guide", ExactVersion: "1.0.0"));
    Require(handler.Downloads == 0, "Planning downloaded a package.");
    Require(plan.Changes.Count == 1, "Expected one planned change.");
    PluginOperationResult installed = await installer.ApplyAsync(plan);
    Require(handler.Downloads == 1, "Expected exactly one asset download.");
    Require(host.Plugins.List().Single().Loaded, "The packaged plugin did not activate.");
    await host.RunSessionAsync(async _ =>
        Require(await host.WaitForAsync(() => host.Client.Variables.Get("guide_probe_started") == "yes"),
            "The plugin session callback did not run."));
    Require(installed.Selection.Plugins.Single().Asset.Sha256 == packed.Release.Assets[0].Sha256, "The lock hash differs.");
    await installer.ApplyAsync(await installer.PlanPolicyAsync("guide-probe", pinned: true));
    Require((await installer.ReadLockAsync()).Plugins.Single().Pinned, "Pin was not saved.");
    await installer.ApplyAsync(await installer.PlanPolicyAsync("guide-probe", pinned: false));
    await installer.ApplyAsync(await installer.PlanUninstallAsync("guide-probe"));
    Require((await installer.ReadLockAsync()).Plugins.Count == 0, "Uninstall did not clear the selection.");
    Console.WriteLine("PASS: deterministic package, exact plan, one download, source activation, session callback, lock hash, pin and uninstall.");
}
finally
{
    Directory.Delete(workspace, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ArchiveHandler(string expectedUrl, string archivePath) : HttpMessageHandler
{
    public int Downloads { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != expectedUrl)
            throw new InvalidOperationException("Unexpected download URL.");
        Downloads++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(archivePath))
        });
    }
}

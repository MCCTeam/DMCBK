using System.Collections.Concurrent;
using DMCBK.Core;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Exported assemblies and <c>context.Services</c>.
/// Both answer the same question: DiscordBridge receives map images as <c>string[]</c> because a type declared in the Map plugin does not unify across two collectible load contexts.
/// With <c>[exports]</c> it does.
/// A type declared in one plugin's single <c>.cs</c> file is compiled into another plugin, travels through the messenger, and arrives as itself.
/// </summary>
public sealed class PluginExportsTests
{
    /// <summary>The exporter: declares the shared contract types, subscribes, and offers a service.</summary>
    private const string ExporterSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ExportedPing
        {
            public string Text { get; set; } = "";
        }

        public interface IExportedGreeter
        {
            string Greet(string name);
        }

        public sealed class ExporterPlugin : IPlugin, IExportedGreeter
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "exporter";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Subscribe<ExportedPing>(ping =>
                {
                    context.Storage.Set("got", ping.Text);
                    context.Storage.Save();
                });
                context.Services.Register<IExportedGreeter>(this);
                return Task.CompletedTask;
            }

            public string Greet(string name) => "hello " + name;
        }
        """;

    /// <summary>The dependent: uses the exporter's own types, which only [exports] allows.</summary>
    private const string DependentSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class DependentPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "dependent";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Publish(new ExportedPing { Text = "typed-across-contexts" });
                context.Storage.Set(
                    "greeting",
                    context.Services.TryGet<IExportedGreeter>(out var greeter) ? greeter.Greet("world") : "none");
                context.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>A plugin that keeps its contract to itself: nobody can reach it, and the host must say so.</summary>
    private const string LonerSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class LonerPing
        {
            public string Text { get; set; } = "";
        }

        public sealed class LonerPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "loner";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Subscribe<LonerPing>(_ => { });
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task ExportedType_TravelsThroughTheMessenger_Typed()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource, exports: "\n[exports]\nassemblies = [\"entry\"]\n");
        fixture.WritePlugin("dependent", DependentSource, requires: "\n[requires]\nexporter = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.All(fixture.Host.List(), info => Assert.True(info.Loaded, info.Status));

        // The exporter received the message as its own type, from an instance the dependent constructed in another load context.
        // Without [exports] the dependent could not name the type at all.
        PluginContext exporter = fixture.Context("exporter");
        Assert.True(exporter.Storage.TryGet("got", out string? got));
        Assert.Equal("typed-across-contexts", got);
    }

    [Fact]
    public async Task ExportedInterface_ReachesTheDependentThroughServices()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource, exports: "\n[exports]\nassemblies = [\"entry\"]\n");
        fixture.WritePlugin("dependent", DependentSource, requires: "\n[requires]\nexporter = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.True(fixture.Context("dependent").Storage.TryGet("greeting", out string? greeting));
        Assert.Equal("hello world", greeting);
    }

    /// <summary>
    /// The control the two tests above need.
    /// With the export withdrawn the dependent's source cannot see the type and fails to compile, which is what proves they measure the export and not some accident of both plugins living in one process.
    /// </summary>
    [Fact]
    public async Task WithoutTheExport_TheDependentDoesNotCompile()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource);
        fixture.WritePlugin("dependent", DependentSource, requires: "\n[requires]\nexporter = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.True(fixture.Host.List().Single(p => p.Id == "exporter").Loaded);
        PluginInfo dependent = fixture.Host.List().Single(p => p.Id == "dependent");
        Assert.False(dependent.Loaded);

        PluginActionResult reload = await fixture.Host.ReloadAsync("dependent");
        Assert.False(reload.Success);
        Assert.Contains("ExportedPing", reload.Message);
    }

    [Fact]
    public async Task ExportsAreDroppedOnUnload_SoTheContextStillCollects()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource, exports: "\n[exports]\nassemblies = [\"entry\"]\n");
        await fixture.Host.LoadAllAsync();

        WeakReference? alc = fixture.Host.GetLoadContextRef("exporter");
        Assert.NotNull(alc);
        await fixture.Host.UnloadAsync("exporter");

        Assert.True(WaitForCollection(alc), "the exporter's load context was still rooted after unload");
    }

    [Fact]
    public async Task Services_AreWithdrawnWhenTheOfferingPluginUnloads()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource, exports: "\n[exports]\nassemblies = [\"entry\"]\n");
        fixture.WritePlugin("dependent", DependentSource, requires: "\n[requires]\nexporter = \"*\"\n");
        await fixture.Host.LoadAllAsync();

        // Unloading in dependency order: the dependent first, since the exporter is refused while it holds.
        await fixture.Host.UnloadAsync("dependent");
        await fixture.Host.UnloadAsync("exporter");

        // Re-loading the exporter must not trip "already offered": the registration went with the unload.
        Assert.True((await fixture.Host.EnableAsync("exporter")).Success);
    }

    [Fact]
    public void ServiceHub_RefusesASecondOfferOfTheSameContract()
    {
        var hub = new PluginServiceHub(NullLogger.Instance);
        var first = new PluginServices(hub, "one");
        var second = new PluginServices(hub, "two");
        first.Register(new System.Text.StringBuilder("first"));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => second.Register(new System.Text.StringBuilder("second")));
        Assert.Contains("one", error.Message);
    }

    [Fact]
    public void ServiceHub_LooksUpAcrossPlugins_AndForgetsOnDispose()
    {
        var hub = new PluginServiceHub(NullLogger.Instance);
        var offering = new PluginServices(hub, "offering");
        var asking = new PluginServices(hub, "asking");

        Assert.False(asking.TryGet(out System.Text.StringBuilder? before));
        Assert.Null(before);

        offering.Register(new System.Text.StringBuilder("shared"));
        Assert.True(asking.TryGet(out System.Text.StringBuilder? found));
        Assert.Equal("shared", found!.ToString());

        offering.DisposeAll();
        Assert.False(asking.TryGet(out System.Text.StringBuilder? after));
        Assert.Null(after);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    /// <summary>
    /// Waits for a collectible load context to go away.
    /// The yield between rounds is what makes this reliable under xUnit's per-class parallelism: an unload that finished on a pool thread can leave the context reachable from that thread's stack for a moment, and a tight GC loop here never lets it move on.
    /// Without the yield this failed about one run in six with other plugin classes running beside it.
    /// </summary>
    private static bool WaitForCollection(WeakReference reference)
    {
        for (int i = 0; i < 20 && reference.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (reference.IsAlive)
                Thread.Sleep(20);
        }

        return !reference.IsAlive;
    }

    /// <summary>
    /// The messenger and the service registry warn when a contract is private to one plugin's load context.
    /// An exported assembly is the case where that is false, and the warning used to fire on it anyway.
    /// </summary>
    [Fact]
    public async Task AnExportedContract_IsNotReportedAsPrivate()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("exporter", ExporterSource, exports: "\n[exports]\nassemblies = [\"entry\"]\n");
        fixture.WritePlugin("dependent", DependentSource, requires: "\n[requires]\nexporter = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.All(fixture.Host.List(), info => Assert.True(info.Loaded, info.Status));
        Assert.DoesNotContain(fixture.Logs, line => line.Contains("private to load context", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control for the test above.
    /// A contract nobody exports is unreachable and must still warn; otherwise "no warning" would also pass with the check deleted.
    /// </summary>
    [Fact]
    public async Task APluginPrivateContract_IsStillReportedAsPrivate()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("loner", LonerSource);

        await fixture.Host.LoadAllAsync();

        Assert.All(fixture.Host.List(), info => Assert.True(info.Loaded, info.Status));
        Assert.Contains(fixture.Logs, line => line.Contains("private to load context", StringComparison.Ordinal));
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly CapturingLoggerFactory _loggerFactory = new();

        public HostFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcc-plugin-exports", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
            Host = new PluginHost(
                Client, Root, _loggerFactory, Client.Translations, Client.Variables);
            Host.InstallationSource = _ => Task.FromResult(TestPackages.Read(Root));
        }

        public IReadOnlyCollection<string> Logs => [.. _loggerFactory.Lines];

        public string Root { get; }

        public Client Client { get; }

        public PluginHost Host { get; }

        public void WritePlugin(string id, string source, string requires = "", string exports = "")
        {
            string folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Entry.cs"), source);
            File.WriteAllText(
                Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"Entry.cs\"\napi-version = \"1.0\"\nenabled = true\n{requires}{exports}"));
        }

        public PluginContext Context(string id)
        {
            PluginContext? context = Host.GetContext(id);
            Assert.NotNull(context);
            return context;
        }

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

    /// <summary>An ILoggerFactory that keeps every formatted line, so a diagnostic can be asserted on.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capturing(ConcurrentBag<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Add(formatter(state, exception));
        }
    }
}

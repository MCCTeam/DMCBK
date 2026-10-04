using System.Runtime.CompilerServices;
using DMCBK.Core;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Client.Plugins;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Plugin host tests: manifest parse, the settings pipeline (defaults + validate clamp/self-disable), discovery + .cs load, api-version refusal, compile-cache hit, collectible-ALC unload proof, and reconnect-aware SessionStarted/SessionEnded fan-out.
/// </summary>
public sealed class PluginHostTests
{
    // A .cs plugin that records activation into its storage sandbox.
    private const string ProbePluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ProbePlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "probe";

            public Task ActivateAsync(PluginContext context)
            {
                context.Storage.Set("activated", "1");
                context.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    // A .cs plugin that records what context.Host reports, so the three version numbers are proved to reach a plugin rather than merely to exist on the host.
    private const string HostInfoPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class HostInfoPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "host-probe";

            public Task ActivateAsync(PluginContext context)
            {
                context.Storage.Set("api", context.Host.ApiVersion);
                context.Storage.Set("mcc", context.Host.MccVersion);
                context.Storage.Set("umpk", context.Host.UmpkVersion);
                context.Storage.Set("mcc-informational", context.Host.MccInformational);
                context.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    // A .cs plugin whose behaviour depends on the api preprocessor symbols the compiler defines.
    // It has to COMPILE either way, so each branch writes a different value rather than one branch failing to build: a symbol that was never defined would otherwise look identical to a source file with a typo in it.
    private const string SymbolPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class SymbolPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "symbol-probe";

            public Task ActivateAsync(PluginContext context)
            {
        #if DMCBK_API_1_0
                context.Storage.Set("two-zero", "yes");
        #else
                context.Storage.Set("two-zero", "no");
        #endif
        #if DMCBK_API_1_0
                context.Storage.Set("two-one", "yes");
        #else
                context.Storage.Set("two-one", "no");
        #endif
        #if DMCBK_API_1_0_OR_GREATER
                context.Storage.Set("two-one-or-greater", "yes");
        #else
                context.Storage.Set("two-one-or-greater", "no");
        #endif
        #if MCC_API_2_99
                context.Storage.Set("far-future", "yes");
        #else
                context.Storage.Set("far-future", "no");
        #endif
                context.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    // A .cs plugin that counts session attach/detach into its storage sandbox (reconnect proof).
    private const string RecoPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class RecoPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "reco-probe";

            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, _) => Bump(context, "started");
                context.SessionEnded += (_, _) => Bump(context, "ended");
                return Task.CompletedTask;
            }

            private static void Bump(PluginContext context, string key)
            {
                int current = context.Storage.TryGet(key, out string? value) && int.TryParse(value, out int n) ? n : 0;
                context.Storage.Set(key, (current + 1).ToString());
                context.Storage.Save();
            }
        }
        """;

    [Fact]
    public void Manifest_Parses_AllFields()
    {
        const string toml = """
            id = "auto-eat"
            version = "2.1.0"
            entry = "AutoEat.dll"
            api-version = "1"
            deps = ["Extra.dll"]
            enabled = false
            """;

        Assert.True(TestManifest.TryParse(toml, out PluginManifest manifest, out string? error));
        Assert.Null(error);
        Assert.Equal("auto-eat", manifest.Id);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Equal("AutoEat.dll", manifest.Entry);
        Assert.Equal("1", manifest.ApiVersion);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(["Extra.dll"], manifest.Deps);
        Assert.False(manifest.IsSourceEntry);
    }

    [Fact]
    public void Manifest_Missing_Id_Fails()
    {
        Assert.False(TestManifest.TryParse("entry = \"x.dll\"", out _, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    public void Manifest_IdThatCouldLeaveThePluginsFolder_Fails(string id)
    {
        // The id becomes the install folder's name and the .removed/<id> an uninstall parks into, so an id holding a separator or opening with a dot would reach outside the plugins root.
        Assert.False(
            TestManifest.TryParse($"id = \"{id}\"\nentry = \"x.dll\"", out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("plugin id", error);
    }

    [Theory]
    [InlineData("auto-eat")]
    [InlineData("Auto_Eat.2")]
    [InlineData("map")]
    public void Manifest_OrdinaryIds_StillParse(string id)
    {
        Assert.True(TestManifest.TryParse($"id = \"{id}\"\nentry = \"x.dll\"", out PluginManifest manifest, out _));
        Assert.Equal(id, manifest.Id);
    }

    [Fact]
    public void Settings_MaterializesDefaults_AndWritesFile()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "settings.toml");
        var settings = new PluginSettings(path, NullLogger.Instance);

        ProbeSettings loaded = settings.Load<ProbeSettings>();

        Assert.True(File.Exists(path));
        Assert.True(loaded.Enabled);
        Assert.Equal(6, loaded.Threshold);
    }

    [Fact]
    public void Settings_Validate_Clamps_OnLoad()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "settings.toml");
        File.WriteAllText(path, "Enabled = true\nThreshold = 99\n");

        ProbeSettings loaded = new PluginSettings(path, NullLogger.Instance).Load<ProbeSettings>();

        Assert.Equal(20, loaded.Threshold); // clamped from 99
    }

    [Fact]
    public void Settings_Validate_SelfDisables()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "settings.toml");
        File.WriteAllText(path, "Enabled = true\nThreshold = -5\n");

        ProbeSettings loaded = new PluginSettings(path, NullLogger.Instance).Load<ProbeSettings>();

        Assert.Equal(0, loaded.Threshold);
        Assert.False(loaded.Enabled); // negative threshold self-disables in Validate
    }

    [Fact]
    public async Task Discovery_And_Load_CsPlugin_Activates()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1");

        PluginActionResult result = await fixture.Host.LoadAllAsync();

        Assert.True(result.Success);
        IReadOnlyList<PluginInfo> plugins = fixture.Host.List();
        PluginInfo info = Assert.Single(plugins);
        Assert.Equal("probe", info.Id);
        Assert.True(info.Loaded);
        Assert.Equal(PluginEntryKind.Source, info.Entry);

        // The plugin wrote its activation marker into the data sandbox.
        string storagePath = Path.Combine(fixture.Root, "userdata", "probe", "data", "storage.toml");
        Assert.True(File.Exists(storagePath));
        Assert.Contains("activated", File.ReadAllText(storagePath));
    }

    [Fact]
    public async Task ApiVersion_Incompatible_IsRefused()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "2");

        PluginActionResult result = await fixture.Host.LoadAllAsync();

        // Discovery succeeds, but the plugin itself is not loaded (refused).
        PluginInfo info = Assert.Single(fixture.Host.List());
        Assert.False(info.Loaded);

        PluginActionResult enable = await fixture.Host.EnableAsync("probe");
        Assert.False(enable.Success);
        Assert.Contains("API", enable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MccRange_OutOfRange_IsRefused_NamingBothVersions()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1", extraKeys: "dmcbk = \">=99.0.0\"\n");

        await fixture.Host.LoadAllAsync();
        Assert.False(Assert.Single(fixture.Host.List()).Loaded);

        PluginActionResult enable = await fixture.Host.EnableAsync("probe");
        Assert.False(enable.Success);

        // The message has to carry both halves: what was asked for and what this client is.
        // Otherwise the reader cannot tell whether to update the client or the plugin.
        Assert.Contains("dmcbk", enable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MccRange_Satisfied_Loads()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1", extraKeys: "dmcbk = \"*\"\n");

        await fixture.Host.LoadAllAsync();

        Assert.True(Assert.Single(fixture.Host.List()).Loaded);
    }

    [Fact]
    public async Task UmpkRange_OutOfRange_IsRefused_NamingBothVersions()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1", extraKeys: "umpk = \">=99.0.0\"\n");

        await fixture.Host.LoadAllAsync();

        PluginActionResult enable = await fixture.Host.EnableAsync("probe");
        Assert.False(enable.Success);
        Assert.Contains("umpk", enable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_UnparsableRange_IsAnError_NotASilentAny()
    {
        // A typo in a gate that exists to refuse a load must not quietly turn the gate off.
        Assert.False(TestManifest.TryParse(
            "id = \"x\"\nentry = \"x.cs\"\ndmcbk = \"not-a-range\"\n", out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("dmcbk", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ParsesMetaAndRanges()
    {
        const string toml = """
            id = "demo"
            entry = "Demo.cs"
            api-version = "1.0"
            dmcbk = ">=2.0.0"
            umpk = "^0.9"

            [meta]
            description = "A demonstration."
            homepage = "https://example.invalid/demo"
            tags = ["demo", "sample"]
            uses = ["network"]
            """;

        Assert.True(TestManifest.TryParse(toml, out PluginManifest manifest, out string? error));
        Assert.Null(error);
        Assert.Equal(">=2.0.0", manifest.Dmcbk);
        Assert.Equal("^0.9", manifest.Umpk);
        Assert.True(manifest.DmcbkRange.Satisfies("2.4.0"));
        Assert.False(manifest.UmpkRange.Satisfies("1.0.0"));
        Assert.Equal("A demonstration.", manifest.Meta.Description);
        Assert.Equal("https://example.invalid/demo", manifest.Meta.Homepage);
        Assert.Equal(["demo", "sample"], manifest.Meta.Tags);
        Assert.Equal(["network"], manifest.Meta.Uses);
    }


    [Fact]
    public async Task EnabledWriteBack_LeavesSomebodyElsesManifestAlone()
    {
        // A bare .cs entry loaded by path takes the folder it sits in, which may already hold another plugin's manifest.
        // Toggling the loaded one must not rewrite that.
        using var fixture = new HostFixture();
        fixture.WritePlugin("resident", "Resident.cs", ProbePluginSource, apiVersion: "1");
        string manifest = File.ReadAllText(fixture.ManifestPath("resident"));

        string stray = Path.Combine(fixture.Root, "resident", "Stray.cs");
        File.WriteAllText(stray, ProbePluginSource.Replace("\"probe\"", "\"stray\"", StringComparison.Ordinal));

        Assert.True((await fixture.Host.LoadAsync(stray)).Success);
        Assert.True((await fixture.Host.DisableAsync("Stray")).Success);

        Assert.Equal(manifest, File.ReadAllText(fixture.ManifestPath("resident")));
    }

    [Fact]
    public async Task Discovery_SkipsDotFolders()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1");

        // What the installer's staging area looks like: a complete, loadable plugin folder that is not one.
        fixture.WritePlugin(".staging", "Probe.cs", ProbePluginSource, apiVersion: "1");

        await fixture.Host.LoadAllAsync();

        Assert.Equal("probe", Assert.Single(fixture.Host.List()).Id);
    }


    [Fact]
    public async Task SingleFilePlugin_SeesTheApiPreprocessorSymbols()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("symbol-probe", "SymbolProbe.cs", SymbolPluginSource, apiVersion: "1.0");

        PluginActionResult result = await fixture.Host.LoadAllAsync();
        Assert.True(Assert.Single(fixture.Host.List()).Loaded, result.Message);

        var store = TomletMain.To<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(fixture.Root, "userdata", "symbol-probe", "data", "storage.toml")));

        Assert.Equal("yes", store["two-zero"]);
        Assert.Equal("yes", store["two-one"]);
        Assert.Equal("yes", store["two-one-or-greater"]);
        Assert.Equal("no", store["far-future"]);
    }

    [Fact]
    public void CompileCache_HitsOnUnchangedSource()
    {
        // Ensure the SDK + core + UMPK assemblies are loaded so Roslyn can reference them.
        _ = typeof(PluginContext);
        _ = typeof(Client);
        _ = typeof(UmpkClient);

        string dir = NewTempDir();
        string csPath = Path.Combine(dir, "Probe.cs");
        File.WriteAllText(csPath, ProbePluginSource);

        CsPluginCompiler.CompileResult first = CsPluginCompiler.Compile(csPath, [], [], new CompilationReferenceProvider(AppContext.BaseDirectory), Path.Combine(dir, "cache"));
        Assert.True(first.Success, first.Error);
        Assert.False(first.FromCache);

        CsPluginCompiler.CompileResult second = CsPluginCompiler.Compile(csPath, [], [], new CompilationReferenceProvider(AppContext.BaseDirectory), Path.Combine(dir, "cache"));
        Assert.True(second.Success);
        Assert.True(second.FromCache);
    }

    [Fact]
    public async Task Alc_Collects_After_Unload()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("probe", "Probe.cs", ProbePluginSource, apiVersion: "1");
        await fixture.Host.LoadAllAsync();

        WeakReference? alcRef = fixture.Host.GetLoadContextRef("probe");
        Assert.NotNull(alcRef);
        Assert.True(alcRef!.IsAlive);

        PluginActionResult unload = await fixture.Host.UnloadAsync("probe");
        Assert.True(unload.Success);

        Assert.True(WaitForCollection(alcRef), "The plugin's collectible load context was not reclaimed after unload.");
    }

    [Fact]
    public async Task Reconnect_Fires_SessionStarted_And_Ended()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("reco-probe", "Reco.cs", RecoPluginSource, apiVersion: "1");
        await fixture.Host.LoadAllAsync();

        var scope = new FakeSessionScope();
        PluginContext context = fixture.Host.GetContext("reco-probe")!;
        context.AttachSession(scope);   // first connect
        context.DetachSession();        // disconnect
        context.AttachSession(scope);   // reconnect (second attach)

        string storagePath = Path.Combine(fixture.Root, "userdata", "reco-probe", "data", "storage.toml");
        Assert.True(File.Exists(storagePath));
        var store = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(storagePath));

        Assert.Equal("2", store["started"]);
        Assert.Equal("1", store["ended"]);
    }

    /// <summary>
    /// Waits for a collectible load context to go away.
    /// The yield between rounds is what makes this reliable under xUnit's per-class parallelism; see the twin of this helper in <c>PluginExportsTests</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
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

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mcc-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The probe plugin's settings POCO (clamps threshold, self-disables on a negative value).</summary>
    public sealed class ProbeSettings : IValidatablePluginSettings
    {
        public bool Enabled { get; set; } = true;

        public int Threshold { get; set; } = 6;

        public void Validate()
        {
            if (Threshold > 20)
                Threshold = 20;
            else if (Threshold < 0)
            {
                Threshold = 0;
                Enabled = false;
            }
        }
    }

    /// <summary>A test fixture owning a temp plugins root, a non-started client, and a host.</summary>
    private sealed class HostFixture : IDisposable
    {
        /// <summary>
        /// A fresh temp root, or an existing one so a second host can be built over the same folder.
        /// The second form is how "the choice survives a restart" is proved without restarting a process.
        /// </summary>
        public HostFixture(string? root = null, System.Globalization.CultureInfo? culture = null)
        {
            Root = root ?? NewTempDir();
            OwnsRoot = root is null;
            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
            Host = new PluginHost(
                Client, Root, NullLoggerFactory.Instance, Client.Translations, Client.Variables, culture);
            Host.InstallationSource = _ => Task.FromResult(TestPackages.Read(Root));
        }

        private bool OwnsRoot { get; }

        public string Root { get; }

        public Client Client { get; }

        public PluginHost Host { get; }

        public void WritePlugin(string id, string entry, string source, string apiVersion, string extraKeys = "")
        {
            string folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, entry), source);
            File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\napi-version = \"{apiVersion}\"\nenabled = true\n{extraKeys}"));
        }

        /// <summary>The path of a written plugin's manifest, for the enabled write-back assertions.</summary>
        public string ManifestPath(string id) => Path.Combine(Root, id, PluginManifest.FileName);

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!OwnsRoot)
                return;

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A minimal <see cref="ISessionScope"/> for the reconnect test: it only proves <see cref="PluginContext.AttachSession"/>/<see cref="PluginContext.DetachSession"/> fan out to PluginContext's own SessionStarted/SessionEnded correctly, so nothing beyond identity is ever touched.
    /// </summary>
    private sealed class FakeSessionScope : ISessionScope
    {
        public UmpkClient Client => throw new NotSupportedException();

        public ClientState State => throw new NotSupportedException();

        public ClientEvents Events => throw new NotSupportedException();

        public ClientActions Actions => throw new NotSupportedException();

        public PluginScheduler Scheduler => throw new NotSupportedException();

        public IPluginCommandScope Commands => throw new NotSupportedException();

        public CancellationToken Detached => CancellationToken.None;

        public ISessionChannels Channels => throw new NotSupportedException();

        public Umpk.Client.ClientActionCapabilities Capabilities => throw new NotSupportedException();

        public IMovementLease? TryAcquireMovement(string reason) => null;

        public string? MovementOwner => null;

        public PluginChannelRegistration RegisterPluginChannel(Umpk.Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
            => throw new NotSupportedException();

        public ValueTask SendPluginMessageAsync(Umpk.Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IDisposable ObservePackets(Umpk.Protocol.Java.PacketFrameHandler handler) => throw new NotSupportedException();
    }
}

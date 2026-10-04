using DMCBK.Core.Configuration;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using DMCBK.Testing.Server;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The three client hooks: <see cref="Client.BeforeConnect"/> (with the plan a handler may redirect or veto), <see cref="Client.ConfigurationReloaded"/>, and <see cref="Client.BeforeExit"/>.
/// The redirect and veto are asserted on the address the connection factory was actually asked for, because a plan a handler changed and nothing dialled would be a hook that only looks like it works.
/// </summary>
public sealed class LifecycleHookTests
{
    [Fact]
    public async Task BeforeConnect_Redirect_SendsTheDialToTheOtherServer()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer first = FakeJavaServer.Create();
        await using FakeJavaServer second = FakeJavaServer.Create();
        var connections = new McPluginSessionHarness.RoutingConnectionFactory(new()
        {
            ["test-harness"] = first.ClientPipe,
            ["second-server"] = second.ClientPipe,
        });

        string root = NewRoot("mcc-before-connect");
        Client client = McPluginSessionHarness.BuildClient(
            first, "Tester", root, NullLoggerFactory.Instance, hostInterface: null, connections: connections);
        await using (client.ConfigureAwait(false))
        {
            ConnectPlan? seen = null;
            client.BeforeConnect += (_, plan) =>
            {
                seen = plan;
                plan.Host = "second-server";
                plan.Port = 25566;
            };

            Task starting = client.StartAsync(ct);
            await McPluginSessionHarness.DriveLoginAsync(second, ct);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);

            Assert.NotNull(seen);
            Assert.Equal(0, seen!.Attempt);
            Assert.Null(seen.PreviousDisconnect);
            Assert.Equal("en_us", seen.ClientInformation.Locale);
            Assert.Equal([new Umpk.ServerEndpoint("second-server", 25566)], connections.Dialed);
        }
    }

    [Fact]
    public async Task BeforeConnect_Veto_StopsTheConnect_AndNothingIsDialled()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        var connections = new McPluginSessionHarness.RoutingConnectionFactory(new()
        {
            ["test-harness"] = server.ClientPipe,
        });

        string root = NewRoot("mcc-veto");
        Client client = McPluginSessionHarness.BuildClient(
            server, "Tester", root, NullLoggerFactory.Instance, hostInterface: null, connections: connections);
        await using (client.ConfigureAwait(false))
        {
            client.BeforeConnect += (_, plan) => plan.Veto("not during maintenance");

            DmcbkConnectVetoedException vetoed =
                await Assert.ThrowsAsync<DmcbkConnectVetoedException>(() => client.StartAsync(ct));

            Assert.Equal("not during maintenance", vetoed.Reason);
            Assert.Empty(connections.Dialed);
        }
    }

    /// <summary>
    /// A plugin's goodbye has to reach the server while the session is still live, which means before its own teardown.
    /// The plugin records both moments in order.
    /// </summary>
    [Fact]
    public async Task BeforeExit_RunsBeforeThePluginIsDeactivated()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using FakeJavaServer server = FakeJavaServer.Create();
        string root = NewRoot("mcc-before-exit");
        Client client = McPluginSessionHarness.BuildClient(server, "Tester", root, NullLoggerFactory.Instance);
        string folder;
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(client, root, NullLoggerFactory.Instance, client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            folder = WritePlugin(root, "exit-recorder", "Recorder.cs", ExitRecorderSource);
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);
            await McPluginSessionHarness.DriveLoginAsync(server, ct);
            await starting.WaitAsync(McPluginSessionHarness.Budget, ct);

            await client.StopAsync();
        }

        string storage = Path.Combine(TestPackages.UserFolder(folder), "data", "storage.toml");
        Assert.Equal("before-exit;deactivate", ReadValue(storage, "order"));

        // The deferred work was waited for, not left running: the marker it writes is there.
        Assert.Equal("done", ReadValue(storage, "deferred"));
    }

    /// <summary>
    /// <c>reload</c> reads the file again and hands the new snapshot on, because the one the client was built from never changes and a plugin reading that would answer with the old value forever.
    /// </summary>
    [Fact]
    public async Task ConfigurationReloaded_CarriesTheSnapshotFromDisk()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        string folder = NewRoot("mcc-reload");
        var loader = new DmcbkConfigurationLoader(folder, loggerFactory: NullLoggerFactory.Instance);
        ConfigurationLoadResult loaded = loader.Load(generateMissing: true);

        Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseConfiguration(loaded.Config)
            .UseConfigurationStorage(folder)
            .UseUsername("Tester")
            .Build();

        await using (client.ConfigureAwait(false))
        {
            DmcbkConfiguration? announced = null;
            client.ConfigurationReloaded += (_, e) => announced = e.Current;

            string clientToml = Path.Combine(folder, "client.toml");
            File.WriteAllText(
                clientToml,
                File.ReadAllText(clientToml).Replace("AutoConnect = true", "AutoConnect = false", StringComparison.Ordinal));

            await client.Commands.HandleInputAsync("/reload", ct);

            Assert.NotNull(announced);
            Assert.False(announced!.Connection.AutoConnect);
            Assert.True(client.Configuration!.Connection.AutoConnect);
        }
    }

    /// <summary>
    /// A plugin that writes the order it saw the shutdown in, and defers a piece of work so the wait is exercised as well.
    /// </summary>
    private const string ExitRecorderSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ExitRecorderPlugin : IPlugin
        {
            private PluginContext? _context;

            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "exit-recorder";

            public Task ActivateAsync(PluginContext context)
            {
                _context = context;
                context.BeforeExit += (_, e) =>
                {
                    Append("before-exit");
                    e.Defer(Task.Run(async () =>
                    {
                        await Task.Delay(20).ConfigureAwait(false);
                        context.Storage.Set("deferred", "done");
                        context.Storage.Save();
                    }));
                };
                return Task.CompletedTask;
            }

            public Task DeactivateAsync(CancellationToken ct)
            {
                Append("deactivate");
                return Task.CompletedTask;
            }

            private void Append(string step)
            {
                string current = _context!.Storage.TryGet("order", out string? value) ? value! : string.Empty;
                _context.Storage.Set("order", current.Length == 0 ? step : current + ";" + step);
                _context.Storage.Save();
            }
        }
        """;

    private static string NewRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WritePlugin(string root, string id, string entry, string source)
    {
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, entry), source);
        File.WriteAllText(
            Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\napi-version = \"1.0\"\nenabled = true\n"));
        return folder;
    }

    private static string? ReadValue(string storagePath, string key)
    {
        if (!File.Exists(storagePath))
            return null;

        var store = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(storagePath));
        return store.TryGetValue(key, out string? value) ? value : null;
    }
}

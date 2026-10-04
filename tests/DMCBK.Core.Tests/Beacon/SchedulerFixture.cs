using DMCBK.Core.Beacon;
using DMCBK.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using DMCBK.Testing.Server;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>Inert host double: records nothing, refuses nothing; other tests assert through it.</summary>
internal sealed class BeaconTestHost : IBeaconHostServices
{
    /// <inheritdoc />
    public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public string? SelfName => "Tester";

    /// <inheritdoc />
    public IReadOnlyList<string> OnlinePlayers(int limit) => [];

    /// <inheritdoc />
    public double? ServerTps => null;

    /// <inheritdoc />
    public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// Real <see cref="Client"/> against a <see cref="FakeJavaServer"/> pipe (the <see cref="McPluginSessionHarness"/> pattern) with the Beacon determinism seams injected: the engine under test runs on a <see cref="VirtualClock"/> and a seeded <see cref="SeededRng"/>.
/// Later tests drive scheduler behavior through <see cref="Engine"/> on this same fixture.
/// </summary>
internal sealed class SchedulerFixture : IAsyncDisposable
{
    private bool _disposed;

    private SchedulerFixture(
        FakeJavaServer server,
        Client client,
        VirtualClock clock,
        SeededRng rng,
        BeaconEngine engine,
        string pluginsRoot)
    {
        Server = server;
        Client = client;
        Clock = clock;
        Rng = rng;
        Engine = engine;
        PluginsRoot = pluginsRoot;
    }

    /// <summary>The in-memory server end of the pipe.</summary>
    public FakeJavaServer Server { get; }

    /// <summary>The real client wired to <see cref="Server"/>.</summary>
    public Client Client { get; }

    /// <summary>Virtual time injected into <see cref="Engine"/>.</summary>
    public VirtualClock Clock { get; }

    /// <summary>Seeded RNG injected into <see cref="Engine"/>.</summary>
    public SeededRng Rng { get; }

    /// <summary>The engine under test.</summary>
    public BeaconEngine Engine { get; }

    /// <summary>Temp plugins root backing the client.</summary>
    public string PluginsRoot { get; }

    /// <summary>Builds the pipe, client, and engine triple.</summary>
    public static SchedulerFixture Create(int seed = 1234)
    {
        FakeJavaServer server = FakeJavaServer.Create();
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Client client = McPluginSessionHarness.BuildClient(server, "Tester", root, NullLoggerFactory.Instance);
        var clock = new VirtualClock();
        var rng = new SeededRng(seed);
        var engine = new BeaconEngine(new BeaconTestHost(), clock, rng, new FuelBudget());
        return new SchedulerFixture(server, client, clock, rng, engine, root);
    }

    /// <summary>Starts the client and drives handshake/login/configuration to the start of play.</summary>
    public async Task StartAndLoginAsync(CancellationToken ct)
    {
        Task starting = Client.StartAsync(ct);
        await McPluginSessionHarness.DriveLoginAsync(Server, ct);
        await starting.WaitAsync(McPluginSessionHarness.Budget, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        try
        {
            Directory.Delete(PluginsRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

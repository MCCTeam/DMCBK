using DMCBK.Core.Beacon;

namespace DMCBK.PluginSdk;

/// <summary>
/// The standing <see cref="IBeaconHost"/> for a context created outside activation (hand-built test contexts): every use fails readably instead of nulling.
/// </summary>
internal sealed class NullBeaconHost : IBeaconHost
{
    public static NullBeaconHost Instance { get; } = new();

    private NullBeaconHost()
    {
    }

    public IBeaconFunctionRegistry Functions => NullRegistry.Instance;

    public IBeaconVariableRegistry Variables => NullVariableRegistry.Instance;

    public IDisposable RegisterEvent(
        string name,
        IReadOnlyList<string> fields,
        string description,
        bool suppressible = false,
        string? capability = null)
        => throw new InvalidOperationException(
            "No Beacon runtime is wired to this context. Activate the plugin through the host to use Beacon.");

    public Task<BeaconFireResult> FireEventAsync(
        string name, IReadOnlyDictionary<string, object?> fields, CancellationToken detached = default)
        => throw new InvalidOperationException(
            "No Beacon runtime is wired to this context. Activate the plugin through the host to use Beacon.");

    public Task<object?> CallFunctionAsync(
        string scriptId, string function, IReadOnlyList<object?> args, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "No Beacon runtime is wired to this context. Activate the plugin through the host to use Beacon.");

    private sealed class NullRegistry : IBeaconFunctionRegistry
    {
        public static NullRegistry Instance { get; } = new();

        public IDisposable Register(BeaconFunction function)
            => throw new InvalidOperationException(
                "No Beacon runtime is wired to this context. Activate the plugin through the host to use Beacon.");
    }

    private sealed class NullVariableRegistry : IBeaconVariableRegistry
    {
        public static NullVariableRegistry Instance { get; } = new();

        public IDisposable Register(BeaconVariable variable)
            => throw new InvalidOperationException(
                "No Beacon runtime is wired to this context. Activate the plugin through the host to use Beacon.");
    }
}

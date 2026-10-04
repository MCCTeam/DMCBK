namespace DMCBK.Core.Beacon;

/// <summary>Per-client extension metadata used by execution, linting and custom hook validation.</summary>
public sealed class BeaconEnvironment
{
    private static readonly AsyncLocal<BeaconEnvironment?> Ambient = new();
    internal static BeaconEnvironment Current => Ambient.Value ??= new();
    internal object Gate { get; } = new();
    internal Dictionary<string, BeaconHookSchema> CustomHooks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The providers available to this engine or explicit tooling scope.</summary>
    public BeaconProviderRegistry Providers { get; } = new();

    /// <summary>Makes this environment available to language tooling for the current asynchronous operation.</summary>
    public IDisposable Enter()
    {
        BeaconEnvironment? previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(previous);
    }

    private sealed class Scope(BeaconEnvironment? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

namespace DMCBK.Core.Beacon;

/// <summary>Script integration contract for optional Beacon implementations.</summary>
public interface IBeaconEngine
{
    /// <summary>The per-client extension bridge.</summary>
    IBeaconBridge Bridge { get; }
    /// <summary>Calls an exported script function from a host module.</summary>
    Task<object?> CallExportFromHostAsync(string scriptId, string function, IReadOnlyList<object?> args, CancellationToken ct = default);
}

/// <summary>Plugin extension registration and invocation boundary.</summary>
public interface IBeaconBridge
{
    /// <summary>Registers an extension function.</summary>
    IDisposable RegisterFunction(BeaconExtensionFunction function);
    /// <summary>Registers a variable namespace.</summary>
    IDisposable RegisterVariable(BeaconVariableRegistration variable);
    /// <summary>Registers a custom event.</summary>
    IDisposable RegisterEvent(string owner, string name, IReadOnlyList<string> fields, string description, bool suppressible = false, string? capability = null);
    /// <summary>Fires a custom event.</summary>
    Task<BeaconFireResult> FireEventAsync(string name, IReadOnlyDictionary<string, BeaconValue> fields, CancellationToken ct = default);
    /// <summary>Removes all registrations owned by a plugin.</summary>
    void WithdrawPlugin(string owner);
}

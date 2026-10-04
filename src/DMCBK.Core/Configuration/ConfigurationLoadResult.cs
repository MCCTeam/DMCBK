namespace DMCBK.Core.Configuration;

/// <summary>The result of a load: the immutable snapshot, recoverable warnings, and whether files were generated.</summary>
public sealed record ConfigurationLoadResult(
    MccConfiguration Config,
    IReadOnlyList<ConfigurationWarning> Warnings,
    bool Generated);

/// <summary>Carries the previous and new snapshots when configuration is reloaded.</summary>
public sealed class ConfigurationReloadedEventArgs(MccConfiguration? previous, MccConfiguration current) : EventArgs
{
    /// <summary>The snapshot before the reload, or null if this is the first load.</summary>
    public MccConfiguration? Previous { get; } = previous;

    /// <summary>The snapshot produced by the reload.</summary>
    public MccConfiguration Current { get; } = current;
}

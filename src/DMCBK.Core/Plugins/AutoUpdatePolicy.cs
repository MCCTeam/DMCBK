namespace DMCBK.Core.Plugins;

/// <summary>
/// What a marketplace is allowed to do on its own after plugins have loaded.
/// The words are the ones the user types on <c>plugins marketplace auto-update</c> and the ones written into <c>marketplaces.toml</c>, so they live beside the seam rather than in either implementation.
/// </summary>
public static class AutoUpdatePolicy
{
    /// <summary>Nothing happens unless the user runs <c>outdated</c> or <c>update</c>.</summary>
    public const string Off = "off";

    /// <summary>Refresh and report how many updates are waiting.</summary>
    public const string Check = "check";

    /// <summary>Refresh, stage and swap, immediately or at the end of the live session.</summary>
    public const string Apply = "apply";

    /// <summary>A plugin's lock-file value meaning "whatever its marketplace says".</summary>
    public const string Inherit = "inherit";

    /// <summary>The three a marketplace may carry.</summary>
    public static IReadOnlyList<string> All { get; } = [Off, Check, Apply];

    /// <summary>Normalises a written policy, or null when it is not one of the three.</summary>
    public static string? Normalize(string? value)
        => All.FirstOrDefault(policy => string.Equals(policy, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

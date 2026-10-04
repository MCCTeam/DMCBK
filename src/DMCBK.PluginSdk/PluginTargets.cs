namespace DMCBK.PluginSdk;
/// <summary>The supported process targets for plugin assets.</summary>
public static class PluginTargets
{
    private static readonly HashSet<string> Targets = ["any", "win-x86", "win-x64", "win-arm64",
        "linux-x64", "linux-arm64", "linux-arm", "linux-musl-x64", "linux-musl-arm64", "linux-musl-arm", "osx-x64", "osx-arm64"];
    /// <summary>Whether the target is supported by this schema.</summary>
    public static bool IsSupported(string target) => Targets.Contains(target);
}

using System.Runtime.InteropServices;

namespace DMCBK.PluginSdk;

/// <summary>Detects the running process architecture, operating system and Linux libc.</summary>
public static class PlatformTarget
{
    /// <summary>Returns a supported plugin target for the running process.</summary>
    public static string Detect()
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException(RuntimeInformation.ProcessArchitecture.ToString()),
        };
        bool musl = OperatingSystem.IsLinux() &&
            (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal)
             || File.ReadAllText("/proc/self/maps").Contains("ld-musl-", StringComparison.Ordinal));
        return Resolve(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
            OperatingSystem.IsMacOS() ? "osx" : "unknown", architecture, musl);
    }

    /// <summary>Resolves an explicit process target, rejecting unsupported architecture combinations.</summary>
    public static string Resolve(string operatingSystem, string processArchitecture, bool musl = false)
    {
        string target = operatingSystem + (operatingSystem == "linux" && musl ? "-musl" : "") + "-" + processArchitecture;
        return PluginTargets.IsSupported(target) ? target : throw new PlatformNotSupportedException(target);
    }
}

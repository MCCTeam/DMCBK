using System.Reflection;

namespace DMCBK.PluginSdk;

/// <summary>The explicit contract set shared between plugin load contexts and the host.</summary>
public static class PluginAssemblyPolicy
{
    private static readonly HashSet<string> Contracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DMCBK.Core", "DMCBK.PluginSdk", "DMCBK.Commands", "Brigadier.NET",
        "Microsoft.Extensions.Logging.Abstractions", "Tomlet"
    };

    // SDK packages with System names are private dependencies, not runtime assemblies.
    private static readonly HashSet<string> PrivateSdkAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.ClientModel", "System.Memory.Data"
    };

    private static readonly HashSet<string> SharedFramework = new(
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.Replace('\\', '/').Contains("/shared/Microsoft.AspNetCore.App/", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an assembly belongs to the framework or public host contracts.</summary>
    public static bool IsShared(string name, bool includeAspNetCore = false) => Contracts.Contains(name)
        || (includeAspNetCore && (SharedFramework.Contains(name)
            || name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.JSInterop", StringComparison.Ordinal)
            || name == "Microsoft.Net.Http.Headers"))
        || name.StartsWith("Umpk.", StringComparison.OrdinalIgnoreCase)
        || name == "System" || (name.StartsWith("System.", StringComparison.Ordinal) && !PrivateSdkAssemblies.Contains(name))
        || name is "netstandard" or "mscorlib" or "Microsoft.CSharp";

    /// <summary>Rejects bundled host contracts instead of silently accepting duplicate identities.</summary>
    public static void ValidatePackage(string folder, bool includeAspNetCore = false)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            AssemblyName identity;
            try { identity = AssemblyName.GetAssemblyName(path); }
            catch (BadImageFormatException) { continue; } // A packaged native library has no managed identity.
            if (identity.Name is { } name && IsShared(name, includeAspNetCore))
                throw new InvalidDataException("plugin.duplicate-host-contract:" + name);
        }
    }
}


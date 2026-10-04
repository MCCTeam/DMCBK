using System.Reflection;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The embeddability law: Mcc.Core and DMCBK.PluginSdk must not drag in console, UI, web, or the legacy client.
/// This walks the referenced-assembly graph from both roots and asserts none of the banned assemblies appear.
/// The embeddability-proof host (Mcc.HostSample) is walked as a third root so its build stays a CI gate against a banned reference sneaking into the sample host too.
/// </summary>
public sealed class DependencyGuardTests
{
    [Fact]
    public void CoreAndPluginSdk_DoNotReference_BannedAssemblies()
    {
        Assembly[] roots =
        [
            typeof(Client).Assembly,
            typeof(IPlugin).Assembly,
        ];

        HashSet<string> reachable = WalkReferences(roots);

        foreach (string name in reachable)
            Assert.False(IsBanned(name), $"Mcc.Core/DMCBK.PluginSdk transitively reference banned assembly '{name}'.");
    }

    [Fact]
    public void OptionalRuntimePackages_DoNotReference_BannedAssemblies()
    {
        // The embeddability proof: the worker-style host references only Mcc.Core, so its closure must be exactly the core's closure - no console/UI library dragged in by embedding the client.
        HashSet<string> reachable = WalkReferences([typeof(PluginHost).Assembly, typeof(MarketplaceService).Assembly, typeof(DMCBK.Testing.PluginTestHost).Assembly, typeof(DMCBK.Core.Beacon.BeaconEngine).Assembly]);

        foreach (string name in reachable)
            Assert.False(IsBanned(name), $"Mcc.HostSample transitively references banned assembly '{name}'.");
    }

    private static bool IsBanned(string assemblyName)
        => string.Equals(assemblyName, "ConsoleInteractive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(assemblyName, "MinecraftClient", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Consolonia", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> WalkReferences(IEnumerable<Assembly> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();

        foreach (Assembly root in roots)
        {
            if (seen.Add(root.GetName().Name ?? root.FullName ?? "<unknown>"))
                queue.Enqueue(root);
        }

        while (queue.Count > 0)
        {
            Assembly current = queue.Dequeue();
            foreach (AssemblyName reference in current.GetReferencedAssemblies())
            {
                string name = reference.Name ?? reference.FullName;
                if (!seen.Add(name))
                    continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // The name is still recorded in 'seen' for the ban check; we just cannot recurse into it.
                }
            }
        }

        return seen;
    }
}

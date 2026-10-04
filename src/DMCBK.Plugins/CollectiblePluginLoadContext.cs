using System.Reflection;
using System.Runtime.Loader;

namespace DMCBK.PluginSdk;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> for one plugin, so a plugin (compiled or compiled-from-.cs) can be unloaded and its assembly reclaimed.
/// Shared framework/host assemblies (DMCBK.PluginSdk, DMCBK.Core, Umpk.*, the BCL) are deliberately NOT loaded into this context: the resolver returns null for anything the default context already provides, so plugin and host see the SAME contract types (a plugin's <c>IPlugin</c> unifies with the host's).
/// Only the plugin's own entry assembly and its declared <c>deps</c> load here.
/// <para>
/// The one other delegation is <c>[exports]</c>.
/// An assembly exported by a plugin in this one's <c>[requires]</c> resolves in that plugin's context rather than being loaded again here, so a type declared in one plugin can be handed to the other.
/// </para>
/// </summary>
internal sealed class CollectiblePluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly bool _shareAspNetCore;
    private readonly Dictionary<string, string> _extraAssemblies;
    private readonly Dictionary<string, PluginExportedAssembly> _exports;

    internal CollectiblePluginLoadContext(
        string name,
        string? entryAssemblyPath,
        IEnumerable<string> depPaths,
        IEnumerable<PluginExportedAssembly>? exports = null, bool includeAspNetCore = false)
        : base(name, isCollectible: true)
    {
        _shareAspNetCore = includeAspNetCore;
        _resolver = entryAssemblyPath is not null ? new AssemblyDependencyResolver(entryAssemblyPath) : null;
        _extraAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dep in depPaths)
        {
            string simpleName = Path.GetFileNameWithoutExtension(dep);
            if (!string.IsNullOrEmpty(simpleName))
                _extraAssemblies[simpleName] = dep;
        }

        _exports = new Dictionary<string, PluginExportedAssembly>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginExportedAssembly export in exports ?? [])
            _exports[export.Name] = export;
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Prefer the shared/default context for any assembly already loaded there (host + framework + Umpk), so plugin types unify with host types.
        // Returning null delegates to the default context.
        if (assemblyName.Name is { } name)
        {
            if (PluginAssemblyPolicy.IsShared(name, _shareAspNetCore))
                return Default.LoadFromAssemblyName(assemblyName);

            // Resolved in the exporter's context, not here.
            // A second copy loaded into this one would be a second set of types that do not unify with the exporter's.
            if (_exports.TryGetValue(name, out PluginExportedAssembly? export))
                return export.Owner.LoadFromAssemblyName(assemblyName);

            if (_extraAssemblies.TryGetValue(name, out string? depPath) && File.Exists(depPath))
                return LoadFromAssemblyPath(Path.GetFullPath(depPath));
        }

        string? resolved = _resolver?.ResolveAssemblyToPath(assemblyName);
        return resolved is not null ? LoadFromAssemblyPath(resolved)
            : throw new FileNotFoundException("plugin.private-dependency-missing", assemblyName.FullName);
    }
    /// <inheritdoc/>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

namespace DMCBK.PluginSdk;

/// <summary>Supplies explicit on-disk compilation reference assets, including single-file host references.</summary>
public interface ICompilationReferenceProvider
{
    /// <summary>Gets reference paths whose contents participate in the compilation cache key.</summary>
    IReadOnlyList<string> GetReferencePaths();
}

/// <summary>References a normal managed deployment without examining loaded application assemblies.</summary>
public sealed class CompilationReferenceProvider : ICompilationReferenceProvider
{
    private readonly string[] _references;

    /// <summary>Creates a provider using explicit host assemblies, optional reference assets and an explicitly provided ASP.NET Core framework.</summary>
    public CompilationReferenceProvider(string assemblyDirectory, IEnumerable<string>? additionalReferences = null, bool includeAspNetCore = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);
        IEnumerable<string> platform = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => PluginAssemblyPolicy.IsShared(Path.GetFileNameWithoutExtension(path), includeAspNetCore));
        IEnumerable<string> contracts = Directory.Exists(assemblyDirectory)
            ? Directory.EnumerateFiles(assemblyDirectory, "*.dll")
                .Where(path => PluginAssemblyPolicy.IsShared(Path.GetFileNameWithoutExtension(path), includeAspNetCore)) : [];
        _references = platform.Concat(contracts).Concat(additionalReferences ?? [])
            .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_references.Any(path => !File.Exists(path))) throw new FileNotFoundException("plugin.compilation-reference-missing");
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetReferencePaths() => Array.AsReadOnly(_references);
}

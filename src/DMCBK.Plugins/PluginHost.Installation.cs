using DMCBK.Core.Plugins;

namespace DMCBK.PluginSdk;

/// <summary>Integrates immutable installation selections with the existing plugin lifecycle.</summary>
public sealed partial class PluginHost : IManagedPluginHost
{
    private readonly HashSet<string> _managedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads exact startup selections from an attached marketplace after transaction recovery.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<PluginInstallation>>>? InstallationSource { get; set; }

    /// <summary>Persists enabled-state changes through the marketplace transaction engine.</summary>
    public Func<string, bool, CancellationToken, Task<PluginActionResult>>? ChangeEnabled { get; set; }

    /// <summary>Explicit local development folders loaded at startup. No legacy marketplace discovery occurs.</summary>
    public IList<string> DevelopmentPluginFolders { get; } = new List<string>();

    /// <inheritdoc/>
    public ValueTask PrepareAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken)
    {
        var exports = new Dictionary<string, IReadOnlyList<PluginExportedAssembly>>(StringComparer.OrdinalIgnoreCase);
        var contexts = new List<CollectiblePluginLoadContext>();
        try
        {
            foreach (PluginInstallation package in graph)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PluginManifest manifest = package.Manifest;
                IReadOnlyList<PluginCompatibilityFailure> failures = PluginCompatibility.Check(manifest.ApiVersion,
                    manifest.Dmcbk, manifest.Umpk, manifest.Framework, manifest.Needs, manifest.Hosts, HostInfo.FromClient(_client), manifest.Target);
                if (failures.Count != 0) throw new InvalidDataException(PluginStrings.Compatibility(failures[0]));
                PluginAssemblyPolicy.ValidatePackage(package.PackageDirectory, manifest.Needs.Contains("aspnetcore"));
                List<string> dependencies = manifest.Deps.Select(path => Path.Combine(package.PackageDirectory, path)).ToList();
                PluginExportedAssembly[] imported = manifest.Requires.Keys.Where(exports.ContainsKey).SelectMany(id => exports[id]).ToArray();
                byte[]? image = null;
                string entry = Path.Combine(package.PackageDirectory, manifest.Entry);
                if (manifest.IsSourceEntry)
                {
                    CsPluginCompiler.CompileResult compiled = CsPluginCompiler.Compile(entry, dependencies, imported,
                        CompilationReferences, Path.Combine(_pluginsRoot, "cache", "source", manifest.Id));
                    if (!compiled.Success) throw new PluginCompileException(compiled.Error ?? "plugin.compilation-failed");
                    image = compiled.Assembly;
                }
                var context = new CollectiblePluginLoadContext("prepare:" + manifest.Id, image is null ? entry : null, dependencies, imported, manifest.Needs.Contains("aspnetcore"));
                contexts.Add(context);
                System.Reflection.Assembly assembly;
                if (image is not null) { using var stream = new MemoryStream(image); assembly = context.LoadFromStream(stream); }
                else assembly = context.LoadFromAssemblyPath(Path.GetFullPath(entry));
                if (FindPluginType(assembly) is null) throw new InvalidDataException("plugin.entry-contract-missing");
                var record = new LoadedPlugin(manifest, package.PackageDirectory,
                    manifest.IsSourceEntry ? PluginEntryKind.Source : PluginEntryKind.Compiled);
                exports[manifest.Id] = ResolveExports(record, context, assembly, entry, image);
            }
        }
        finally { foreach (CollectiblePluginLoadContext context in contexts.AsEnumerable().Reverse()) context.Unload(); }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DeactivateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        foreach (string id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Find(id) is not { } record) continue;
            if (record.Loaded) await UnloadRecordAsync(record).ConfigureAwait(false);
            lock (_gate) _plugins.Remove(id);
        }
    }

    /// <inheritdoc/>
    public ValueTask ActivateAsync(IReadOnlyList<PluginInstallation> graph, CancellationToken cancellationToken)
        => ActivateCoreAsync(graph, strict: true, cancellationToken);

    private async ValueTask ActivateCoreAsync(IReadOnlyList<PluginInstallation> graph, bool strict, CancellationToken cancellationToken)
    {
        var records = new List<LoadedPlugin>();
        foreach (PluginInstallation package in graph)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedPlugin record = RegisterRecord(new(package.Manifest, package.PackageDirectory,
                package.Manifest.IsSourceEntry ? PluginEntryKind.Source : PluginEntryKind.Compiled));
            if (!Path.GetFullPath(record.Folder).Equals(Path.GetFullPath(package.PackageDirectory), StringComparison.Ordinal))
                throw new InvalidOperationException("plugin.active-selection-conflict");
            record.UserDirectory = package.UserDirectory;
            record.Enabled = package.Enabled;
            _managedIds.Add(package.Manifest.Id);
            records.Add(record);
        }
        foreach (LoadedPlugin record in records)
        {
            if (!record.Enabled) { record.Status = PluginStrings.StateDisabled; continue; }
            if (record.Loaded) continue;
            PluginActionResult result = await LoadRecordAsync(record, cancellationToken).ConfigureAwait(false);
            if (!result.Success && strict) throw new InvalidOperationException(result.Message);
        }
    }
}

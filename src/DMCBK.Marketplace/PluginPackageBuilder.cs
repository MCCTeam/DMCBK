using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DMCBK.PluginSdk;
using Tomlet;

namespace DMCBK.Marketplace;

/// <summary>The deterministic package and generated release metadata.</summary>
/// <param name="ArchivePath">The archive location.</param>
/// <param name="Manifest">The exact selected asset manifest.</param>
/// <param name="Release">The generated catalogue release.</param>
public sealed record PackedPlugin(string ArchivePath, PluginManifest Manifest, PluginRelease Release);

/// <summary>Builds archives and catalogue entries from the runtime's shared v2 models.</summary>
public static class PluginPackageBuilder
{
    /// <summary>Packs one declared asset; binaries are excluded from Git and host contracts from the archive.</summary>
    public static PackedPlugin Pack(string pluginFolder, string payloadFolder, string outputFolder,
        string kind, string target, Uri releaseBaseUrl, IEnumerable<string>? providedAssemblies = null, string? entry = null)
    {
        ArgumentNullException.ThrowIfNull(releaseBaseUrl);
        if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(pluginFolder, PluginManifest.FileName)), out PluginManifest manifest, out string? error))
            throw new MarketplaceException("pack.manifest-invalid", error ?? string.Empty);
        if (kind is not ("source" or "compiled") || !PluginTargets.IsSupported(target)
            || releaseBaseUrl.Scheme is not ("https" or "http")) throw new MarketplaceException("pack.asset-invalid", kind, target);
        manifest.Kind = kind; manifest.Target = target;
        if (entry is not null)
        {
            if (!PluginManifest.IsPackagePath(entry)) throw new MarketplaceException("pack.path-invalid", entry);
            manifest.Entry = entry;
        }
        if (manifest.IsSourceEntry != (kind == "source")) throw new MarketplaceException("pack.entry-kind", manifest.Entry, kind);
        string entryPath = Path.Combine(payloadFolder, manifest.Entry);
        if (!File.Exists(entryPath)) throw new MarketplaceException("pack.entry-missing", manifest.Entry);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var excluded = (providedAssemblies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var privateAssemblies = new List<string>();
        foreach (string path in Directory.EnumerateFiles(payloadFolder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(payloadFolder, path).Replace('\\', '/');
            if (!PluginManifest.IsPackagePath(relative) || relative.Split('/').Any(part => part is "obj" or "bin" or "data" || part.StartsWith('.'))) continue;
            if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string? identity = AssemblyName.GetAssemblyName(path).Name;
                    if (identity is not null && (PluginAssemblyPolicy.IsShared(identity, manifest.Needs.Contains("aspnetcore")) || excluded.Contains(identity))) continue;
                    if (relative != manifest.Entry) privateAssemblies.Add(relative);
                }
                catch (BadImageFormatException) { /* Native dependencies remain at their declared relative locations. */ }
            }
            else if (kind == "compiled" && !relative.EndsWith(".deps.json", StringComparison.Ordinal)
                && !relative.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)
                && Path.GetExtension(path) is not (".so" or ".dylib")) continue;
            else if (kind == "source" && relative != manifest.Entry
                && !manifest.Deps.Contains(relative) && !manifest.Exports.Assemblies.Contains(relative)) continue;
            Add(relative, File.ReadAllBytes(path));
        }
        // Localization, manuals and package defaults accompany every asset.
        foreach (string assetDirectory in new[] { "lang", "man", "defaults" })
        {
            string directory = Path.Combine(pluginFolder, assetDirectory);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                Add(Path.GetRelativePath(pluginFolder, path).Replace('\\', '/'), File.ReadAllBytes(path));
        }
        foreach (string document in new[] { "LICENSE", "LICENSE.md", "LICENSE.txt", "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.txt" })
        {
            string licensePath = Path.Combine(pluginFolder, document);
            if (File.Exists(licensePath)) Add(document, File.ReadAllBytes(licensePath));
        }
        string defaultSettings = Path.Combine(pluginFolder, "settings.toml");
        if (File.Exists(defaultSettings)) Add("defaults/settings.toml", File.ReadAllBytes(defaultSettings));
        if (kind == "compiled") manifest.Deps = privateAssemblies.Order(StringComparer.Ordinal).ToList();
        foreach (string required in manifest.Deps.Append(manifest.Entry).Concat(manifest.Exports.Assemblies.Where(name => name != PluginExports.EntryToken)))
            if (!files.ContainsKey(required)) throw new MarketplaceException("pack.required-file-missing", required);
        string manifestText = TomletMain.TomlStringFrom(manifest);
        if (!PluginManifest.TryParse(manifestText, out manifest, out error)) throw new MarketplaceException("pack.generated-manifest-invalid", error ?? string.Empty);
        Add(PluginManifest.FileName, Encoding.UTF8.GetBytes(manifestText));
        Directory.CreateDirectory(outputFolder);
        string name = $"{manifest.Id}-{manifest.Version}-{kind}-{target}.zip";
        string output = Path.Combine(outputFolder, name);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach ((string relative, byte[] bytes) in files)
                {
                    ZipArchiveEntry archiveEntry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    archiveEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    archiveEntry.ExternalAttributes = 0x81a4 << 16;
                    using Stream stream = archiveEntry.Open(); stream.Write(bytes);
                }
            string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(temporary)));
            if (File.Exists(output))
            {
                if (!Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output))).Equals(digest, StringComparison.Ordinal))
                    throw new MarketplaceException("pack.immutable-release-conflict", manifest.Id, manifest.Version);
                File.Delete(temporary);
            }
            else File.Move(temporary, output);
            var release = new PluginRelease
            {
                Version = manifest.Version,
                ApiVersion = manifest.ApiVersion,
                Dmcbk = manifest.Dmcbk,
                Umpk = manifest.Umpk,
                Framework = manifest.Framework,
                Needs = [.. manifest.Needs],
                Requires = new(manifest.Requires),
                Optional = new(manifest.Optional),
                Hosts = new(manifest.Hosts),
                Assets = [new() { Kind = kind, Target = target, Url = new Uri(releaseBaseUrl, name).AbsoluteUri, Sha256 = digest }]
            };
            release.Validate(manifest.Id);
            File.WriteAllText(output + ".sha256", digest + "  " + name + "\n", new UTF8Encoding(false));
            string catalogue = TomletMain.TomlStringFrom(new ReleaseCatalogue { SchemaVersion = 2, Id = manifest.Id, Releases = [release] });
            _ = ReleaseCatalogue.Parse(catalogue);
            File.WriteAllText(Path.Combine(outputFolder, name + ".catalogue.toml"), catalogue, new UTF8Encoding(false));
            return new(output, manifest, release);
        }
        finally { File.Delete(temporary); }

        void Add(string relative, byte[] bytes)
        {
            if (!PluginManifest.IsPackagePath(relative)) throw new MarketplaceException("pack.path-invalid", relative);
            if (files.Keys.Any(existing => existing.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new MarketplaceException("pack.path-duplicate", relative);
            string path = Path.Combine(pluginFolder, relative);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new MarketplaceException("pack.storage-link", relative);
            files.Add(relative, bytes);
        }
    }
}

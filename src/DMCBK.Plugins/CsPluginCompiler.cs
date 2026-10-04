using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DMCBK.PluginSdk;

/// <summary>
/// Compiles a single-file <c>.cs</c> plugin entry into an in-memory assembly with Roslyn.
/// The source must declare a real <see cref="IPlugin"/> type, so compiler diagnostics carry the true file and line with no
/// <c>#line</c> remapping. An external content-hash cache skips recompilation of
/// unchanged sources; <c>deps</c> add extra assembly references, which covers what <c>//dll</c> covered.
/// References resolve through an explicit reference provider, which fits a normal apphost plus assemblies deployment.
/// </summary>
internal static class CsPluginCompiler
{
    /// <summary>
    /// The conditional-compilation symbols every single-file plugin is compiled with: <c>DMCBK_API_1_0</c>, <c>DMCBK_API_1_1</c>, ... one per minor from 0 up to the host's current one, and an <c>_OR_GREATER</c> form of each in the style of .NET's own <c>NET8_0_OR_GREATER</c>.
    /// One source file can then use a member added in a later minor under <c>#if DMCBK_API_1_1</c> and still load on an older host, which is the only version-adaptation tool a distributed <c>.cs</c> file has.
    /// <para>
    /// Both forms are defined for every shipped minor, so the two spellings behave identically today.
    /// They diverge the moment a plugin is compiled by a host with a HIGHER minor than the one it names: there <c>DMCBK_API_1_1</c> is still defined (this host has 2.1) and so is <c>DMCBK_API_1_1_OR_GREATER</c>.
    /// The <c>_OR_GREATER</c> spelling is the one to reach for, because it reads as the question being asked.
    /// </para>
    /// </summary>
    private static readonly string[] ApiSymbols = BuildApiSymbols();

    /// <summary>The result of a compile attempt.</summary>
    internal sealed record CompileResult(bool Success, byte[]? Assembly, string? Error, bool FromCache)
    {
        internal static CompileResult Compiled(byte[] assembly) => new(true, assembly, null, false);

        internal static CompileResult Cached(byte[] assembly) => new(true, assembly, null, true);

        internal static CompileResult Failure(string error) => new(false, null, error, false);
    }

    internal static CompileResult Compile(
        string csPath,
        IReadOnlyList<string> depFullPaths,
        IReadOnlyList<PluginExportedAssembly>? exports = null,
        ICompilationReferenceProvider? referenceProvider = null,
        string? cacheRoot = null)
    {
        exports ??= [];
        string source = File.ReadAllText(csPath);
        referenceProvider ??= new CompilationReferenceProvider(AppContext.BaseDirectory);
        IReadOnlyList<string> referencePaths = referenceProvider.GetReferencePaths();
        string hash = ComputeHash(source, referencePaths.Concat(depFullPaths).ToArray(), exports);
        string cacheDir = cacheRoot ?? Path.Combine(Path.GetTempPath(), "dmcbk", "plugin-source-cache");
        string cacheFile = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(csPath)}.{hash}.dll");

        if (File.Exists(cacheFile))
            return CompileResult.Cached(File.ReadAllBytes(cacheFile));

        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: ApiSymbols),
            path: Path.GetFullPath(csPath));

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            // Plugin authoring is not held to the host's TreatWarningsAsErrors bar; only errors fail the load.
            reportSuppressedDiagnostics: false, deterministic: true);

        CSharpCompilation compilation = CSharpCompilation.Create(
            $"Plugin_{Path.GetFileNameWithoutExtension(csPath)}_{hash}",
            [tree],
            GatherReferences(referencePaths.Concat(depFullPaths).ToArray(), exports),
            options);

        using var stream = new MemoryStream();
        EmitResult emit = compilation.Emit(stream);
        if (!emit.Success)
            return CompileResult.Failure(FormatDiagnostics(emit.Diagnostics));

        byte[] bytes = stream.ToArray();
        try
        {
            Directory.CreateDirectory(cacheDir);
            string temporary = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, cacheFile, overwrite: true); }
            finally { File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A non-writable folder just means no cache; the compile still succeeded.
            _ = ex;
        }

        return CompileResult.Compiled(bytes);
    }

    /// <summary>
    /// How many compiled images in a plugin's cache belong to a source it no longer has.
    /// The cache is keyed by content hash and nothing deletes the old entry, so every edit to a single-file plugin leaves its predecessor behind.
    /// </summary>
    internal static (int Count, string Folder) StaleOutputs(string cacheRoot, bool sourceEntry)
    {
        string cacheDir = Path.GetFullPath(cacheRoot);
        if (!Directory.Exists(cacheDir))
            return (0, cacheDir);

        string[] images = Directory.GetFiles(cacheDir, "*.dll");
        int keep = sourceEntry && images.Length > 0 ? 1 : 0;
        return (Math.Max(0, images.Length - keep), cacheDir);
    }

    private static string[] BuildApiSymbols()
    {
        var symbols = new List<string>((PluginApiVersion.Minor + 1) * 2);
        for (int minor = 0; minor <= PluginApiVersion.Minor; minor++)
        {
            string name = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"DMCBK_API_{PluginApiVersion.Major}_{minor}");
            symbols.Add(name);
            symbols.Add(name + "_OR_GREATER");
        }

        return [.. symbols];
    }

    private static IReadOnlyList<MetadataReference> GatherReferences(
        IReadOnlyList<string> depFullPaths, IReadOnlyList<PluginExportedAssembly> exports)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dep in depFullPaths)
        {
            if (File.Exists(dep) && seen.Add(dep))
                references.Add(MetadataReference.CreateFromFile(dep));
        }

        // What the plugins in [requires] export.
        // A single-file exporter has no file to reference, only the image Roslyn emitted for it, which is exactly why the export carries one.
        foreach (PluginExportedAssembly export in exports)
        {
            if (export.Image is { Length: > 0 } image)
                references.Add(MetadataReference.CreateFromImage(image));
            else if (export.Path is { Length: > 0 } path && File.Exists(path) && seen.Add(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var builder = new StringBuilder();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
                continue;

            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            string file = string.IsNullOrEmpty(span.Path) ? "<plugin>" : Path.GetFileName(span.Path);
            int line = span.StartLinePosition.Line + 1;
            int column = span.StartLinePosition.Character + 1;
            builder.Append(file).Append('(').Append(line).Append(',').Append(column).Append("): ")
                .Append(diagnostic.Id).Append(": ")
                .AppendLine(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString().TrimEnd();
    }

    private static string ComputeHash(
        string source, IReadOnlyList<string> depFullPaths, IReadOnlyList<PluginExportedAssembly> exports)
    {
        var builder = new StringBuilder(source);
        builder.Append("\0api=").Append(PluginApiVersion.Current)
            .Append("\0compiler=").Append(typeof(CSharpCompilation).Assembly.GetName().Version)
            .Append("\0options=release;unsafe=false;deterministic=true;language=latest;")
            .AppendJoin(';', ApiSymbols);
        foreach (string dep in depFullPaths)
            builder.Append('\0').Append(Path.GetFileName(dep)).Append(':')
                .Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dep))));

        // The exporters' CONTENT, not just their names: a rebuilt dependency changes what this source compiles against, and a cache keyed on the name alone would hand back an assembly built against the old one.
        foreach (PluginExportedAssembly export in exports)
            builder.Append(" exp=").Append(export.Name).Append('/').Append(ContentHash(export));

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static string ContentHash(PluginExportedAssembly export)
    {
        try
        {
            byte[]? bytes = export.Image ?? (export.Path is { Length: > 0 } path && File.Exists(path)
                ? File.ReadAllBytes(path)
                : null);
            return bytes is null ? "missing" : Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as unchanged: a hash that cannot be taken must not collide with one that could, so the cache misses rather than guessing.
            return Guid.NewGuid().ToString("N");
        }
    }
}

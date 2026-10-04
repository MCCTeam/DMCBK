using System.Security.Cryptography;
using System.Text;

namespace DMCBK.Core.Beacon;

/// <summary>Thrown when an import cannot be resolved, read, or parsed (codes B1004/B1005).</summary>
public sealed class BeaconImportException : Exception
{
    /// <summary>Builds an import failure with its stable diagnostic code.</summary>
    public BeaconImportException(string code, string message, string? suggestion)
        : base(message)
    {
        Code = code;
        Suggestion = suggestion;
    }

    /// <summary>The stable diagnostic code (B1004 not found, B1005 cycle, B0001 parse).</summary>
    public string Code { get; }

    /// <summary>The paste-ready fix, when there is one.</summary>
    public string? Suggestion { get; }
}

/// <summary>Resolves one import to its parsed script (file-backed or in-memory for tests).</summary>
public interface IBeaconModuleResolver
{
    /// <summary>
    /// Resolves <paramref name="import"/> (relative to <paramref name="importingFile"/>) with <paramref name="chain"/> holding the files already open above it (cycle detection).
    /// </summary>
    Task<BeaconScript> ResolveAsync(
        string importingFile, BeaconImport import, IReadOnlyList<string> chain, CancellationToken ct = default);
}

/// <summary>
/// The import loader: relative <c>import "lib/x.bcn" as ns</c> resolves against the importing
/// file and stays inside its folder (like the lint closure, so runtime and lint never disagree);
/// parsed files are cached by content hash (like the single-file plugin cache); a circular chain fails with an error naming every file in the cycle.
/// One level of namespacing: the importing script calls <c>ns.func()</c>; inside a module body its own functions are visible bare (the module context), while the importer must still qualify them; nested imports merge under the same alias in file order so a library file stays self-contained.
/// </summary>
public sealed class BeaconImportLoader : IBeaconModuleResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BeaconScript> _byHash = new(StringComparer.Ordinal);

    /// <summary>
    /// In-memory overlay for tests: absolute path (or file name) to source.
    /// Checked before disk.
    /// </summary>
    public Dictionary<string, string> Overlay { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<BeaconScript> ResolveAsync(
        string importingFile, BeaconImport import, IReadOnlyList<string> chain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importingFile);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(chain);
        _ = ct;
        return Task.FromResult(Resolve(importingFile, import, chain.ToList()));
    }

    private BeaconScript Resolve(string importingFile, BeaconImport import, List<string> chain)
    {
        string? absolute = ResolveInside(importingFile, import.Path);
        if (absolute is null)
        {
            throw new BeaconImportException(
                BeaconDiagnosticCodes.ImportNotFound,
                $"Import '{import.Path}' leaves the script folder. Import paths resolve relative to the importing file and stay inside its folder.",
                $"Move the library under '{Path.GetDirectoryName(importingFile)}' and import it by relative path.");
        }

        int cycleAt = chain.FindIndex(p => string.Equals(p, absolute, StringComparison.Ordinal));
        if (cycleAt >= 0)
        {
            string cycle = string.Join(" -> ", chain.Skip(cycleAt).Concat([absolute]).Select(Path.GetFileName));
            throw new BeaconImportException(
                BeaconDiagnosticCodes.ImportCycle,
                $"Circular import: {cycle}.",
                "Break the cycle by moving the shared code into a third file both import.");
        }

        string source;
        if (!Overlay.TryGetValue(absolute, out string? overlaid) || overlaid is null)
        {
            string byName = Path.GetFileName(absolute);
            if (Overlay.TryGetValue(byName, out string? named) && named is not null)
                source = named;
            else
            {
                try
                {
                    source = File.ReadAllText(absolute);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new BeaconImportException(
                        BeaconDiagnosticCodes.ImportNotFound,
                        $"Import '{import.Path}' cannot be read ('{absolute}': {ex.Message}). Loading fails closed: without the file its permissions cannot join the union.",
                        $"Check that '{import.Path}' exists next to '{Path.GetFileName(importingFile)}'.");
                }
            }
        }
        else
            source = overlaid;

        BeaconScript parsed = ParseCached(absolute, source);
        chain.Add(absolute);
        try
        {
            foreach (BeaconImport nested in parsed.Imports)
                Resolve(absolute, nested, chain);
        }
        finally
        {
            chain.RemoveAt(chain.Count - 1);
        }

        return parsed;
    }

    /// <summary>Loads the transitive import closure for lint-union parity (file names in visit order).</summary>
    public IReadOnlyList<BeaconScript> LoadClosure(string importingFile, BeaconScript root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importingFile);
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<BeaconScript>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        LoadClosureInto(importingFile, root, [importingFile], found, seen);
        return found;
    }

    private void LoadClosureInto(
        string importingFile, BeaconScript script, List<string> chain,
        List<BeaconScript> found, HashSet<string> seen)
    {
        foreach (BeaconImport import in script.Imports)
        {
            BeaconScript child = Resolve(importingFile, import, chain);
            string? absolute = ResolveInside(importingFile, import.Path);
            if (absolute is not null && seen.Add(absolute))
            {
                found.Add(child);
                LoadClosureInto(absolute, child, chain, found, seen);
            }
        }
    }

    /// <summary>Resolves <paramref name="relative"/> against <paramref name="importingFile"/>, fenced to its folder.</summary>
    public static string? ResolveInside(string importingFile, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importingFile);
        ArgumentNullException.ThrowIfNull(relative);
        try
        {
            if (Path.IsPathRooted(relative))
                return null;

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(importingFile)) ?? ".";
            string absolute = Path.GetFullPath(Path.Combine(baseDir, relative));
            string fence = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(fence, StringComparison.Ordinal)
                && !string.Equals(absolute, baseDir, StringComparison.Ordinal))
                return null;

            return absolute;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private BeaconScript ParseCached(string absolute, string source)
    {
        string hash = ContentHash(source);
        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out BeaconScript? cached) && cached is not null)
                return cached;
        }

        string display = Path.GetFileName(absolute);
        BeaconHeaderResult header = BeaconPipeline.LexAndParseHeader(
            display, source, out BeaconLexResult lexed, out _);
        if (!header.Ok)
        {
            throw new BeaconImportException(
                BeaconDiagnosticCodes.Parse,
                $"Import '{display}' fails its header gate: {header.Diagnostics.FirstOrDefault()?.Message ?? "missing # beacon 1"}.",
                "Start the library with '# beacon 1'.");
        }

        BeaconParseResult parsed = BeaconParser.Parse(display, lexed.Tokens, header.Major);
        if (parsed.Script is null)
        {
            throw new BeaconImportException(
                BeaconDiagnosticCodes.Parse,
                $"Import '{display}' does not parse: {parsed.Diagnostics.FirstOrDefault()?.Message ?? "syntax error"}.",
                "Run lint on the library file and fix the B0001 findings.");
        }

        IReadOnlyList<BeaconDiagnostic> errors = parsed.Diagnostics.Where(d => d.Severity == BeaconSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new BeaconImportException(
                errors[0].Code,
                $"Import '{display}' is not clean: {errors[0].Message}",
                errors[0].Suggestion);
        }

        BeaconScript desugared = BeaconDesugar.Desugar(parsed.Script);
        lock (_gate)
        {
            _byHash[hash] = desugared;
            return desugared;
        }
    }

    private static string ContentHash(string source)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes);
    }
}

using System.Runtime.Loader;

namespace DMCBK.PluginSdk;

/// <summary>
/// One assembly a loaded plugin exports to its dependents (<c>[exports] assemblies</c>).
/// A dependent needs two things from it: the owner's load context, to resolve it at runtime, and a file path or image, to compile against it.
/// A single-file plugin's Roslyn output has no file, hence the image.
/// </summary>
/// <param name="Name">The assembly's simple name, as an assembly reference names it.</param>
/// <param name="Path">The file the assembly was loaded from, or null for a compiled-in-memory entry.</param>
/// <param name="Image">The assembly image, for a compiled-in-memory entry; null when <paramref name="Path"/> is set.</param>
/// <param name="Owner">The exporting plugin's load context, which is where a dependent must resolve it.</param>
/// <param name="OwnerId">The exporting plugin's id, for diagnostics.</param>
internal sealed record PluginExportedAssembly(
    string Name,
    string? Path,
    byte[]? Image,
    AssemblyLoadContext Owner,
    string OwnerId);

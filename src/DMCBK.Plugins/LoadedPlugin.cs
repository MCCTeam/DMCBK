using System.Runtime.Loader;
using DMCBK.Core.Plugins;

namespace DMCBK.PluginSdk;

/// <summary>
/// The host's live record for one discovered plugin: its manifest/folder, and (when loaded) its instance, context, collectible load context, and the per-plugin services the host must dispose on unload.
/// A discovered-but-not-loaded plugin (disabled, failed, or refused) keeps only the manifest, entry kind, and a status line.
/// </summary>
internal sealed class LoadedPlugin
{
    internal LoadedPlugin(PluginManifest manifest, string folder, PluginEntryKind entryKind)
    {
        Manifest = manifest;
        Folder = folder;
        EntryKind = entryKind;
        Enabled = true;
    }

    internal PluginManifest Manifest { get; }

    internal string Folder { get; }

    internal string? UserDirectory { get; set; }

    internal PluginEntryKind EntryKind { get; }

    internal bool Enabled { get; set; }

    internal bool Loaded { get; set; }

    internal string? Status { get; set; }

    internal IPlugin? Instance { get; set; }

    internal PluginContext? Context { get; set; }

    internal PluginMessenger? Messenger { get; set; }

    /// <summary>The plugin's view over the shared service registry, disposed on unload.</summary>
    internal PluginServices? Services { get; set; }

    internal PluginStorage? Storage { get; set; }

    /// <summary>
    /// What this plugin exports to its dependents (<c>[exports] assemblies</c>), resolved once at load: the owning load context for runtime resolution, the path or image for the Roslyn compile.
    /// Empty for a plugin that exports nothing, and cleared on unload so neither the context nor the image is rooted.
    /// </summary>
    internal IReadOnlyList<PluginExportedAssembly> Exports { get; set; } = [];

    /// <summary>
    /// The plugin's <c>lang/</c> table, kept as the concrete type so <c>/lang &lt;tag&gt;</c> can retarget it without unloading the plugin: the files are already read, only the culture it answers in changes.
    /// </summary>
    internal PluginLocalization? Localization { get; set; }

    /// <summary>The plugin's settings file, held so <c>plugins settings regen|reset</c> can rewrite it.</summary>
    internal PluginSettings? Settings { get; set; }

    /// <summary>
    /// The settings type the plugin declared in <c>Configure</c>, or null when it declared none.
    /// Held alongside <see cref="Settings"/> because regeneration has to load the file INTO that type before it can write it back; without the type there is no schema and nothing to regenerate.
    /// </summary>
    internal Type? SettingsType { get; set; }

    /// <summary>
    /// The plugin's manual-source registration, held so unloading takes its pages back out of <c>/man</c>.
    /// An unloaded plugin's topics must not stay in the index pointing at files nothing will read.
    /// </summary>
    internal IDisposable? ManualRegistration { get; set; }

    internal AssemblyLoadContext? LoadContext { get; set; }

    /// <summary>A weak handle to the load context, kept across unload so the ALC-collect can be proven.</summary>
    internal WeakReference? LoadContextRef { get; set; }

    /// <summary>
    /// What the crash monitor has counted for this plugin, and the last exception it saw.
    /// Null until the plugin throws for the first time.
    /// </summary>
    internal PluginFault? LastFault { get; set; }

    /// <summary>How many exceptions this plugin has thrown since it was loaded, across every surface.</summary>
    internal int FaultCount { get; set; }

    /// <summary>True when the host disabled this plugin because it went over its crash budget.</summary>
    internal bool CrashDisabled { get; set; }

    internal PluginInfo ToInfo(string state) => new(
        Manifest.Id, Manifest.Version, EntryKind, Enabled, Loaded, state,
        Manifest.Services.Offline, FaultCount, LastFault?.Describe());
}

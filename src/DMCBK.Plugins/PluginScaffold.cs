using System.Globalization;
using System.Text.RegularExpressions;

namespace DMCBK.PluginSdk;

/// <summary>What <see cref="PluginScaffold.TryCreate"/> wrote.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Folder">The folder that was created.</param>
/// <param name="Files">The files written, relative to the folder, in the order they were written.</param>
public sealed record PluginScaffoldResult(string Id, string Folder, IReadOnlyList<string> Files);

/// <summary>
/// Writes a new plugin folder that already follows every convention this SDK expects: an immutable schema-v2 manifest with the current API version, a single-file entry with a settings class whose comments are <c>$settings.*$</c> placeholders, an English string table, and a manual page.
/// An author starts from the conventions instead of reading about them.
/// </summary>
public static class PluginScaffold
{
    private static readonly Regex ValidId = new(
        "^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Creates <c>&lt;root&gt;/&lt;Id&gt;/</c> from <paramref name="id"/>.
    /// Fails rather than overwriting when the folder is already there.
    /// </summary>
    public static bool TryCreate(
        string root, string id, out PluginScaffoldResult result, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        result = null!;
        error = null;

        string trimmed = (id ?? string.Empty).Trim();
        if (!ValidId.IsMatch(trimmed))
        {
            error = PluginStrings.ScaffoldIdInvalid(trimmed);
            return false;
        }

        string typeName = TypeName(trimmed);
        string folder = Path.Combine(Path.GetFullPath(root), typeName);
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            error = PluginStrings.ScaffoldExists(folder);
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, PluginLocalization.FolderName));
            Directory.CreateDirectory(Path.Combine(folder, "man", "en"));

            var written = new List<string>();
            Write(folder, PluginManifest.FileName, Manifest(trimmed, typeName), written);
            Write(folder, typeName + ".cs", Source(trimmed, typeName), written);
            Write(folder, Path.Combine(PluginLocalization.FolderName, "en.toml"), Language(typeName), written);
            Write(folder, Path.Combine("man", "en", trimmed + ".md"), Manual(trimmed, typeName), written);

            result = new PluginScaffoldResult(trimmed, folder, written);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = PluginStrings.ScaffoldFailed(trimmed, ex.Message);
            return false;
        }
    }

    /// <summary>The PascalCase type and folder name for a hyphenated plugin id.</summary>
    internal static string TypeName(string id)
        => string.Concat(id.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpper(part[0], CultureInfo.InvariantCulture) + part[1..]));

    private static void Write(string folder, string relative, string content, List<string> written)
    {
        File.WriteAllText(Path.Combine(folder, relative), content);
        written.Add(relative);
    }

    private static string Manifest(string id, string typeName) =>
        $"""
        # {typeName} - one line saying what this plugin does.
        schema-version = 2
        kind = "source"
        target = "any"
        framework = "net10.0"
        dmcbk = ">=0.1.0-preview.1 <0.2.0"
        umpk = ">=0.9.0-beta.4 <0.10.0"
        needs = ["commands"]
        id = "{id}"
        version = "1.0.0"
        entry = "{typeName}.cs"
        api-version = "{PluginApiVersion.Current}"

        # Manual pages this plugin contributes, reachable as /man <topic>.
        man = ["{id}"]

        [meta]
        description = "One line for a catalogue listing."
        tags = []
        uses = []

        # Declare this when the plugin is useful with no server connected. The idle banner names it.
        [services]
        offline = false

        """;

    private static string Source(string id, string typeName) =>
        $$"""
        // {{typeName}} - scaffolded by 'plugins new {{id}}'.
        //
        // Single-file plugins are compiled at load time with EXPLICIT usings: the host does not enable
        // ImplicitUsings, so every namespace this file needs is named below.

        using System;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;
        using Microsoft.Extensions.Logging;
        using Tomlet.Attributes;

        namespace DMCBK.Plugins.{{typeName}};

        /// <summary>The user-editable settings, written to settings.toml.</summary>
        public sealed class {{typeName}}Settings
        {
            /// <summary>Master switch.</summary>
            [TomlInlineComment("$settings.enabled$")]
            public bool Enabled { get; set; } = true;

            /// <summary>What the plugin says when it starts.</summary>
            [TomlInlineComment("$settings.greeting$")]
            public string Greeting { get; set; } = "hello";
        }

        /// <summary>{{typeName}}.</summary>
        public sealed class {{typeName}}Plugin : IPlugin
        {
            private {{typeName}}Settings _settings = new();

            /// <inheritdoc/>
            public void Configure(PluginDescriptor descriptor)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                descriptor.Id = "{{id}}";
                descriptor.Version = "1.0.0";
                descriptor.WithSettings<{{typeName}}Settings>();
            }

            /// <inheritdoc/>
            public Task ActivateAsync(PluginContext context)
            {
                ArgumentNullException.ThrowIfNull(context);
                _settings = context.Settings.Load<{{typeName}}Settings>();
                if (!_settings.Enabled)
                {
                    return Task.CompletedTask;
                }

                // Every user-facing string comes from lang/<language>.toml, never from a literal here.
                context.Logger.LogInformation("{Message}", context.Strings.Format("started", _settings.Greeting));

                context.SessionStarted += (_, _) =>
                    context.Logger.LogInformation("{Message}", context.Strings.Get("connected"));

                return Task.CompletedTask;
            }
        }

        """;

    // Message keys first, the [settings] table last.
    // A key written after a table header belongs to that table, so message keys placed below one resolve as "settings.<key>" and print as their own names.
    private static string Language(string typeName) =>
        $$"""
        # {{typeName}}'s own strings, in English. Copy this file to lang/<language>.toml to translate it;
        # leave the keys alone. Placeholders are positional ({0}, {1}).
        #
        # Keys outside a table come first, as TOML requires: anything written after a [table] header belongs
        # to that table.

        started = "{{typeName}} is running, and says {0}."
        connected = "{{typeName}} sees a session."

        # The settings.toml comments. The settings class references these as $settings.<key>$, so
        # settings.toml is written in the reader's language.
        [settings]
        enabled = "Set to false to stop the plugin without unloading it."
        greeting = "What the plugin says when it starts."

        """;

    private static string Manual(string id, string typeName) =>
        $"""
        # {typeName}

        One paragraph saying what this plugin is for. The first sentence becomes its row in the `/man`
        index, so write it as a summary rather than an introduction.

        Enable it with `/plugins enable {id}`.

        ## Settings

        In `plugins/userdata/{id}/settings.toml`.

        | Key | What it does |
        | --- | --- |
        | `Enabled` | Set to false to stop the plugin without unloading it. |
        | `Greeting` | What the plugin says when it starts. |

        """;
}

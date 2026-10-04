using System.Globalization;
using System.Text;
using DMCBK.Core.Configuration;
using Tomlet;
using Tomlet.Models;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Per-script settings: the declared <c># setting name = default ; comment</c> schema overlaid with the settings file, read through the <c>settings</c> map.
/// The file (<c>beacon/&lt;id&gt;.settings.toml</c>) is generated with commented defaults when missing and never rewritten once it exists; deleting it restores defaults.
/// Values are scalars only (text, number, yes/no); anything else in the file fails that key back to its default with a load warning, never the whole script.
/// </summary>
public static class BeaconScriptSettings
{
    /// <summary>
    /// Resolves settings for <paramref name="scriptId"/>: schema defaults overlaid with the settings file when <paramref name="configFolder"/> is given.
    /// File problems that are not the owner's fault surface as <paramref name="warnings"/>; the script still loads.
    /// </summary>
    public static IReadOnlyDictionary<string, BeaconValue> Resolve(
        string scriptId,
        IReadOnlyList<BeaconSettingDecl> schema,
        string? configFolder,
        List<BeaconDiagnostic>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(schema);
        var resolved = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach (BeaconSettingDecl decl in schema)
            resolved[decl.Name] = decl.Default;

        if (configFolder is null)
            return resolved;

        string path;
        try
        {
            path = Path.GetFullPath(ConfigurationPaths.BeaconSettingsFile(configFolder, scriptId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            warnings?.Add(SettingsWarning(scriptId, $"cannot resolve a settings path: {ex.Message}"));
            return resolved;
        }

        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, RenderDefaults(scriptId, schema), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings?.Add(SettingsWarning(scriptId, $"cannot write defaults: {ex.Message}"));
            }

            return resolved;
        }

        string source;
        try
        {
            source = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings?.Add(SettingsWarning(scriptId, $"cannot read '{path}': {ex.Message}"));
            return resolved;
        }

        TomlDocument document;
        try
        {
            document = new TomlParser().Parse(source);
        }
        catch (Exception ex)
        {
            warnings?.Add(SettingsWarning(scriptId, $"settings file does not parse ({ex.Message}); defaults apply"));
            return resolved;
        }

        foreach (BeaconSettingDecl decl in schema)
        {
            if (!document.TryGetValue(decl.Name, out TomlValue? node) || node is null)
                continue;

            try
            {
                BeaconValue decoded = BeaconValueTomlCodec.Decode(node, decl.Name);
                if (decoded is BeaconTextValue or BeaconNumberValue or BeaconYesNoValue)
                    resolved[decl.Name] = decoded;
                else
                {
                    warnings?.Add(SettingsWarning(
                        scriptId, $"setting '{decl.Name}' must be text, a number, or yes/no; the default applies"));
                }
            }
            catch (InvalidDataException ex)
            {
                warnings?.Add(SettingsWarning(scriptId, $"setting '{decl.Name}' unreadable ({ex.Message}); the default applies"));
            }
        }

        return resolved;
    }

    /// <summary>Renders the commented-defaults file content for <paramref name="schema"/>.</summary>
    public static string RenderDefaults(string scriptId, IReadOnlyList<BeaconSettingDecl> schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder();
        sb.Append("# Settings for Beacon script '").Append(scriptId).Append("'.");
        sb.Append(" Delete this file to restore defaults.\n");
        foreach (BeaconSettingDecl decl in schema)
        {
            if (!string.IsNullOrWhiteSpace(decl.Comment))
                sb.Append("# ").Append(decl.Comment.Trim()).Append('\n');

            sb.Append(decl.Name).Append(" = ").Append(RenderScalar(decl.Default)).Append('\n');
        }

        return sb.ToString();
    }

    private static string RenderScalar(BeaconValue value) => value switch
    {
        BeaconTextValue text => "\"" + text.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        BeaconNumberValue number => number.Value.ToString("G", CultureInfo.InvariantCulture),
        BeaconYesNoValue yesNo => yesNo.Value ? "true" : "false",
        _ => "\"\"",
    };

    private static BeaconDiagnostic SettingsWarning(string scriptId, string message) => new(
        BeaconDiagnosticCodes.ManifestWantsUnavailable,
        BeaconSeverity.Warning,
        $"Beacon settings for script '{scriptId}': {message}.",
        new SourceSpan(scriptId + ".settings.toml", 1, 1, 0),
        "Delete the settings file to restore commented defaults.");
}

namespace DMCBK.PluginSdk;

/// <summary>
/// The mutable declaration a plugin fills in during <see cref="IPlugin.Configure"/>.
/// The host reads it back to learn the plugin's identity, the SDK major it targets, and the POCO settings type to run through the settings pipeline.
/// Manifest (<c>plugin.toml</c>) values take precedence for id/version/api-version when present; this descriptor supplies them for code-first plugins and declares the settings type, which the manifest cannot express.
/// </summary>
public sealed class PluginDescriptor
{
    /// <summary>The plugin id (stable, lowercase-with-dashes by convention). Required.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The plugin version string (informational).</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>The SDK major the plugin targets; defaults to the current SDK major.</summary>
    public int ApiVersion { get; set; } = PluginApiVersion.Major;

    /// <summary>
    /// The POCO settings type the host loads/validates/persists as <c>settings.toml</c>.
    /// Null means the plugin declares no settings.
    /// The type must have a public parameterless constructor; implement <see cref="IValidatablePluginSettings"/> on it to hook validation (clamp/normalize/self-disable).
    /// </summary>
    public Type? SettingsType { get; set; }

    /// <summary>Declares a settings type fluently. Equivalent to setting <see cref="SettingsType"/>.</summary>
    public PluginDescriptor WithSettings<TSettings>()
        where TSettings : class, new()
    {
        SettingsType = typeof(TSettings);
        return this;
    }
}

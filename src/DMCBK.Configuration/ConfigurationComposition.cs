using DMCBK.Core.Configuration;
namespace DMCBK.Core;
/// <summary>Composes explicit TOML configuration storage.</summary>
public static class ConfigurationComposition
{
    /// <summary>Loads an explicit configuration path. Missing files are generated only when requested.</summary>
    public static ClientBuilder UseConfigurationsFolder(this ClientBuilder builder, string path,
        ConfigurationOverrides? overrides = null, bool generateMissing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var loader = new DmcbkConfigurationLoader(path);
        builder.UseConfiguration(loader.Load(overrides, generateMissing).Config);
        return builder.UseConfigurationStorage(path);
    }

    /// <summary>Attaches file persistence for an explicitly configured path.</summary>
    public static ClientBuilder UseConfigurationStorage(this ClientBuilder builder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string absolute = Path.GetFullPath(path);
        return builder.UseModule<IConfigurationStorage>(_ => new DmcbkConfigurationLoader(absolute));
    }
}

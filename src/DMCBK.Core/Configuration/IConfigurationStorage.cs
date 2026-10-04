namespace DMCBK.Core.Configuration;
/// <summary>Explicit file-based configuration storage supplied by a host module.</summary>
public interface IConfigurationStorage
{
    /// <summary>Selects the culture used for generated comments.</summary>
    void UseCulture(System.Globalization.CultureInfo culture);
    /// <summary>Loads a configuration without generating files unless requested.</summary>
    ConfigurationLoadResult Load(ConfigurationOverrides? overrides = null, bool generateMissing = false);
    /// <summary>Persists a named server.</summary>
    void SaveServer(ConfiguredServer server, bool makeActive = false);
    /// <summary>Persists a selected language.</summary>
    bool TrySaveLanguage(string language, out string? error);
}

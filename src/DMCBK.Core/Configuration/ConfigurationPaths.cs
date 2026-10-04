namespace DMCBK.Core.Configuration;

/// <summary>The fixed file names inside a <c>configurations/</c> folder.</summary>
public static class ConfigurationPaths
{
    /// <summary>The default configurations folder name (relative to the working directory).</summary>
    public const string DefaultFolderName = "configurations";

    /// <summary>The client (non-secret) config file.</summary>
    public const string ClientFileName = "client.toml";

    /// <summary>The accounts (secret) config file.</summary>
    public const string AccountsFileName = "accounts.toml";

    /// <summary>The servers config file.</summary>
    public const string ServersFileName = "servers.toml";

    /// <summary>The host-owned console config file (loaded by Mcc.Cli, not DMCBK.Core).</summary>
    public const string ConsoleFileName = "console.toml";

    /// <summary>The added plugin marketplaces, written only by the marketplace commands.</summary>
    public const string MarketplacesFileName = "marketplaces.toml";

    /// <summary>The Beacon script configuration file (host allowlist for Phase-8 net).</summary>
    public const string BeaconFileName = "beacon.toml";

    /// <summary>The gitignore template generated alongside the config files.</summary>
    public const string GitIgnoreFileName = ".gitignore";

    /// <summary>The scripts folder name, resolved beside (not inside) the configurations folder.</summary>
    public const string ScriptsDirName = "scripts";

    /// <summary>The per-script persisted-state folder name inside the configurations folder.</summary>
    public const string BeaconDirName = "beacon";

    /// <summary>Resolves a folder argument that may point at the folder itself or at client.toml inside it.</summary>
    public static string ResolveFolder(string folderOrClientFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderOrClientFile);
        if (File.Exists(folderOrClientFile)
            && string.Equals(Path.GetFileName(folderOrClientFile), ClientFileName, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetFullPath(folderOrClientFile)) ?? folderOrClientFile;

        return folderOrClientFile;
    }

    /// <summary>The path of the client file inside a folder.</summary>
    public static string ClientFile(string folder) => Path.Combine(folder, ClientFileName);

    /// <summary>The path of the accounts file inside a folder.</summary>
    public static string AccountsFile(string folder) => Path.Combine(folder, AccountsFileName);

    /// <summary>The path of the servers file inside a folder.</summary>
    public static string ServersFile(string folder) => Path.Combine(folder, ServersFileName);

    /// <summary>The path of the Beacon config file inside a folder.</summary>
    public static string BeaconFile(string folder) => Path.Combine(folder, BeaconFileName);

    /// <summary>The path of the gitignore file inside a folder.</summary>
    public static string GitIgnoreFile(string folder) => Path.Combine(folder, GitIgnoreFileName);

    /// <summary>The path of the marketplaces file inside a folder.</summary>
    public static string MarketplacesFile(string folder) => Path.Combine(folder, MarketplacesFileName);

    /// <summary>Resolves the scripts folder sitting beside a configurations folder (they share a parent).</summary>
    public static string ScriptsDir(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        string parent = Path.GetDirectoryName(Path.GetFullPath(configurationsFolder)) ?? configurationsFolder;
        return Path.Combine(parent, ScriptsDirName);
    }

    /// <summary>The path of the per-script persisted-state folder inside a configurations folder.</summary>
    public static string BeaconDir(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        return Path.Combine(configurationsFolder, BeaconDirName);
    }

    /// <summary>The path of one script's persisted-state file.</summary>
    public static string BeaconStateFile(string configurationsFolder, string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        return Path.Combine(BeaconDir(configurationsFolder), scriptId + ".toml");
    }

    /// <summary>The path of one script's settings file (declared <c># setting</c> schema with commented defaults).</summary>
    public static string BeaconSettingsFile(string configurationsFolder, string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        return Path.Combine(BeaconDir(configurationsFolder), scriptId + ".settings.toml");
    }
}

using DMCBK.Core.Configuration.Toml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Tomlet.Models;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Loads, generates, reloads and (on explicit request only) writes back the <c>configurations/</c> TOML files.
/// The pipeline is: deserialize (warn on unknown keys) -&gt; apply overrides -&gt; validate/derive -&gt; immutable <see cref="MccConfiguration"/> snapshot.
/// First-run generation writes any missing file with defaults and localized comments plus a <c>.gitignore</c> template.
/// A plain load of existing files writes nothing.
/// Reload produces a NEW snapshot and raises <see cref="Reloaded"/>; there is no live mutable global.
/// Instance-scoped: no static state.
/// </summary>
public sealed class MccConfigurationLoader : IConfigurationStorage
{
    private readonly string _folder;
    private readonly ILogger _logger;
    private readonly bool _commentsSupplied;
    private IConfigCommentSource _comments;
    private DefaultConfigWriter _writer;

    /// <summary>Creates a loader for a configurations folder (or a client.toml path inside one).</summary>
    public MccConfigurationLoader(
        string folderOrClientFile,
        IConfigCommentSource? comments = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderOrClientFile);
        _folder = ConfigurationPaths.ResolveFolder(folderOrClientFile);
        _commentsSupplied = comments is not null;
        _comments = comments ?? new ConfigCommentSource();
        _writer = new DefaultConfigWriter(_comments);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DMCBK.Core.Configuration");
    }

    /// <summary>The resolved configurations folder.</summary>
    public string Folder => _folder;

    /// <summary>
    /// Points later writes at the resolved UI culture's comments.
    /// First-run generation cannot use it: the file that carries the language preference is the file being generated, so its comments come out in the operating system's culture.
    /// Everything written afterwards (an account or server save, a regenerated file) follows the configured language instead.
    /// A host that supplied its own comment source keeps it; the caller chose that deliberately.
    /// </summary>
    public void UseCulture(System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (_commentsSupplied)
            return;

        _comments = new ConfigCommentSource(culture);
        _writer = new DefaultConfigWriter(_comments);
    }

    /// <summary>Raised after <see cref="Reload"/> produces a new snapshot.</summary>
    public event EventHandler<ConfigurationReloadedEventArgs>? Reloaded;

    /// <summary>The most recently produced snapshot, or null before the first load.</summary>
    public MccConfiguration? Current { get; private set; }

    /// <summary>
    /// Loads the configuration.
    /// When <paramref name="generateMissing"/> is true (the default), any missing file is generated with commented defaults and a <c>.gitignore</c> template before reading.
    /// Existing files are never rewritten by a load.
    /// </summary>
    public ConfigurationLoadResult Load(ConfigurationOverrides? overrides = null, bool generateMissing = false)
    {
        var warnings = new List<ConfigurationWarning>();
        bool generated = false;

        if (generateMissing)
            generated = GenerateMissingFiles();

        ClientTomlFile client = ReadFile<ClientTomlFile>(ConfigurationPaths.ClientFile(_folder), warnings);
        AccountsTomlFile accounts = ReadFile<AccountsTomlFile>(ConfigurationPaths.AccountsFile(_folder), warnings);
        ServersTomlFile servers = ReadFile<ServersTomlFile>(ConfigurationPaths.ServersFile(_folder), warnings);

        if (overrides is not null && !overrides.IsEmpty)
            ApplyOverrides(client, accounts, servers, overrides, warnings);

        ConfigurationValidationResult result = ConfigurationValidation.Validate(client, accounts, servers, _folder);
        warnings.AddRange(result.Warnings);

        Current = result.Config;
        return new ConfigurationLoadResult(result.Config, warnings, generated);
    }

    /// <summary>Reloads the configuration from disk, producing a new snapshot and raising <see cref="Reloaded"/>.</summary>
    public ConfigurationLoadResult Reload(ConfigurationOverrides? overrides = null)
    {
        MccConfiguration? previous = Current;
        ConfigurationLoadResult result = Load(overrides, generateMissing: false);
        Reloaded?.Invoke(this, new ConfigurationReloadedEventArgs(previous, result.Config));
        return result;
    }

    /// <summary>Generates any missing config file and the gitignore template. Returns true if anything was written.</summary>
    public bool GenerateMissingFiles()
    {
        Directory.CreateDirectory(_folder);
        bool wrote = false;

        wrote |= WriteIfMissing(ConfigurationPaths.ClientFile(_folder), () => _writer.Serialize(new ClientTomlFile()));
        wrote |= WriteIfMissing(ConfigurationPaths.AccountsFile(_folder), () => _writer.Serialize(DefaultAccounts()));
        wrote |= WriteIfMissing(ConfigurationPaths.ServersFile(_folder), () => _writer.Serialize(DefaultServers()));
        wrote |= WriteIfMissing(ConfigurationPaths.GitIgnoreFile(_folder), () => GitIgnoreTemplate);

        return wrote;
    }

    /// <summary>Write-back on explicit request: upserts an account into accounts.toml (optionally making it active).</summary>
    public void SaveAccount(ConfiguredAccount account, bool makeActive = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        string path = ConfigurationPaths.AccountsFile(_folder);
        AccountsTomlFile file = File.Exists(path) ? Parse<AccountsTomlFile>(File.ReadAllText(path)) : DefaultAccounts();

        AccountsTomlFile.AccountEntry entry = file.Account
            .FirstOrDefault(a => string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase))
            ?? AddEntry(file, account.Name);

        entry.Kind = account.Kind switch
        {
            MccAccountKind.MicrosoftDeviceCode => "microsoft",
            MccAccountKind.MicrosoftBrowser => "microsoft-browser",
            MccAccountKind.Yggdrasil => "yggdrasil",
            _ => "offline",
        };
        entry.Login = account.Login;
        entry.AuthServer = account.AuthServer ?? string.Empty;
        if (makeActive)
            file.Active = account.Name;

        WriteFile(path, _writer.Serialize(file));
    }

    /// <summary>Write-back on explicit request: upserts a server into servers.toml (optionally making it active).</summary>
    public void SaveServer(ConfiguredServer server, bool makeActive = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        string path = ConfigurationPaths.ServersFile(_folder);
        ServersTomlFile file = File.Exists(path) ? Parse<ServersTomlFile>(File.ReadAllText(path)) : DefaultServers();

        ServersTomlFile.ServerEntry entry = file.Server
            .FirstOrDefault(s => string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase))
            ?? AddServerEntry(file, server.Name);

        entry.Host = server.Host;
        entry.Port = server.Port;
        entry.Version = server.Version;
        entry.Kind = server.Kind == ConfiguredServerKind.Realm ? "realm" : "normal";
        entry.RealmWorld = server.RealmWorld;
        if (makeActive)
            file.Active = server.Name;

        WriteFile(path, _writer.Serialize(file));
    }

    /// <summary>
    /// Write-back on explicit request: sets <c>Localization.Language</c> in <c>client.toml</c> for <c>/lang &lt;tag&gt;</c>.
    /// Returns false with a reason when the file cannot be read or written.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SaveAccount"/> and <see cref="SaveServer"/>, which re-emit their whole file, this edits the one line.
    /// <c>accounts.toml</c> and <c>servers.toml</c> are lists this client owns outright; <c>client.toml</c> is the file the user edits by hand, and re-emitting it would keep every value and destroy every comment they wrote.
    /// Same reasoning, and the same shape, as the plugin manifest's <c>enabled</c> write-back.
    /// </remarks>
    /// <param name="language">The language tag to store, already validated by the caller.</param>
    /// <param name="error">The failure reason, or null on success.</param>
    public bool TrySaveLanguage(string language, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        error = null;
        string path = ConfigurationPaths.ClientFile(_folder);
        try
        {
            string original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            WriteFile(path, ClientLanguageWriter.Rewrite(original, language));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Failed to write the language into {Path}.", path);
            return false;
        }
    }

    private void ApplyOverrides(
        ClientTomlFile client,
        AccountsTomlFile accounts,
        ServersTomlFile servers,
        ConfigurationOverrides overrides,
        List<ConfigurationWarning> warnings)
    {
        // Address/version: retarget whichever source resolution will use (an active server, else the connection).
        if (overrides.Address is not null || overrides.Version is not null)
        {
            ServersTomlFile.ServerEntry? active = string.IsNullOrWhiteSpace(servers.Active)
                ? null
                : servers.Server.FirstOrDefault(s => string.Equals(s.Name, servers.Active, StringComparison.OrdinalIgnoreCase));

            if (overrides.Address is not null)
            {
                (string host, ushort? port) = SplitAddress(overrides.Address);
                if (active is not null)
                {
                    active.Host = host;
                    if (port is not null)
                        active.Port = port.Value;
                }
                else
                {
                    client.Connection.Host = host;
                    if (port is not null)
                        client.Connection.Port = port.Value;
                }
            }

            if (overrides.Version is not null)
            {
                if (active is not null)
                    active.Version = overrides.Version;
                else
                    client.Connection.Version = overrides.Version;
            }
        }

        // Account: mutate the active (or first, or a synthesized) account entry.
        if (overrides.Username is not null || overrides.AuthMode is not null
            || overrides.AuthServer is not null || overrides.Password is not null)
        {
            AccountsTomlFile.AccountEntry entry = ResolveOrCreateActiveAccount(accounts);
            if (overrides.Username is not null)
                entry.Login = overrides.Username;

            if (overrides.AuthMode is not null)
                entry.Kind = overrides.AuthMode;

            if (overrides.AuthServer is not null)
                entry.AuthServer = overrides.AuthServer;

            // "-" is the offline sentinel.
            // Any other password is accepted but unusable (online auth is interactive).
            // An explicit --auth wins over the sentinel.
            // Silently downgrading an explicit online request to offline sent the client at an online-mode server with an offline account, which the server dropped as an opaque connect failure.
            // Passing "-" without --auth still forces offline.
            if (overrides.Password == "-")
            {
                if (overrides.AuthMode is null)
                    entry.Kind = "offline";
                else
                {
                    warnings.Add(new ConfigurationWarning(
                        $"Ignoring the '-' offline password sentinel because --auth '{overrides.AuthMode}' was given explicitly; "
                        + "pass no --auth flag to force offline."));
                }
            }
        }

        foreach (KeyValuePair<string, string> dotted in overrides.Dotted)
            ClientTomlOverrideBinder.Apply(client, dotted.Key, dotted.Value, warnings);
    }

    private static AccountsTomlFile.AccountEntry ResolveOrCreateActiveAccount(AccountsTomlFile accounts)
    {
        if (accounts.Account.Count == 0)
        {
            var created = new AccountsTomlFile.AccountEntry { Name = "cli", Kind = "offline" };
            accounts.Account.Add(created);
            accounts.Active = "cli";
            return created;
        }

        AccountsTomlFile.AccountEntry entry = (string.IsNullOrWhiteSpace(accounts.Active)
            ? null
            : accounts.Account.FirstOrDefault(a => string.Equals(a.Name, accounts.Active, StringComparison.OrdinalIgnoreCase)))
            ?? accounts.Account[0];

        accounts.Active = entry.Name;
        return entry;
    }

    private static (string Host, ushort? Port) SplitAddress(string address)
    {
        int colon = address.LastIndexOf(':');
        if (colon > 0 && colon < address.Length - 1 && ushort.TryParse(address[(colon + 1)..], out ushort port))
            return (address[..colon], port);

        return (address, null);
    }

    private T ReadFile<T>(string path, List<ConfigurationWarning> warnings) where T : new()
    {
        if (!File.Exists(path))
            return new T();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new ConfigurationWarning($"Could not read '{path}': {ex.Message}. Using defaults."));
            return new T();
        }

        try
        {
            TomlDocument document = new TomlParser().Parse(text);
            TomlUnknownKeyScanner.Scan(document, typeof(T), string.Empty, warnings);
            return TomletMain.To<T>(document);
        }
        catch (Exception ex)
        {
            warnings.Add(new ConfigurationWarning($"Could not parse '{path}': {ex.Message}. Using defaults."));
            _logger.LogWarning(ex, "Failed to parse {Path}; using defaults.", path);
            return new T();
        }
    }

    private static T Parse<T>(string text) where T : new()
        => TomletMain.To<T>(new TomlParser().Parse(text));

    private bool WriteIfMissing(string path, Func<string> content)
    {
        if (File.Exists(path))
            return false;

        WriteFile(path, content());
        return true;
    }

    private void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _folder);
        File.WriteAllText(path, content);
        _logger.LogInformation("Wrote configuration file {Path}.", path);
    }

    private static AccountsTomlFile.AccountEntry AddEntry(AccountsTomlFile file, string name)
    {
        var entry = new AccountsTomlFile.AccountEntry { Name = name };
        file.Account.Add(entry);
        return entry;
    }

    private static ServersTomlFile.ServerEntry AddServerEntry(ServersTomlFile file, string name)
    {
        var entry = new ServersTomlFile.ServerEntry { Name = name };
        file.Server.Add(entry);
        return entry;
    }

    /// <summary>
    /// The generated accounts.toml. The account list is deliberately EMPTY: a stub entry here would be an account as far as every consumer is concerned, which would silence the host's first-run login prompt and quietly connect everyone as the same placeholder name.
    /// An empty list is the honest statement that no account has been chosen yet, and it is what makes the prompt fire exactly once.
    /// </summary>
    private static AccountsTomlFile DefaultAccounts() => new();

    private static ServersTomlFile DefaultServers()
    {
        var file = new ServersTomlFile();
        file.Server.Add(new ServersTomlFile.ServerEntry { Name = "localhost", Host = "localhost", Port = 25565 });
        return file;
    }

    private const string GitIgnoreTemplate =
        "# Secrets and caches generated by Minecraft Console Client. Do not commit these.\n"
        + "accounts.toml\n"
        + "cache/\n"
        + "*.backup.toml\n";
}

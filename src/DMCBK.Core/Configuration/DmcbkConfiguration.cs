namespace DMCBK.Core.Configuration;

/// <summary>
/// An immutable, validated configuration snapshot.
/// Produced by <c>file-based configuration storage</c> from the <c>client.toml</c> / <c>accounts.toml</c> / <c>servers.toml</c> files, then never mutated.
/// A reload produces a NEW snapshot; consumers re-read rather than observe in-place mutation.
/// </summary>
/// <remarks>
/// The <c>Resolved*</c> members are derived by the validator: they fold the account/server selection and the connection block into the single effective endpoint, version and account the client actually uses.
/// This keeps the builder mapping trivial and lets the resolution be unit-tested without a session.
/// </remarks>
public sealed record DmcbkConfiguration
{
    /// <summary>Connection address and policy (client.toml).</summary>
    public ConnectionConfig Connection { get; init; } = new();

    /// <summary>Feature gates and gameplay behavior (client.toml).</summary>
    public GameplayConfig Gameplay { get; init; } = new();

    /// <summary>Outgoing chat behavior, formats and signing toggles (client.toml).</summary>
    public ChatConfig Chat { get; init; } = new();

    /// <summary>Announced client-settings (client.toml).</summary>
    public ClientSettingsConfig ClientSettings { get; init; } = new();

    /// <summary>Language and translation flags (client.toml).</summary>
    public LocalizationConfig Localization { get; init; } = new();

    /// <summary>Logging levels, filters and file output (client.toml).</summary>
    public LoggingConfig Logging { get; init; } = new();

    /// <summary>Per-session diagnostics: the bundle written under <c>logs/</c>.</summary>
    public DiagnosticsConfig Diagnostics { get; init; } = new();

    /// <summary>The plugin host's own limits (client.toml).</summary>
    public PluginsConfig Plugins { get; init; } = new();

    /// <summary>Command prefix and owners (client.toml).</summary>
    public PermissionsConfig Permissions { get; init; } = new();

    /// <summary>User-defined %variables% (client.toml), seeded into the command variable store.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Accounts, cache policy and cache directory (accounts.toml, secrets).</summary>
    public AccountsConfig Accounts { get; init; } = new();

    /// <summary>Proxy configuration (accounts.toml, secrets).</summary>
    public ProxyConfig Proxy { get; init; } = new();

    /// <summary>Named servers and active selection (servers.toml).</summary>
    public ServersConfig Servers { get; init; } = new();

    /// <summary>The effective server host after folding server selection over the connection block.</summary>
    public string ResolvedHost { get; init; } = string.Empty;

    /// <summary>The effective server port.</summary>
    public ushort ResolvedPort { get; init; } = 25565;

    /// <summary>The effective version: <c>"auto"</c> or a release name.</summary>
    public string ResolvedVersion { get; init; } = "auto";

    /// <summary>The effective account after folding the active-account selection over the account list.</summary>
    public ConfiguredAccount ResolvedAccount { get; init; } = new();

    /// <summary>The configurations folder this snapshot was loaded from, when loaded from disk.</summary>
    public string? SourceFolder { get; init; }
}

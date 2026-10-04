namespace DMCBK.Core.Configuration;

/// <summary>
/// CLI-supplied overrides applied on top of the on-disk configuration, in precedence order <c>config &lt; positional &lt; dotted</c>.
/// Positional and flag fields retarget the CLI contract (<c>&lt;configs&gt; &lt;username&gt; &lt;password|-&gt; &lt;host:port&gt;</c> plus <c>-v/--auth/--auth-server</c>).
/// <see cref="Dotted"/> carries <c>--section.setting=value</c> pairs that bind onto client.toml paths and win over the positional values.
/// </summary>
public sealed record ConfigurationOverrides
{
    /// <summary>Overrides the active account's login (positional username).</summary>
    public string? Username { get; init; }

    /// <summary>The positional password slot: <c>"-"</c> forces offline; other values are accepted but unused (online auth is interactive).</summary>
    public string? Password { get; init; }

    /// <summary>Overrides the server address (host or host:port).</summary>
    public string? Address { get; init; }

    /// <summary>Overrides the version (release name or <c>auto</c>).</summary>
    public string? Version { get; init; }

    /// <summary>Overrides the active account kind (offline/microsoft/microsoft-browser/yggdrasil).</summary>
    public string? AuthMode { get; init; }

    /// <summary>Overrides the active account's Yggdrasil base URL.</summary>
    public string? AuthServer { get; init; }

    /// <summary>Dotted <c>section.setting=value</c> overrides for client.toml paths (highest precedence).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Dotted { get; init; } = [];

    /// <summary>True when no override is set.</summary>
    public bool IsEmpty =>
        Username is null && Password is null && Address is null && Version is null
        && AuthMode is null && AuthServer is null && Dotted.Count == 0;
}

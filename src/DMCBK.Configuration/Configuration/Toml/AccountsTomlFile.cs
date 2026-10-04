using Tomlet.Attributes;

namespace DMCBK.Core.Configuration.Toml;

// Mutable, TOML-facing model of accounts.toml (secrets: accounts + proxy credentials).
// Kept out of the git-tracked tree by the generated configurations/.gitignore.

internal sealed class AccountsTomlFile
{
    [TomlPrecedingComment("$Mcc.accounts.active$")]
    public string Active { get; set; } = string.Empty;

    [TomlPrecedingComment("$Mcc.accounts.session_cache$")]
    public string SessionCache { get; set; } = "disk";

    [TomlPrecedingComment("$Mcc.accounts.profile_key_cache$")]
    public string ProfileKeyCache { get; set; } = "disk";

    [TomlPrecedingComment("$Mcc.accounts.cache_directory$")]
    public string CacheDirectory { get; set; } = "cache";

    [TomlPrecedingComment("$Mcc.accounts.entry$")]
    public List<AccountEntry> Account { get; set; } = new();

    [TomlPrecedingComment("$Mcc.proxy$")]
    public ProxyTable Proxy { get; set; } = new();

    internal sealed class AccountEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = "offline";
        public string Login { get; set; } = string.Empty;
        public string AuthServer { get; set; } = string.Empty;
    }

    internal sealed class ProxyTable
    {
        [TomlPrecedingComment("$Proxy.Enabled_Login$")]
        public bool EnabledLogin { get; set; }

        [TomlPrecedingComment("$Proxy.Enabled_Ingame$")]
        public bool EnabledIngame { get; set; }

        [TomlPrecedingComment("$Proxy.Server$")]
        public string Host { get; set; } = "0.0.0.0";

        public int Port { get; set; } = 8080;

        [TomlPrecedingComment("$Proxy.Proxy_Type$")]
        public string Kind { get; set; } = "http";

        [TomlPrecedingComment("$Proxy.Username$")]
        public string Username { get; set; } = string.Empty;

        [TomlPrecedingComment("$Proxy.Password$")]
        public string Password { get; set; } = string.Empty;
    }
}

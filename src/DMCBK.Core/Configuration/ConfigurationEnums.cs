namespace DMCBK.Core.Configuration;

/// <summary>SRV-record resolution policy (re-homed from the legacy <c>ResolveSrvRecordType</c>).</summary>
public enum SrvResolveMode
{
    /// <summary>Never look up SRV records.</summary>
    No,

    /// <summary>Look up SRV records with a short timeout (the default).</summary>
    Fast,

    /// <summary>Look up SRV records, waiting for the full DNS timeout.</summary>
    Yes,
}

/// <summary>The client brand string reported to the server (re-homed from <c>BrandInfoType</c>).</summary>
public enum BrandKind
{
    /// <summary>Report the MCC brand.</summary>
    Mcc,

    /// <summary>Report the vanilla brand.</summary>
    Vanilla,

    /// <summary>Report an empty brand.</summary>
    Empty,
}

/// <summary>Where a cache (session token, signing certificate) is persisted (re-homed from <c>CacheType</c>).</summary>
public enum CacheMode
{
    /// <summary>Do not cache.</summary>
    None,

    /// <summary>Cache in memory for the process lifetime only.</summary>
    Memory,

    /// <summary>Cache on disk under the configurations cache directory.</summary>
    Disk,
}

/// <summary>The announced client difficulty (re-homed from <c>DifficultyType</c>).</summary>
public enum DifficultyKind
{
    /// <summary>Peaceful.</summary>
    Peaceful,

    /// <summary>Easy.</summary>
    Easy,

    /// <summary>Normal.</summary>
    Normal,

    /// <summary>Difficult (hard).</summary>
    Difficult,
}

/// <summary>The announced chat mode (re-homed from <c>ChatModeType</c>).</summary>
public enum ChatModeKind
{
    /// <summary>All chat enabled.</summary>
    Enabled,

    /// <summary>Commands only.</summary>
    Commands,

    /// <summary>Chat disabled.</summary>
    Disabled,
}

/// <summary>The announced main hand (re-homed from <c>MainHandType</c>).</summary>
public enum MainHandKind
{
    /// <summary>Left hand.</summary>
    Left,

    /// <summary>Right hand.</summary>
    Right,
}

/// <summary>Log-filter mode (re-homed from <c>FilterModeEnum</c>).</summary>
public enum LogFilterMode
{
    /// <summary>No filtering.</summary>
    Disable,

    /// <summary>Drop messages matching the filter.</summary>
    Blacklist,

    /// <summary>Keep only messages matching the filter.</summary>
    Whitelist,
}

/// <summary>The prefix that marks a line as an internal command (re-homed from <c>InternalCmdCharType</c>).</summary>
public enum InternalCommandPrefix
{
    /// <summary>No prefix; every line is an internal command.</summary>
    None,

    /// <summary>A leading slash marks an internal command.</summary>
    Slash,

    /// <summary>A leading backslash marks an internal command.</summary>
    Backslash,
}

/// <summary>Proxy protocol (re-homed from <c>ProxyType</c>).</summary>
public enum ProxyKind
{
    /// <summary>HTTP CONNECT proxy.</summary>
    Http,

    /// <summary>SOCKS4 proxy.</summary>
    Socks4,

    /// <summary>SOCKS4a proxy.</summary>
    Socks4a,

    /// <summary>SOCKS5 proxy.</summary>
    Socks5,
}

/// <summary>The kind of a configured server entry.</summary>
public enum ConfiguredServerKind
{
    /// <summary>A normal Java server reached by host and port.</summary>
    Normal,

    /// <summary>A Minecraft Realm.</summary>
    Realm,
}

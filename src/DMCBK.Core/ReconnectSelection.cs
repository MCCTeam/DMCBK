using Umpk;
using Umpk.Protocol.Java;

namespace DMCBK.Core;

/// <summary>
/// A minimal server selection for <see cref="Client.ReconnectAsync"/>: where to connect and, optionally, a pinned version.
/// The full connect/reconnect command UX (selecting a named server from servers.toml) lives in the command layer; this is the core value the API needs now.
/// </summary>
public sealed record ServerSelection
{
    /// <summary>The target host (or SRV domain).</summary>
    public required string Host { get; init; }

    /// <summary>The target port.</summary>
    public ushort Port { get; init; } = ServerEndpoint.DefaultJavaPort;

    /// <summary>A pinned version; <c>null</c> re-detects the version by status ping on reconnect.</summary>
    public JavaVersion? Version { get; init; }

    /// <summary>Projects the selection to a UMPK endpoint.</summary>
    public ServerEndpoint ToEndpoint() => new(Host, Port);
}

/// <summary>
/// A minimal account selection for <see cref="Client.ReconnectAsync"/>: the account to log in as on the new session.
/// Resolving a named account from accounts.toml happens in the command layer; this carries the resolved account.
/// </summary>
public sealed record AccountSelection
{
    /// <summary>The account to reconnect as.</summary>
    public required MccAccount Account { get; init; }
}

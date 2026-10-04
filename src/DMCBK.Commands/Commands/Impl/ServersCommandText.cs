using System.Globalization;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The text of the <c>servers</c> command: saving named servers for <c>/connect</c>.</summary>
internal static class ServersCommandText
{
    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);

    public const string Desc = "Save servers to servers.toml for /connect.";

    public const string Usage = "servers add <name> <host[:port]>";

    /// <summary>The saved confirmation, naming what answers <c>/connect &lt;name&gt;</c> now.</summary>
    public static string Saved(string name, string host, int port)
        => F("Saved server '{0}' ({1}:{2}).", name, host, port);

    /// <summary>The address failed the same host[:port] shape <c>/connect</c> accepts.</summary>
    public static string InvalidAddress(string address)
        => F("'{0}' is not a host or host:port.", address);

    /// <summary>The address named a saved server instead of giving a literal address to store.</summary>
    public static string LooksLikeSavedName(string address)
        => F("'{0}' is already a saved server name. Give the host:port to store under a new name.", address);

    public const string NoConfig = "No configuration folder is known, so there is nowhere to save.";

    /// <summary>A failed write-back, carrying the I/O reason.</summary>
    public static string SaveFailed(string reason)
        => F("Could not save the server: {0}", reason);
}

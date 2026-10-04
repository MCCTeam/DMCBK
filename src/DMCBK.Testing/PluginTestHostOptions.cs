using Microsoft.Extensions.Logging;

namespace DMCBK.Testing;

/// <summary>How a <see cref="PluginTestHost"/> is built. Every value has a working default.</summary>
public sealed class PluginTestHostOptions
{
    /// <summary>The username the client logs in with.</summary>
    public string Username { get; set; } = "Tester";

    /// <summary>
    /// The Minecraft version the session speaks, by name.
    /// It is pinned so no status ping is attempted, and it decides which packets the fake server can encode.
    /// </summary>
    public string Version { get; set; } = "1.21.5";

    /// <summary>
    /// The plugins root.
    /// Null means a fresh folder under the system temp directory, which the host deletes when it is disposed.
    /// A folder given here is left alone.
    /// </summary>
    public string? PluginsRoot { get; set; }

    /// <summary>How long the harness waits for anything before giving up.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Where the client, the host and the plugins log. Null discards the output.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>The <c>[Plugins]</c> limits the host runs under, above all the crash budget.</summary>
    public DMCBK.Core.Configuration.PluginsConfig? Limits { get; set; }
}

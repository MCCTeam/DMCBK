using Tomlet.Attributes;

namespace DMCBK.Core.Configuration.Toml;

// Mutable, TOML-facing model of servers.toml (named server list + active selection).

internal sealed class ServersTomlFile
{
    [TomlPrecedingComment("$Mcc.servers.active$")]
    public string Active { get; set; } = string.Empty;

    [TomlPrecedingComment("$Mcc.servers.entry$")]
    public List<ServerEntry> Server { get; set; } = new();

    internal sealed class ServerEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 25565;
        public string Version { get; set; } = "auto";
        public string Kind { get; set; } = "normal";
        public string RealmWorld { get; set; } = string.Empty;
    }
}

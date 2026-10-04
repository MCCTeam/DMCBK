using DMCBK.Core.Commands;
using Umpk.Client;

namespace DMCBK.Core;

/// <summary>Composes the optional command dispatcher.</summary>
public static class CommandsComposition
{
    /// <summary>Adds a per-client command dispatcher.</summary>
    public static ClientBuilder UseCommands(this ClientBuilder builder)
        => builder.UseModule<ICommandDispatcher>(client => new CommandService(client, client.CommandOutput, client.HostUi,
            client.Configuration, client.Features, client.Variables), "commands");
}

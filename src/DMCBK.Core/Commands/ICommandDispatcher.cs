using System.Collections.Immutable;
using DMCBK.Core.Configuration;
using DMCBK.Core.Presentation;
using Microsoft.Extensions.Logging;
using Umpk.Commands;

namespace DMCBK.Core.Commands;

/// <summary>Client command boundary implemented by the optional Commands module.</summary>
public interface ICommandDispatcher
{
    /// <summary>Raised when a registered command fails.</summary>
    event EventHandler<CommandFaultedEventArgs>? CommandFaulted;
    /// <summary>The configured prefix character.</summary>
    char Prefix { get; }
    /// <summary>Whether input has no internal command prefix.</summary>
    bool NoPrefix { get; }
    /// <summary>The displayed prefix.</summary>
    string ActivePrefix { get; }
    /// <summary>The current configuration.</summary>
    DmcbkConfiguration? CurrentConfiguration { get; }
    /// <summary>The variable store.</summary>
    VariableStore Variables { get; }
    /// <summary>The host status glyphs.</summary>
    GlyphSet Glyphs { get; set; }
    /// <summary>Whether executed commands are echoed.</summary>
    bool EchoCommands { get; set; }
    /// <summary>The registry completion source.</summary>
    IRegistrySuggestionSource RegistrySuggestions { get; }
    /// <summary>The diagnostic logger.</summary>
    ILogger Logger { get; }
    /// <summary>Registers a command for the module lifetime.</summary>
    void RegisterHostCommand(CommandBase command);
    /// <summary>Registers a command with an explicit disposal scope.</summary>
    IDisposable RegisterScopedCommand(CommandBase command);
    /// <summary>Applies a new configuration snapshot.</summary>
    void ReloadConfiguration(DmcbkConfiguration config);
    /// <summary>Routes a host input line.</summary>
    Task<InputRouting> HandleInputAsync(string line, CancellationToken ct = default);
    /// <summary>Dispatches an internal command.</summary>
    Task<CmdResult> DispatchAsync(string command, CancellationToken ct = default);
    /// <summary>Dispatches an internal command while capturing body output.</summary>
    Task<(CmdResult Result, string Output)> DispatchCapturedAsync(string command, CancellationToken ct = default);
    /// <summary>Completes an input line.</summary>
    Task<IReadOnlyList<string>> CompleteAsync(string input, CancellationToken ct = default);
    /// <summary>Completes an input buffer at its cursor.</summary>
    Task<InputCompletion> CompleteInputAsync(string line, int cursor, CancellationToken ct = default);
    /// <summary>Returns the registered command index.</summary>
    IReadOnlyList<CommandSummary> ListCommands();
    /// <summary>Returns structured command descriptions.</summary>
    IReadOnlyList<CommandDescriptor> DescribeCommands();
    /// <summary>Returns dispatcher-derived usage rows.</summary>
    ImmutableArray<string> GetCommandUsage(string commandName, CommandContext ctx);
    /// <summary>Renders the command index through the attached presentation adapter.</summary>
    string BuildHelpListing();
    /// <summary>Renders one command's help through the attached presentation adapter.</summary>
    string RenderUsage(CommandBase command, CommandContext context);
}

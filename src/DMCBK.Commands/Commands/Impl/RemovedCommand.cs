using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Commands;
using Umpk.Data.Java;
using Umpk.Protocol.Java;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// A gutted command: it exists only to report that it was removed and point to its replacement.
/// The legacy grammar still parses (every argument shape is swallowed by the greedy <c>args</c>) so an old script or habit gets the notice instead of a syntax error.
/// </summary>
public sealed class RemovedCommand : CommandBase
{
    private readonly string _name;
    private readonly string _message;

    private RemovedCommand(string name, string message)
    {
        _name = name;
        _message = message;
    }

    /// <inheritdoc/>
    public override string CmdName => _name;

    /// <inheritdoc/>
    public override string CmdDesc => _message;

    /// <inheritdoc/>
    public override string CmdUsage => _name;

    /// <summary>The removed <c>upgrade</c> command (legacy <c>Commands/Upgrade.cs</c>).</summary>
    public static RemovedCommand Upgrade() => new("upgrade", CommandStrings.RemovedUpgrade);

    /// <summary>The removed <c>script</c> command (legacy <c>Commands/Script.cs</c>).</summary>
    public static RemovedCommand Script() => new("script", CommandStrings.RemovedScript);

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    /// <remarks>
    /// A removed command still answers when typed, so the user learns what happened to it, but it has no business taking a row in the index of things you CAN do.
    /// </remarks>
    public override bool ShowInIndex => false;

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ctx.Source.Result.Ok(_message))
            .ThenArgument("args", Arguments.GreedyString(), h => h.Executes(ctx => ctx.Source.Result.Ok(_message)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }
}

using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>respawn</c> command: respawn after death.
///
/// <para>
/// The success line is legacy's "You have respawned."
/// The two failure lines have no legacy counterpart because legacy had no failure: it printed that sentence unconditionally, and the sentence is true about the socket and false about the player, since vanilla's <c>perform_respawn</c> handler returns without doing anything when the player is alive and did not just win the game, and it sends nothing back to say so.
/// The command waits for the clientbound respawn, the same signal a vanilla client waits for before leaving the death screen, and only claims the respawn when it arrives.
/// </para>
/// </summary>
public sealed class RespawnCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "respawn";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.respawn.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "respawn";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Session;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "respawn after dying"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["respawn"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["health"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Turns what the server is known to have done about the respawn request into the reported success flag and message.
    /// <see cref="RespawnOutcome.NotDead"/> only refines the failure text: it names the reason vanilla most often drops the request instead of leaving the user with a bare "did not respawn you".
    /// It is never used to refuse the send, because the won-the-game respawn out of the End is also made by a living player and gating on health would turn a false success into a false failure.
    /// Internal and static so the mapping is pinned by a test without a live server.
    /// </summary>
    internal static (bool Ok, string Message) Describe(RespawnOutcome outcome) => outcome switch
    {
        RespawnOutcome.Confirmed => (true, McStrings.Get("cmd.respawn.done")),
        RespawnOutcome.NotDead => (false, CommandStrings.RespawnNotDead),
        _ => (false, CommandStrings.RespawnUnconfirmed),
    };

    private int Run(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        RespawnOutcome outcome = ctx.Run(ct => ctx.Game.Player.RespawnAsync(ct));
        (bool ok, string message) = Describe(outcome);
        return ok ? ctx.Result.Ok(message) : ctx.Result.Fail(message);
    }
}

using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>animation</c> command: swings the main or off hand.</summary>
public sealed class AnimationCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "animation";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.animation.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "animation <mainhand|offhand>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("mainhand", "swing your main hand"),
        new("offhand", "swing your off hand"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["animation mainhand"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy also accepted "help animation mainhand" and "help animation offhand" (Animation.cs:18-21);
        // all three render the same description.
        help.ThenLiteral("mainhand", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("offhand", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source, true))
            .ThenLiteral("mainhand", h => h.Executes(ctx => Run(ctx.Source, true)))
            .ThenLiteral("offhand", h => h.Executes(ctx => Run(ctx.Source, false)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, bool mainHand)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        ctx.Run(ct => ctx.Game.Movement.SwingAsync(mainHand, ct));

        // Legacy reported the swing through CmdResult.SetAndReturn(bool) (CommandHandler/CmdResult.cs:67-72), whose message is the generic "Done" / "Fail" word, not a sentence about the arm.
        // UMPK's swing action completes or throws, so only the Done branch is reachable here.
        return ctx.Result.Set(CmdStatus.Done, McStrings.Get("general.done"));
    }
}

using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>sneak</c> command: toggles sneaking.</summary>
public sealed class SneakCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "sneak";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.sneak.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "sneak";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "toggle sneaking"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Terrain;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["sneak"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        bool next = !pose.Sneaking;
        ctx.Run(ct => ctx.Game.Movement.SetSneakingAsync(next, ct));
        return ctx.Result.Ok(McStrings.Get(next ? "cmd.sneak.on" : "cmd.sneak.off"));
    }
}

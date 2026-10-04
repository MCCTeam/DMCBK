using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>tps</c> command: prints the estimated server TPS, coloured by threshold.</summary>
public sealed class TpsCommand : CommandBase
{
    /// <summary>Legacy's threshold colours (Tps.cs:44-51): red below 10, yellow below 15, green above.</summary>
    private const string ColorRed = "§c";

    private const string ColorYellow = "§e";

    private const string ColorGreen = "§a";

    /// <inheritdoc/>
    public override string CmdName => "tps";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.tps.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "tps";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "estimate the server's tick rate"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["tps"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["debug"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders a measured tick rate exactly as legacy did: the rate rounded to two decimals, prefixed by the threshold colour code, after "Current tps: ".
    /// <para>
    /// A null rate means UNKNOWN and says so.
    /// Legacy could not reach that state (its averageTPS field starts at 20 and only ever holds the last reading, so a paused server printed a stale number), while UMPK derives the rate from the game time the server broadcasts every 20 ticks and expires it once those stop, which is exactly what a server paused by 1.21.2+ pause-when-empty or held by <c>/tick freeze</c> does.
    /// Printing a stale reading there would claim knowledge the client does not have, so this one branch keeps the newer wording; every reachable-in-legacy branch is legacy's.
    /// </para>
    /// </summary>
    /// <param name="ticksPerSecond">The measured tick rate, or null when it is not known.</param>
    public static string Format(double? ticksPerSecond)
    {
        if (ticksPerSecond is not { } measured)
            return CommandStrings.TpsUnknown;

        double tps = Math.Round(measured, 2);
        string color = tps < 10 ? ColorRed : tps < 15 ? ColorYellow : ColorGreen;
        return $"{McStrings.Get("cmd.tps.current")}: {color}{tps}";
    }

    private int Run(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        SessionInfoSnapshot info = ctx.Run(ct => ctx.Game.Session.GetInfoAsync(ct));
        return ctx.Result.Ok(Format(info.TpsEstimate));
    }
}

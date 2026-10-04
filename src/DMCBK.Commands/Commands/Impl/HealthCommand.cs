using System.Globalization;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>health</c> command: prints health, food saturation, level and total experience.</summary>
public sealed class HealthCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "health";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.health.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "health";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "show health, food and experience"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["health"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["effects", "respawn"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders a health reading the way a player reads it off the hearts.
    /// Health is a float on the wire and the server's damage arithmetic leaves a binary tail on it, so the value behind "14 hearts" arrives as 14.000002 and the raw number puts every one of those digits in the line.
    /// The corpus line is legacy's and holds a bare "{0}", so the rounding belongs on the value: at most two decimals, and a whole number stays whole (14.000002 -&gt; "14", 14.5 -&gt; "14.5", 14.567 -&gt; "14.57").
    /// <para>
    /// The culture is <see cref="CultureInfo.CurrentCulture"/>, the one <see cref="McStrings.Format"/> uses for the rest of the line, so the decimal separator stays the same across the whole sentence.
    /// </para>
    /// <para>
    /// Only health needs this.
    /// The other three fields of the line are integers (food level, experience level and total experience) and must keep printing as integers, so they go in unchanged.
    /// </para>
    /// </summary>
    /// <param name="health">The health reading, in half-hearts as the server sends it.</param>
    internal static string FormatHealth(float health) => health.ToString("0.##", CultureInfo.CurrentCulture);

    private static int Run(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        PlayerStatus status = ctx.Run(ct => ctx.Game.Player.GetStatusAsync(ct));
        ctx.Result.SetData(status);
        return ctx.Result.Ok(ctx.Ui?.FormatPlayerStatus(status, ctx.Glyphs)
            ?? McStrings.Format("cmd.health.response", FormatHealth(status.Health), status.Food, status.ExperienceLevel, status.TotalExperience));
    }
}

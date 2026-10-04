using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>effects</c> command: lists active potion effects (entity handling required).</summary>
public sealed class EffectsCommand : CommandBase
{
    /// <summary>The vanilla translation-key prefix for a mob effect's display name.</summary>
    private const string EffectNamePrefix = "effect.minecraft.";

    /// <summary>A day in seconds. Durations at or past this read as unlimited.</summary>
    private const int SecondsPerDay = 24 * 3600;

    /// <summary>Legacy's roman-numeral table (EnchantmentMapping.ConvertLevelToRomanNumbers, line 356).</summary>
    private static readonly (string Symbol, int Value)[] RomanNumerals =
    [
        ("M", 1000), ("CM", 900), ("D", 500), ("CD", 400), ("C", 100), ("XC", 90),
        ("L", 50), ("XL", 40), ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1)
    ];

    /// <inheritdoc/>
    public override string CmdName => "effects";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.effects.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "effects";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list your active status effects"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["effects"];

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
    /// Legacy's compact duration label (EffectData.FormatShortDuration, Inventory/EffectData.cs:155-177), rebuilt on the same five keys.
    /// Negative seconds mean an infinite effect, and so does anything at or past a full day: a 30-hour beacon effect reading "30h 00m" is noise, not information.
    /// </summary>
    /// <param name="seconds">The remaining seconds, or -1 for unlimited.</param>
    internal static string FormatShortDuration(int seconds)
    {
        if (seconds < 0 || seconds >= SecondsPerDay)
            return McStrings.Get("effect.duration.short.unlimited");

        if (seconds < 60)
            return McStrings.Format("effect.duration.short.seconds", seconds);

        int minutes = seconds / 60;
        int remainingSeconds = seconds % 60;
        if (seconds < 3600)
        {
            return remainingSeconds == 0
                ? McStrings.Format("effect.duration.short.minutes", minutes)
                : McStrings.Format("effect.duration.short.minutes_seconds", minutes, remainingSeconds);
        }

        int hours = seconds / 3600;
        int remainingMinutes = seconds % 3600 / 60;
        return remainingMinutes == 0
            ? McStrings.Format("effect.duration.short.hours", hours)
            : McStrings.Format("effect.duration.short.hours_minutes", hours, remainingMinutes);
    }

    /// <summary>Renders a positive integer the way legacy rendered an effect level.</summary>
    /// <param name="number">The level; zero or less renders as an empty string, as it did in legacy.</param>
    internal static string ToRomanNumerals(int number)
    {
        StringBuilder result = new();
        foreach ((string symbol, int value) in RomanNumerals)
        {
            while (number >= value)
            {
                result.Append(symbol);
                number -= value;
            }
        }

        return result.ToString();
    }

    private static string DisplayName(CommandContext ctx, EffectSnapshot effect)
    {
        // EffectId is namespaced ("minecraft:speed"); legacy built the key from the effect enum name snake-cased, which is the same path segment.
        string id = effect.EffectId;
        int separator = id.IndexOf(':');
        string path = separator >= 0 ? id[(separator + 1)..] : id;

        string name = ctx.Translations.TryResolve(EffectNamePrefix + path, out string? translated)
            && !string.IsNullOrEmpty(translated)
                ? translated
                : path;

        return effect.Amplifier <= 0
            ? name
            : McStrings.Format("effect.name.with_amplifier", name, ToRomanNumerals(effect.Level));
    }

    private static string RemainingDuration(EffectSnapshot effect)
        => FormatShortDuration(effect.IsInfinite ? -1 : (Math.Max(0, effect.Duration) + 19) / 20);

    private int Run(CommandContext ctx)
    {
        if (!ctx.EntityEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedEntity);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        // Legacy dropped expired entries and ordered by the effect enum, which is the registry order.
        // The snapshot MCC receives carries the namespaced id and not the network id, so the ordering key here is that id; the set and the wording are legacy's.
        List<EffectSnapshot> effects =
        [
            .. ctx.Run(ct => ctx.Game.Player.GetEffectsAsync(ct))
                .Where(static effect => effect.IsInfinite || effect.Duration > 0)
                .OrderBy(static effect => effect.EffectId, StringComparer.Ordinal)
        ];

        if (effects.Count == 0)
            return ctx.Result.Ok(McStrings.Get("cmd.effects.none"));

        StringBuilder response = new();
        response.AppendLine(McStrings.Get("cmd.effects.header"));
        foreach (EffectSnapshot effect in effects)
        {
            response.AppendLine(McStrings.Format(
                "cmd.effects.entry", DisplayName(ctx, effect), RemainingDuration(effect)));
        }

        return ctx.Result.Ok(response.ToString().TrimEnd());
    }
}

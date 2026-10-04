using System.Globalization;
using DMCBK.Core.Localization;
using Umpk.Text;

namespace DMCBK.Core;

/// <summary>
/// Renders a potion effect the way the legacy client did: its translated vanilla name with a roman level, and its remaining duration in the short <c>1h 2m</c> / <c>3m 4s</c> / <c>5s</c> shapes.
/// <para>
/// Shared on purpose.
/// Three places need exactly this text and the legacy client had one set of strings for all three: the <c>effects</c> command, the TUI status bar, and the gained/expired chat notifications.
/// </para>
/// </summary>
public static class EffectText
{
    /// <summary>Vanilla ticks per second, which is what a duration is counted in on the wire.</summary>
    private const int TicksPerSecond = 20;

    /// <summary>
    /// The effect's display name: the vanilla <c>effect.minecraft.&lt;path&gt;</c> translation, with the roman level appended when the effect is above level 1 (corpus key <c>effect.name.with_amplifier</c>).
    /// Falls back to the raw id when vanilla has no entry.
    /// </summary>
    public static string DisplayName(EffectSnapshot effect, ITranslationSource translations)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(translations);

        string name = Name(effect.EffectId, translations);
        return effect.Level > 1
            ? McStrings.Format("effect.name.with_amplifier", name, Roman(effect.Level))
            : name;
    }

    /// <summary>Just the translated name, without the level.</summary>
    public static string Name(string effectId, ITranslationSource translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        if (string.IsNullOrEmpty(effectId))
            return string.Empty;

        string ns = "minecraft";
        string path = effectId;
        int colon = effectId.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            ns = effectId[..colon];
            path = effectId[(colon + 1)..];
        }

        string key = $"effect.{ns}.{path}";
        string resolved = Component.Translatable(key).ToPlainText(translations);
        return string.IsNullOrEmpty(resolved) || string.Equals(resolved, key, StringComparison.Ordinal)
            ? effectId
            : resolved;
    }

    /// <summary>
    /// The compact remaining duration used in the status bar and the gained notification: hours and minutes, minutes and seconds, or bare seconds, and the infinity sign for an endless effect.
    /// Corpus keys <c>effect.duration.short.*</c>.
    /// </summary>
    public static string ShortDuration(EffectSnapshot effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.IsInfinite || effect.Duration < 0)
            return McStrings.Get("effect.duration.short.unlimited");

        int totalSeconds = effect.Duration / TicksPerSecond;
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int seconds = totalSeconds % 60;

        if (hours > 0)
        {
            return minutes > 0
                ? McStrings.Format("effect.duration.short.hours_minutes", hours, minutes)
                : McStrings.Format("effect.duration.short.hours", hours);
        }

        if (minutes > 0)
        {
            return seconds > 0
                ? McStrings.Format("effect.duration.short.minutes_seconds", minutes, seconds)
                : McStrings.Format("effect.duration.short.minutes", minutes);
        }

        return McStrings.Format("effect.duration.short.seconds", seconds);
    }

    /// <summary>Roman numerals for the effect level, as the vanilla HUD writes them.</summary>
    public static string Roman(int level)
    {
        if (level is <= 0 or > 3999)
            return level.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            while (level >= values[i])
            {
                sb.Append(symbols[i]);
                level -= values[i];
            }
        }

        return sb.ToString();
    }
}

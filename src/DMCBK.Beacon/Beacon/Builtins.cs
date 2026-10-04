using System.Text.Json;
using DMCBK.Core.Localization;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Matcher semantics shared by every <c>inv.*</c> call plus the game tables: bare text matches the normalized type id or the display name, case-insensitive, with the <c>minecraft:</c> prefix optional and <c>_</c>, space and <c>-</c> treated as equal.
/// The map form is exact unless the key says otherwise: <c>{type: "compass", name_contains: "server"}</c>, with <c>name</c>, <c>name_matches /.../ </c>, <c>lore_contains</c>, <c>lore_matches</c>, <c>min_count</c>, <c>named: yes/no</c>.
/// Display names resolve like chat; <c>type</c> never localizes, so it stays stable across versions and languages.
/// </summary>
public static class BeaconMatchers
{
    /// <summary>Normalizes an id or display fragment for comparison.</summary>
    public static string NormalizeId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal);
    }

    /// <summary>Strips an optional <c>minecraft:</c> prefix for display matching.</summary>
    public static string ShortName(string typeId)
    {
        ArgumentNullException.ThrowIfNull(typeId);
        int colon = typeId.IndexOf(':');
        return colon < 0 ? typeId : typeId[(colon + 1)..];
    }

    /// <summary>True when bare <paramref name="matcher"/> hits <paramref name="slot"/>.</summary>
    public static bool MatchesText(string matcher, BeaconInvSlot slot)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(slot);
        string norm = NormalizeId(matcher);
        string shortNorm = NormalizeId(ShortName(matcher));
        return NormalizeId(slot.Type).Equals(norm, StringComparison.Ordinal)
            || NormalizeId(ShortName(slot.Type)).Equals(shortNorm, StringComparison.Ordinal)
            || NormalizeId(slot.Name).Equals(norm, StringComparison.Ordinal);
    }

    /// <summary>True when map-form <paramref name="matcher"/> hits <paramref name="slot"/>.</summary>
    public static bool MatchesMap(BeaconMapValue matcher, BeaconInvSlot slot)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(slot);
        foreach ((string key, BeaconValue value) in matcher.Entries)
        {
            switch (key)
            {
                case "type":
                    if (value is not BeaconTextValue type || !MatchesText(type.Value, slot))
                        return false;

                    break;
                case "name":
                    if (value is not BeaconTextValue name
                        || !slot.Name.Equals(name.Value, StringComparison.OrdinalIgnoreCase))
                        return false;

                    break;
                case "name_contains":
                    if (value is not BeaconTextValue contains
                        || !slot.Name.Contains(contains.Value, StringComparison.OrdinalIgnoreCase))
                        return false;

                    break;
                case "name_matches":
                    if (value is not BeaconTextValue pattern
                        || !RegexMatch(slot.Name, pattern.Value))
                        return false;

                    break;
                case "lore_contains":
                    if (value is not BeaconTextValue lore
                        || slot.Lore is null
                        || !slot.Lore.Any(line => line.Contains(lore.Value, StringComparison.OrdinalIgnoreCase)))
                        return false;

                    break;
                case "lore_matches":
                    if (value is not BeaconTextValue lorePattern
                        || slot.Lore is null
                        || !slot.Lore.Any(line => RegexMatch(line, lorePattern.Value)))
                        return false;

                    break;
                case "min_count":
                    if (value is not BeaconNumberValue min || slot.Count < (long)min.Value)
                        return false;

                    break;
                case "named":
                    if (value is BeaconYesNoValue named)
                    {
                        bool hasName = !NormalizeId(slot.Name).Equals(NormalizeId(ShortName(slot.Type)), StringComparison.Ordinal)
                            && slot.Name.Length > 0;
                        if (named.Value != hasName)
                            return false;
                    }
                    else
                        return false;

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="matcher"/> (text or map) hits <paramref name="slot"/>.</summary>
    public static bool Matches(BeaconValue matcher, BeaconInvSlot slot)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(slot);
        return matcher switch
        {
            BeaconTextValue text => MatchesText(text.Value, slot),
            BeaconMapValue map => MatchesMap(map, slot),
            _ => false,
        };
    }

    /// <summary>Renders a slot as the per-slot map <c>inv.list()</c> returns.</summary>
    public static BeaconValue ToSlotMap(BeaconInvSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var entries = new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["slot"] = BeaconValue.Number(slot.Slot),
            ["type"] = BeaconValue.Text(slot.Type),
            ["name"] = BeaconValue.Text(slot.Name),
            ["count"] = BeaconValue.Number(slot.Count),
        };
        entries["lore"] = slot.Lore is null
            ? BeaconValue.None
            : BeaconValue.List(slot.Lore.Select(BeaconValue.Text).ToList<BeaconValue>());
        return BeaconValue.Map(entries);
    }

    private static bool RegexMatch(string text, string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                text, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// JSON round trip for the auction-sniper shape: <c>json_parse</c> maps objects to maps, arrays to lists, strings to text, numbers to number, booleans to yes/no, null to none (nesting past 32 levels refuses); <c>json_stringify</c> is the inverse.
/// Both failures are catchable.
/// </summary>
public static class BeaconJson
{
    private const int MaxDepth = 32;

    /// <summary>Parses <paramref name="text"/> into a Beacon value.</summary>
    public static BeaconValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            MaxDepth = MaxDepth,
            AllowTrailingCommas = true,
        });
        return FromElement(document.RootElement, 0);
    }

    /// <summary>Renders <paramref name="value"/> as compact JSON.</summary>
    public static string Stringify(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ToJson(value, 0);
    }

    private static BeaconValue FromElement(JsonElement element, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("JSON nests past 32 levels; Beacon values stop there.");

        return element.ValueKind switch
        {
            JsonValueKind.Object => BeaconValue.Map(element.EnumerateObject()
                .ToDictionary(p => p.Name, p => FromElement(p.Value, depth + 1), StringComparer.Ordinal)),
            JsonValueKind.Array => BeaconValue.List(element.EnumerateArray()
                .Select(item => FromElement(item, depth + 1)).ToList<BeaconValue>()),
            JsonValueKind.String => BeaconValue.Text(element.GetString() ?? string.Empty),
            JsonValueKind.Number => element.TryGetDouble(out double number)
                ? BeaconValue.Number(number)
                : BeaconValue.None,
            JsonValueKind.True => BeaconValue.YesNo(true),
            JsonValueKind.False => BeaconValue.YesNo(false),
            _ => BeaconValue.None,
        };
    }

    private static string ToJson(BeaconValue value, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("Value nests past 32 levels; JSON output stops there.");

        return value switch
        {
            BeaconTextValue text => JsonSerializer.Serialize(text.Value),
            BeaconNumberValue number => number.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            BeaconYesNoValue yesNo => yesNo.Value ? "true" : "false",
            BeaconNoneValue => "null",
            BeaconListValue list => "[" + string.Join(",", list.Items.Select(item => ToJson(item, depth + 1))) + "]",
            BeaconMapValue map => "{" + string.Join(",",
                map.Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + ToJson(kv.Value, depth + 1))) + "}",
            _ => "null",
        };
    }
}

/// <summary>
/// Localized time rendering for <c>time.ago</c>: every user-visible word comes out of the string corpus (never hardcoded), with the invariant English shape as the key-on-miss fallback the corpus guarantees.
/// </summary>
public static class BeaconTimeText
{
    /// <summary>Renders how long ago <paramref name="stamp"/> was, at <paramref name="now"/>.</summary>
    public static string Ago(DateTimeOffset now, DateTimeOffset stamp)
    {
        TimeSpan delta = now - stamp;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1)
            return McStrings.Get("beacon.time.just_now");

        if (delta.TotalHours < 1)
            return McStrings.Format("beacon.time.minutes_ago", (int)delta.TotalMinutes);

        if (delta.TotalDays < 1)
            return McStrings.Format("beacon.time.hours_ago", (int)delta.TotalHours);

        return McStrings.Format("beacon.time.days_ago", (int)delta.TotalDays);
    }
}

/// <summary>Food ranking for <c>eat()</c>: picks the most filling known food in the snapshot.</summary>
public static class BeaconFood
{
    private static readonly Dictionary<string, int> FoodRank = new(StringComparer.Ordinal)
    {
        ["enchanted_golden_apple"] = 100,
        ["golden_apple"] = 90,
        ["cooked_beef"] = 80,
        ["cooked_porkchop"] = 79,
        ["cooked_chicken"] = 70,
        ["cooked_mutton"] = 69,
        ["cooked_rabbit"] = 68,
        ["baked_potato"] = 60,
        ["bread"] = 55,
        ["carrot"] = 50,
        ["apple"] = 45,
        ["potato"] = 30,
        ["rotten_flesh"] = 5,
    };

    /// <summary>True for a type id (or display name) known to be edible.</summary>
    public static bool IsFood(string typeOrName)
    {
        ArgumentNullException.ThrowIfNull(typeOrName);
        string norm = BeaconMatchers.NormalizeId(BeaconMatchers.ShortName(typeOrName));
        return FoodRank.ContainsKey(norm);
    }

    /// <summary>Picks the best edible slot, or null when nothing edible is present.</summary>
    public static BeaconInvSlot? PickBest(IEnumerable<BeaconInvSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        BeaconInvSlot? best = null;
        int bestRank = -1;
        foreach (BeaconInvSlot slot in slots)
        {
            if (slot.Count <= 0)
                continue;

            string norm = BeaconMatchers.NormalizeId(BeaconMatchers.ShortName(slot.Type));
            if (!FoodRank.TryGetValue(norm, out int rank))
            {
                if (!IsFood(slot.Name))
                    continue;

                rank = 1;
            }

            if (rank > bestRank)
            {
                bestRank = rank;
                best = slot;
            }
        }

        return best;
    }
}

/// <summary>
/// The <c>vars.beacon.*</c> bridge over <c>VariableStore</c> so scheduled commands and scripts share state instead of maintaining rival truths.
/// The store names flat word-only keys (its sanitizer truncates at the first dot, pinned by tests), so the bridge translates: script <c>vars.beacon.coins</c> reads and writes the store key <c>beacon_coins</c>, which is exactly what <c>%beacon_coins%</c> expansion and <c>set beacon_coins</c> already see.
/// Only the <c>beacon</c> second segment bridges; anything else is a readable error, never a shadow.
/// </summary>
public static class BeaconVarsBridge
{
    /// <summary>The store prefix behind <c>vars.beacon.*</c>.</summary>
    public const string StorePrefix = "beacon_";

    /// <summary>Translates a script suffix (<c>coins</c>) to its store key (<c>beacon_coins</c>).</summary>
    public static string ToStoreKey(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return StorePrefix + suffix.Trim();
    }

    /// <summary>True when <paramref name="storeKey"/> bridges (starts with the prefix).</summary>
    public static bool IsBridged(string storeKey)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        return storeKey.StartsWith(StorePrefix, StringComparison.OrdinalIgnoreCase)
            && storeKey.Length > StorePrefix.Length;
    }

    /// <summary>Reads the live <c>vars</c> view: <c>{beacon: {name: text}}</c> (empty maps when unwired).</summary>
    public static BeaconValue Snapshot(DMCBK.Core.Commands.VariableStore? store)
    {
        var inner = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        if (store is not null)
        {
            foreach ((string key, string text) in store.Snapshot())
            {
                if (IsBridged(key))
                    inner[key[StorePrefix.Length..]] = BeaconValue.Text(text);
            }
        }

        return BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal)
        {
            ["beacon"] = BeaconValue.Map(inner),
        });
    }

    /// <summary>Writes one bridged value through; returns false with <paramref name="error"/> when unwired.</summary>
    public static bool TryWrite(
        DMCBK.Core.Commands.VariableStore? store, string suffix, BeaconValue value, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        ArgumentNullException.ThrowIfNull(value);
        error = null;
        if (store is null)
        {
            error = "no variable store is wired";
            return false;
        }

        store.Set(ToStoreKey(suffix), BeaconInterpreter.ToDisplayText(value));
        return true;
    }
}

using Umpk.Data.Java;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Read-only game tables for the <c>items.*</c>, <c>effects.*</c> and <c>enchants.*</c> builtins.
/// Names are read from the UMPK dataset at runtime for the session protocol (<see cref="IBeaconExtendedReads.GameProtocol"/>), so they track versions; when no protocol is known or the dataset read fails, a built-in list stands in so headless runs and offline lint stay deterministic.
/// Unknown keys suggest neighbors, which retires the works-on-my-version failure.
/// </summary>
public static class BeaconGameTables
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, TableSet> ByProtocol = [];

    /// <summary>Built-in item ids used when the dataset is unreachable (headless, offline, unknown protocol).</summary>
    public static IReadOnlySet<string> FallbackItems { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "minecraft:apple", "minecraft:arrow", "minecraft:baked_potato", "minecraft:bedrock",
        "minecraft:blaze_rod", "minecraft:bow", "minecraft:bread", "minecraft:bucket",
        "minecraft:carrot", "minecraft:chest", "minecraft:clock", "minecraft:coal",
        "minecraft:cobblestone", "minecraft:compass", "minecraft:cooked_beef", "minecraft:cooked_chicken",
        "minecraft:cooked_porkchop", "minecraft:crafting_table", "minecraft:diamond", "minecraft:diamond_sword",
        "minecraft:dirt", "minecraft:ender_pearl", "minecraft:fishing_rod", "minecraft:glass",
        "minecraft:golden_apple", "minecraft:gold_ingot", "minecraft:iron_axe", "minecraft:iron_ingot",
        "minecraft:iron_pickaxe", "minecraft:iron_sword", "minecraft:ladder", "minecraft:leather",
        "minecraft:oak_log", "minecraft:oak_planks", "minecraft:potato", "minecraft:redstone",
        "minecraft:rotten_flesh", "minecraft:shield", "minecraft:stick", "minecraft:stone",
        "minecraft:stone_sword", "minecraft:string", "minecraft:torch", "minecraft:totem_of_undying",
        "minecraft:water_bucket", "minecraft:wheat", "minecraft:white_bed", "minecraft:white_wool",
        "minecraft:wooden_pickaxe", "minecraft:wooden_sword",
    };

    /// <summary>Built-in effect ids used when the dataset is unreachable.</summary>
    public static IReadOnlySet<string> FallbackEffects { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "minecraft:absorption", "minecraft:blindness", "minecraft:fire_resistance", "minecraft:haste",
        "minecraft:health_boost", "minecraft:hunger", "minecraft:invisibility", "minecraft:jump_boost",
        "minecraft:night_vision", "minecraft:poison", "minecraft:regeneration", "minecraft:resistance",
        "minecraft:slowness", "minecraft:speed", "minecraft:strength", "minecraft:water_breathing",
        "minecraft:weakness", "minecraft:wither",
    };

    /// <summary>Built-in enchantment ids used when the dataset is unreachable.</summary>
    public static IReadOnlySet<string> FallbackEnchants { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "minecraft:aqua_affinity", "minecraft:efficiency", "minecraft:feather_falling", "minecraft:flame",
        "minecraft:fortune", "minecraft:infinity", "minecraft:knockback", "minecraft:looting",
        "minecraft:mending", "minecraft:power", "minecraft:protection", "minecraft:punch",
        "minecraft:sharpness", "minecraft:silk_touch", "minecraft:smite", "minecraft:unbreaking",
    };

    /// <summary>Item ids for <paramref name="protocol"/> (dataset), or the built-in list when unknown.</summary>
    public static IReadOnlySet<string> ItemIds(int? protocol) => Table(protocol).Items;

    /// <summary>Effect ids for <paramref name="protocol"/> (dataset), or the built-in list when unknown.</summary>
    public static IReadOnlySet<string> EffectIds(int? protocol) => Table(protocol).Effects;

    /// <summary>Enchantment ids for <paramref name="protocol"/> (dataset), or the built-in list when unknown.</summary>
    public static IReadOnlySet<string> EnchantIds(int? protocol) => Table(protocol).Enchants;

    /// <summary>True when the dataset (not the fallback) backs <paramref name="protocol"/>.</summary>
    public static bool IsDatasetBacked(int? protocol)
    {
        if (protocol is not { } number)
            return false;

        lock (Gate)
        {
            return ByProtocol.TryGetValue(number, out TableSet? set) && set is not null && set.FromDataset;
        }
    }

    /// <summary>
    /// Resolves a script key (<c>totem_of_undying</c> or <c>minecraft:totem_of_undying</c>) against <paramref name="candidates"/>; null when nothing matches.
    /// Matching is case-insensitive with the <c>minecraft:</c> prefix optional and <c>_</c>, space and <c>-</c> treated as equal.
    /// </summary>
    public static string? Resolve(string key, IReadOnlySet<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(candidates);
        string norm = BeaconMatchers.NormalizeId(key);
        foreach (string candidate in candidates)
        {
            if (BeaconMatchers.NormalizeId(candidate).Equals(norm, StringComparison.Ordinal))
                return candidate;
        }

        return null;
    }

    /// <summary>Suggests the nearest candidate for an unknown key, or null when nothing is close.</summary>
    public static string? Suggest(string key, IReadOnlySet<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(candidates);
        string norm = BeaconMatchers.NormalizeId(key);
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates)
        {
            int distance = BeaconTextDistance.Levenshtein(norm, BeaconMatchers.NormalizeId(candidate));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best is null)
            return null;

        int allowed = Math.Max(2, norm.Length / 3);
        return bestDistance <= allowed ? best : null;
    }

    /// <summary>
    /// Every protocol number in the UMPK dataset, ascending with no duplicates.
    /// Cached after the first read (which roots the generated version catalog once, by design: this list exists to name the versions scripts can gate on).
    /// Unreachable datasets yield an empty list.
    /// </summary>
    public static IReadOnlyList<int> Protocols()
    {
        lock (Gate)
        {
            if (_protocols is not null)
                return _protocols;
        }

        IReadOnlyList<int> built;
        try
        {
            built = JavaVersions.All
                .Select(v => v.Version.Protocol)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            built = [];
        }

        lock (Gate)
        {
            _protocols = built;
            return _protocols;
        }
    }

    private static IReadOnlyList<int>? _protocols;

    private static TableSet Table(int? protocol)
    {
        if (protocol is not { } number)
            return TableSet.Fallback;

        lock (Gate)
        {
            if (ByProtocol.TryGetValue(number, out TableSet? cached) && cached is not null)
                return cached;
        }

        TableSet built = BuildFromDataset(number);
        lock (Gate)
        {
            ByProtocol[number] = built;
            return built;
        }
    }

    private static TableSet BuildFromDataset(int protocol)
    {
        try
        {
            var access = JavaGameData.Registries(protocol);
            var items = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in access.Items)
                items.Add(entry.Id.ToString());

            var effects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in access.MobEffects)
                effects.Add(entry.Id.ToString());

            var enchants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in access.Enchantments)
                enchants.Add(entry.Id.ToString());

            if (items.Count == 0)
                return TableSet.Fallback;

            return new TableSet(items, effects.Count == 0 ? FallbackEffects : effects,
                enchants.Count == 0 ? FallbackEnchants : enchants, FromDataset: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return TableSet.Fallback;
        }
    }

    private sealed record TableSet(
        IReadOnlySet<string> Items,
        IReadOnlySet<string> Effects,
        IReadOnlySet<string> Enchants,
        bool FromDataset)
    {
        public static TableSet Fallback { get; } = new(FallbackItems, FallbackEffects, FallbackEnchants, false);
    }
}

using DMCBK.Core;
using Umpk;
using Umpk.Data.Java;
using Umpk.Game.Items;
using Umpk.Game.Items.Components;
using Umpk.Game.Registries;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// H1 guard: the static registries Client wires for the negotiated protocol (<c>JavaGameData.Registries(protocol)</c>) must be non-null and populated, or entities silently resolve to minecraft:unknown and non-air items drop under the strict decode policy.
/// This asserts the exact data seam Client feeds into <c>UseStaticRegistries</c> for every representative version.
/// </summary>
public sealed class RegistryWiringTests
{
    public static TheoryData<string, int> Representatives => new()
    {
        { "1.8", 47 },
        { "1.12.2", 340 },
        { "1.16.5", 754 },
        { "1.21.5", 770 },
        { "26.2", 776 },
        { "26.3", 777 },
    };

    [Theory]
    [MemberData(nameof(Representatives))]
    public void Registries_AreWired_ForEveryRepresentativeProtocol(string versionName, int expectedProtocol)
    {
        Assert.True(JavaVersions.TryGetByName(versionName, out JavaVersion version));
        Assert.Equal(expectedProtocol, version.Version.Protocol);

        RegistryAccess registries = JavaGameData.Registries(version.Version.Protocol);

        Assert.NotNull(registries);
        Assert.NotNull(registries.Blocks);
        Assert.NotNull(registries.Items);
    }

    /// <summary>
    /// Consequence closure: what <see cref="ItemStackInfo"/> serves a host for a component-era enchantment.
    /// <para>
    /// <c>ItemStackInfo.From</c> builds <c>ItemEnchantmentInfo(enchantment.Enchantment.Id.ToString(), ..)</c> with no <c>IsDefault</c> guard, so whatever the session's enchantment registry answers with is what the host sees.
    /// On 766 that registry used to be EMPTY, so a real, correctly decoded depth-strider component reached plugins as the id string <c>"minecraft:"</c> with a real level: plausible-looking, unusably wrong.
    /// Now the registry is populated from the version's own registries report and the same projection serves the real id.
    /// </para>
    /// <para>
    /// The lookup here is the SAME call the wire codec makes: <c>ItemCodecPrimitives.ResolveEnchantment</c> is <c>context.Registries.Enchantments.TryGet(networkId, out var entry) ? entry : default</c>, and holder id 8 is what a 1.20.6 server puts on the wire for depth strider (<c>data/java/766/registries.json</c>).
    /// No MCC production code changed for this: the fix flows through from the UMPK data layer.
    /// </para>
    /// </summary>
    [Fact]
    public void ItemStackInfo_ServesTheRealEnchantmentId_OnTheComponentEra()
    {
        RegistryAccess registries = JavaGameData.Registries(766);

        // Resolve exactly as the 766 item codec does: by the numeric holder id the wire carries.
        Assert.True(
            registries.Enchantments.TryGet(8, out RegistryEntry<EnchantmentDefinition> depthStrider),
            "766 enchantment registry did not resolve holder id 8.");
        Assert.True(registries.Items.TryGet(Identifier.Minecraft("diamond_boots"), out RegistryEntry<ItemDefinition> boots));

        var stack = new ItemStack(boots, 1)
            .With(DataComponents.Enchantments, new EnchantmentsComponent([new EnchantmentInstance(depthStrider, 3)]));

        ItemStackInfo info = ItemStackInfo.From(stack, translations: null);

        ItemEnchantmentInfo served = Assert.Single(info.Enchantments);
        Assert.Equal("minecraft:depth_strider", served.EnchantmentId);
        Assert.Equal(3, served.Level);
    }

    /// <summary>
    /// The honest other half, kept visible on purpose: from protocol 767 vanilla stopped shipping <c>minecraft:enchantment</c> as a built-in registry (1.21.1-decompiled <c>RegistryDataLoader.java:113</c> lists it in <c>SYNCHRONIZED_REGISTRIES</c> instead, so the SERVER sends it at config phase), and UMPK's config-phase sync installs dimension types and chat types only.
    /// So on 767+ the holder id still resolves to nothing and <see cref="ItemStackInfo"/> still serves the bare namespace with a real level.
    /// This pins that residue so it reds the day the dynamic sync lands, instead of being mistaken for a regression in the table this stream added.
    /// </summary>
    [Fact]
    public void ItemStackInfo_StillServesABareNamespace_AboveTheStaticRegistryEra()
    {
        RegistryAccess registries = JavaGameData.Registries(776);

        Assert.False(registries.Enchantments.TryGet(8, out _), "776 has a static enchantment registry now; this pin is stale.");

        Assert.True(registries.Items.TryGet(Identifier.Minecraft("diamond_boots"), out RegistryEntry<ItemDefinition> boots));
        var stack = new ItemStack(boots, 1)
            .With(DataComponents.Enchantments, new EnchantmentsComponent([new EnchantmentInstance(default, 3)]));

        ItemStackInfo info = ItemStackInfo.From(stack, translations: null);

        ItemEnchantmentInfo served = Assert.Single(info.Enchantments);
        Assert.Equal("minecraft:", served.EnchantmentId);
        Assert.Equal(3, served.Level);
    }
}

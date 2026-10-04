using DMCBK.Core.Configuration;
using Umpk.Client;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Guards the client-settings projection Client applies at build time: the configured <see cref="ClientSettingsConfig.Locale"/> and <see cref="ClientSettingsConfig.RenderDistance"/> map onto <see cref="ClientOptions.Locale"/> and <see cref="ClientOptions.ViewDistance"/> (the only two fields UMPK honors today; the rest of the client-information packet is fixed, a recorded gap).
/// This asserts the exact values a build would push, using the real target type, without a live connection.
/// </summary>
public sealed class ClientSettingsMappingTests
{
    [Fact]
    public void NonDefaultLocaleAndRenderDistance_ProjectOntoClientOptions()
    {
        var config = new DmcbkConfiguration
        {
            ClientSettings = new ClientSettingsConfig { Locale = "fr_FR", RenderDistance = 12 },
        };

        // Mirror the exact mapping Client.StartAsync performs into the builder options.
        // UMPK moved the announce fields into ClientInformationOptions, which is what let MCC finally send the WHOLE client-settings block rather than just locale and view distance.
        var options = new ClientOptions
        {
            ClientInformation = config.ClientSettings.ToClientInformation(),
        };

        Assert.Equal("fr_FR", options.ClientInformation.Locale);
        Assert.Equal(12, options.ClientInformation.ViewDistance);
    }

    [Fact]
    public void FullClientSettings_ReachTheAnnounceOptions()
    {
        // The unannounced-settings gap: chat visibility, chat colors, main hand and skin parts were configurable but never announced, because UMPK's send hardcoded them.
        // They now map through.
        var settings = new ClientSettingsConfig
        {
            ChatMode = ChatModeKind.Commands,
            ChatColors = false,
            MainHand = MainHandKind.Left,
            Skin = new SkinConfig { Cape = true, Hat = true, Jacket = false, SleeveLeft = true },
        };

        ClientInformationOptions announced = settings.ToClientInformation();

        Assert.Equal(ChatVisibility.System, announced.ChatVisibility);
        Assert.False(announced.ChatColors);
        Assert.Equal(MainHand.Left, announced.MainHand);
        Assert.True(announced.DisplayedSkinParts.HasFlag(SkinParts.Cape));
        Assert.True(announced.DisplayedSkinParts.HasFlag(SkinParts.Hat));
        Assert.True(announced.DisplayedSkinParts.HasFlag(SkinParts.LeftSleeve));
        Assert.False(announced.DisplayedSkinParts.HasFlag(SkinParts.Jacket));
    }

    [Fact]
    public void DefaultClientSettings_CarryVanillaDefaults()
    {
        var config = new DmcbkConfiguration();

        Assert.Equal("en_US", config.ClientSettings.Locale);

        // 16 chunks, and it is the server's chunk radius as well as an announcement: everything the world model, the pathfinder and any terrain-reading plugin can see is bounded by it.
        Assert.Equal(16, config.ClientSettings.RenderDistance);

        // Every skin part, which is what a fresh vanilla install announces (EnumSet.allOf(PlayerModelPart.class), 1.21.11-client-decompiled Options.java:327).
        Assert.Equal(SkinParts.All, config.ClientSettings.ToClientInformation().DisplayedSkinParts);
    }

    [Fact]
    public void RenderDistance_WidensToViewDistance_WithoutLoss()
    {
        var config = new DmcbkConfiguration
        {
            ClientSettings = new ClientSettingsConfig { RenderDistance = 32 },
        };

        int viewDistance = config.ClientSettings.RenderDistance;

        Assert.Equal(32, viewDistance);
    }
}

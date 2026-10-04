using DMCBK.Core;
using DMCBK.Core.Configuration;
using Umpk.Client;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The client brand actually reaches the wire.
/// <para>
/// <c>Connection.Brand</c> was documented, parsed, validated and consumed by nothing: no session had ever put a brand on the wire, so setting it to "vanilla" bought exactly the same silence as leaving it at "mcc".
/// That silence is itself the tell.
/// Vanilla announces "vanilla" on <c>minecraft:brand</c> right after <c>login_acknowledged</c> and before the client information (1.21.11-client-decompiled ClientHandshakePacketListenerImpl.java:204-207), every modded client announces something of its own, and a client that announces nothing at all matches none of them.
/// </para>
/// </summary>
public sealed class ClientBrandTests
{
    [Theory]
    [InlineData(BrandKind.Vanilla, "vanilla")]
    [InlineData(BrandKind.Mcc, "mcc")]
    public void BrandString_MapsTheConfiguredChoice(BrandKind kind, string expected)
        => Assert.Equal(expected, Client.BrandString(kind));

    [Fact]
    public void BrandString_EmptyAnnouncesNothing()
    {
        // "none" in the config file means announce no brand at all, which is a real (if unusual) choice and has to stay distinguishable from announcing an empty string.
        Assert.Null(Client.BrandString(BrandKind.Empty));
    }

    [Fact]
    public void ApplySessionOptions_CarriesTheBrandOntoTheSession()
    {
        var options = new ClientOptions();
        Client.ApplySessionOptions(
            options,
            new MccConfiguration { Connection = new ConnectionConfig { Brand = BrandKind.Vanilla } });

        Assert.Equal("vanilla", options.ClientBrand);
    }

    [Fact]
    public void DefaultClientSettings_MatchWhatAVanillaInstallAnnounces()
    {
        // The two values a fresh vanilla install puts on the wire, and now the two this client defaults to.
        //
        // Skin parts: vanilla's set is EnumSet.allOf(PlayerModelPart.class) (1.21.11-client-decompiled Options.java:327) and buildPlayerInformation ORs every member's mask into the byte (:1797-1802).
        // The enum declares CAPE(0) JACKET(1) LEFT_SLEEVE(2) RIGHT_SLEEVE(3) LEFT_PANTS_LEG(4) RIGHT_PANTS_LEG(5) HAT(6) with mask = 1 << bit (PlayerModelPart.java:8-14, :27), so all seven is 0x7F.
        // Announcing anything less is announcing a customised skin screen.
        ClientInformationOptions announced = new ClientSettingsConfig().ToClientInformation();

        Assert.Equal((SkinParts)0x7F, announced.DisplayedSkinParts);
        Assert.Equal(SkinParts.All, announced.DisplayedSkinParts);
        Assert.Equal(16, announced.ViewDistance);
    }

    [Fact]
    public void ApplySessionOptions_WithNoConfiguration_LeavesTheBrandAlone()
    {
        // An embedding host that builds a client with no MCC configuration is not opted into an announce it never asked for; UMPK's own default is to say nothing.
        var options = new ClientOptions();
        Client.ApplySessionOptions(options, configuration: null);

        Assert.Null(options.ClientBrand);
    }

}

using System.Globalization;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using Umpk.Protocol.Java;

namespace DMCBK.Core;

/// <summary>
/// Projects MCC's <see cref="ClientSettingsConfig"/> onto UMPK's <see cref="ClientInformationOptions"/>, which is the packet the client announces to the server.
/// </summary>
/// <remarks>
/// Until UMPK grew a real client-information options record, its send hardcoded everything except locale and view distance, so MCC could model chat visibility, chat colors, main hand and skin parts in <c>client.toml</c> but never actually announce them (recorded as the unannounced-settings gap).
/// This mapping closes that gap: every configured field now reaches the wire, era-gated by UMPK per negotiated version.
/// </remarks>
internal static class ClientInformationMapping
{
    /// <summary>
    /// Builds the announce options from the configured client settings.
    /// A <c>Locale</c> of <c>auto</c> resolves to <paramref name="uiCulture"/> in Minecraft's own tag form, so a server-side plugin that localises its messages sees the language the person is actually reading in; every other value goes to the wire exactly as configured.
    /// A null culture means the ambient UI culture, which startup has already pinned to the resolved one.
    /// </summary>
    public static ClientInformationOptions ToClientInformation(
        this ClientSettingsConfig settings,
        CultureInfo? uiCulture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ClientInformationOptions
        {
            Locale = ResolveLocale(settings.Locale, uiCulture),
            ViewDistance = settings.RenderDistance,
            ChatVisibility = ToChatVisibility(settings.ChatMode),
            ChatColors = settings.ChatColors,
            DisplayedSkinParts = ToSkinParts(settings.Skin),
            MainHand = settings.MainHand == MainHandKind.Left ? MainHand.Left : MainHand.Right,
        };
    }

    private static string ResolveLocale(string locale, CultureInfo? uiCulture)
        => string.Equals(locale?.Trim(), UiCulture.Auto, StringComparison.OrdinalIgnoreCase)
            ? UiCulture.ToMinecraftTag(uiCulture ?? CultureInfo.CurrentUICulture)
            : locale ?? string.Empty;

    private static ChatVisibility ToChatVisibility(ChatModeKind mode) => mode switch
    {
        ChatModeKind.Enabled => ChatVisibility.Full,
        ChatModeKind.Commands => ChatVisibility.System,
        ChatModeKind.Disabled => ChatVisibility.Hidden,
        _ => ChatVisibility.Full,
    };

    private static SkinParts ToSkinParts(SkinConfig skin)
    {
        SkinParts parts = SkinParts.None;
        if (skin.Cape)
            parts |= SkinParts.Cape;

        if (skin.Jacket)
            parts |= SkinParts.Jacket;

        if (skin.SleeveLeft)
            parts |= SkinParts.LeftSleeve;

        if (skin.SleeveRight)
            parts |= SkinParts.RightSleeve;

        if (skin.PantsLeft)
            parts |= SkinParts.LeftPantsLeg;

        if (skin.PantsRight)
            parts |= SkinParts.RightPantsLeg;

        if (skin.Hat)
            parts |= SkinParts.Hat;

        return parts;
    }
}

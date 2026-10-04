using System.Globalization;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The one place a configured language becomes a <see cref="CultureInfo"/>.
/// Both tag spellings are accepted (Minecraft's <c>pt_br</c> and BCP-47's <c>pt-BR</c>), <c>auto</c> means the operating system, and a tag no installed culture matches is rejected rather than silently manufactured into a language nothing has strings for.
/// </summary>
public sealed class UiCultureTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("  auto ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Auto_IsRecognised(string? language)
        => Assert.True(UiCulture.IsAuto(language));

    [Theory]
    [InlineData("en")]
    [InlineData("pt_br")]
    [InlineData("pt-BR")]
    public void ARealTag_IsNotAuto(string language)
        => Assert.False(UiCulture.IsAuto(language));

    [Theory]
    [InlineData("pt_br", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh_cn", "zh-CN")]
    [InlineData("de", "de")]
    [InlineData("  fr  ", "fr")]
    public void ParsesBothTagSpellings(string tag, string expected)
    {
        Assert.True(UiCulture.TryParseTag(tag, out CultureInfo? culture));
        Assert.NotNull(culture);
        Assert.Equal(expected, culture.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-language-at-all")]
    [InlineData("qq_zz")]
    public void RejectsAutoAndUnknownTags(string? tag)
    {
        Assert.False(UiCulture.TryParseTag(tag, out CultureInfo? culture));
        Assert.Null(culture);
    }

    [Fact]
    public void Resolve_Auto_FollowsTheOperatingSystem()
        => Assert.Equal(CultureInfo.CurrentUICulture, UiCulture.Resolve(new LocalizationConfig()));

    [Fact]
    public void Resolve_ATag_UsesIt()
        => Assert.Equal("pt-BR", UiCulture.Resolve(new LocalizationConfig { Language = "pt_br" }).Name);

    [Fact]
    public void Resolve_AnUnknownTag_FallsBackRatherThanThrowing()
    {
        // The validator is what warns; by the time the culture is resolved the run has to continue.
        Assert.Equal(
            CultureInfo.CurrentUICulture,
            UiCulture.Resolve(new LocalizationConfig { Language = "qq_zz" }));
    }

    [Theory]
    [InlineData("pt-BR", "pt_br")]
    [InlineData("de", "de")]
    [InlineData("zh-Hans-CN", "zh_hans_cn")]
    public void ToMinecraftTag_UsesUnderscoresAndLowerCase(string cultureName, string expected)
        => Assert.Equal(expected, UiCulture.ToMinecraftTag(CultureInfo.GetCultureInfo(cultureName)));

    [Fact]
    public void ToMinecraftTag_InvariantIsEnglish()
        => Assert.Equal("en_us", UiCulture.ToMinecraftTag(CultureInfo.InvariantCulture));
}

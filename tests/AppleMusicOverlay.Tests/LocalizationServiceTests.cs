using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void SupportedLanguagesExposeMatchingResourceKeys()
    {
        IReadOnlySet<string> zhKeys = LocalizationService.GetResourceKeys("zh-CN");
        IReadOnlySet<string> enKeys = LocalizationService.GetResourceKeys("en-US");

        Assert.NotEmpty(zhKeys);
        Assert.Equal(zhKeys.OrderBy(key => key), enKeys.OrderBy(key => key));
    }

    [Fact]
    public void UnknownLanguageFallsBackToSimplifiedChinese()
    {
        var localization = new LocalizationService();

        localization.SetLanguage("fr-FR");

        Assert.Equal("zh-CN", localization.LanguageCode);
        Assert.Equal("常用选项", localization["NavGeneral"]);
    }

    [Fact]
    public void LanguageChangeUsesTargetLanguageForFeedback()
    {
        var localization = new LocalizationService();

        localization.SetLanguage("en-US");

        Assert.Equal("Language changed", localization["GeneralLanguageChangedTitle"]);
        Assert.Equal("The interface language is now English.", localization["GeneralLanguageChangedMessage"]);
    }
}

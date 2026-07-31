using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Translations;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class TranslationServiceTests
{
    [Theory]
    [InlineData("pl-PL", AppLanguage.Polish)]
    [InlineData("de-AT", AppLanguage.Deutsch)]
    [InlineData("pt-PT", AppLanguage.PortugeseBrazil)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("tr_TR", AppLanguage.Turkish)]
    [InlineData("ko-KR", AppLanguage.Korean)]
    [InlineData("nl-NL", AppLanguage.Dutch)]
    [InlineData("bg-BG", AppLanguage.Bulgarian)]
    [InlineData("ca-ES", AppLanguage.Catalan)]
    [InlineData("da-DK", AppLanguage.Danish)]
    [InlineData("fi-FI", AppLanguage.Finnish)]
    [InlineData("gl-ES", AppLanguage.Galician)]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("lt-LT", AppLanguage.Lithuanian)]
    [InlineData("sk-SK", AppLanguage.Slovak)]
    [InlineData("uk-UA", AppLanguage.Ukrainian)]
    public void ResolveSupportedLanguage_ReturnsExpectedLanguage(string languageTag, AppLanguage expectedLanguage)
    {
        var result = TranslationService.ResolveSupportedLanguage([languageTag]);

        result.Should().Be(expectedLanguage);
    }

    [Fact]
    public void GetAvailableLanguages_ReturnsLanguagesWithEmbeddedResources()
    {
        var service = new TranslationService(null, null);

        foreach (var language in service.GetAvailableLanguages())
        {
            using var stream = WinoTranslationDictionary.GetLanguageStream(language.Language);

            stream.Should().NotBeNull($"the {language.Code} translation is listed as an app language");
        }
    }
}

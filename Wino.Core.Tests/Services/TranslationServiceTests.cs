using FluentAssertions;
using Wino.Core.Domain.Enums;
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
    [InlineData("nl-NL", AppLanguage.English)]
    public void ResolveSupportedLanguage_ReturnsExpectedLanguage(string languageTag, AppLanguage expectedLanguage)
    {
        var result = TranslationService.ResolveSupportedLanguage([languageTag]);

        result.Should().Be(expectedLanguage);
    }
}

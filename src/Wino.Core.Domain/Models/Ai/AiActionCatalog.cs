using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Ai;

/// <summary>
/// The fixed lists of translation languages and rewrite modes the AI actions offer.
/// </summary>
public static class AiActionCatalog
{
    public static IReadOnlyList<AiTranslateLanguageOption> GetTranslateLanguageOptions()
    {
        return new AiTranslateLanguageOption[]
        {
            new("en-US", Translator.Composer_AiTranslateLanguageEnglish),
            new("tr-TR", Translator.Composer_AiTranslateLanguageTurkish),
            new("de-DE", Translator.Composer_AiTranslateLanguageGerman),
            new("fr-FR", Translator.Composer_AiTranslateLanguageFrench),
            new("es-ES", Translator.Composer_AiTranslateLanguageSpanish),
            new("it-IT", Translator.Composer_AiTranslateLanguageItalian),
            new("pt-BR", Translator.Composer_AiTranslateLanguagePortugueseBrazil),
            new("nl-NL", Translator.Composer_AiTranslateLanguageDutch),
            new("pl-PL", Translator.Composer_AiTranslateLanguagePolish),
            new("uk-UA", Translator.Composer_AiTranslateLanguageUkrainian),
            new("ru-RU", Translator.Composer_AiTranslateLanguageRussian),
            new("ja-JP", Translator.Composer_AiTranslateLanguageJapanese),
            new("ko-KR", Translator.Composer_AiTranslateLanguageKorean),
            new("zh-CN", Translator.Composer_AiTranslateLanguageChineseSimplified),
            new("ar-SA", Translator.Composer_AiTranslateLanguageArabic),
            new("hi-IN", Translator.Composer_AiTranslateLanguageHindi),
        };
    }

    public static IReadOnlyList<AiRewriteModeOption> GetRewriteModeOptions()
    {
        return new AiRewriteModeOption[]
        {
            new("polite", Translator.Composer_AiRewritePolite, Translator.Composer_AiRewritePoliteDescription),
            new("angry", Translator.Composer_AiRewriteAngry, Translator.Composer_AiRewriteAngryDescription),
            new("happy", Translator.Composer_AiRewriteHappy, Translator.Composer_AiRewriteHappyDescription),
            new("formal", Translator.Composer_AiRewriteFormal, Translator.Composer_AiRewriteFormalDescription),
            new("friendly", Translator.Composer_AiRewriteFriendly, Translator.Composer_AiRewriteFriendlyDescription),
            new("shorter", Translator.Composer_AiRewriteShorter, Translator.Composer_AiRewriteShorterDescription),
            new("clearer", Translator.Composer_AiRewriteClearer, Translator.Composer_AiRewriteClearerDescription),
        };
    }

    /// <summary>
    /// Modes offered when rewriting a received message in the reading pane. The order puts the
    /// reading aids first, and "angry" is left out because it does not help anyone read. Every mode
    /// here must also be in the API's <c>AiEmail:AllowedRewriteModes</c> list, or the request fails
    /// validation. The composer offers the full <see cref="GetRewriteModeOptions"/> list, which
    /// matches that API list exactly.
    /// </summary>
    public static IReadOnlyList<AiRewriteModeOption> GetReaderRewriteModeOptions()
    {
        var options = GetRewriteModeOptions();
        var result = new List<AiRewriteModeOption>(ReaderRewriteModes.Length);
        foreach (var mode in ReaderRewriteModes)
        {
            foreach (var option in options)
            {
                if (option.Mode == mode)
                {
                    result.Add(option);
                    break;
                }
            }
        }

        return result;
    }

    private static readonly string[] ReaderRewriteModes = ["clearer", "shorter", "formal", "friendly", "polite", "happy"];
}

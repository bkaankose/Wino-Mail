using System.Collections.Generic;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;

namespace Wino.Mail.WinUI.Services;

public sealed class AiActionOptionsService : IAiActionOptionsService
{
    public IReadOnlyList<AiTranslateLanguageOption> GetTranslateLanguageOptions()
    {
        return
        [
            new("en-US", Translator.Composer_AiTranslateLanguageEnglish),
            new("tr-TR", Translator.Composer_AiTranslateLanguageTurkish),
            new("de-DE", Translator.Composer_AiTranslateLanguageGerman),
            new("fr-FR", Translator.Composer_AiTranslateLanguageFrench),
            new("es-ES", Translator.Composer_AiTranslateLanguageSpanish),
            new("it-IT", Translator.Composer_AiTranslateLanguageItalian),
            new("pt-BR", Translator.Composer_AiTranslateLanguagePortugueseBrazil),
            new("nl-NL", Translator.Composer_AiTranslateLanguageDutch),
            new("pl-PL", Translator.Composer_AiTranslateLanguagePolish),
            new("ru-RU", Translator.Composer_AiTranslateLanguageRussian),
            new("ja-JP", Translator.Composer_AiTranslateLanguageJapanese),
            new("ko-KR", Translator.Composer_AiTranslateLanguageKorean),
            new("zh-CN", Translator.Composer_AiTranslateLanguageChineseSimplified),
            new("ar-SA", Translator.Composer_AiTranslateLanguageArabic),
            new("hi-IN", Translator.Composer_AiTranslateLanguageHindi),
        ];
    }

    public IReadOnlyList<AiRewriteModeOption> GetRewriteModeOptions()
    {
        return
        [
            new("polite", Translator.Composer_AiRewritePolite, Translator.Composer_AiRewritePoliteDescription),
            new("angry", Translator.Composer_AiRewriteAngry, Translator.Composer_AiRewriteAngryDescription),
            new("happy", Translator.Composer_AiRewriteHappy, Translator.Composer_AiRewriteHappyDescription),
            new("formal", Translator.Composer_AiRewriteFormal, Translator.Composer_AiRewriteFormalDescription),
            new("friendly", Translator.Composer_AiRewriteFriendly, Translator.Composer_AiRewriteFriendlyDescription),
            new("shorter", Translator.Composer_AiRewriteShorter, Translator.Composer_AiRewriteShorterDescription),
            new("clearer", Translator.Composer_AiRewriteClearer, Translator.Composer_AiRewriteClearerDescription),
            new(string.Empty, Translator.Composer_AiRewriteCustom, Translator.Composer_AiRewriteCustomDescription, true),
        ];
    }
}

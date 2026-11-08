#nullable enable
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// One feature of the Wino Intelligence subscription as the settings page lists it. <see cref="TrailingText"/>
/// is the monthly allowance, or the live usage once the API reports the feature's bucket.
/// </summary>
public sealed record IntelligencePerk(string Bucket, WinoIconGlyph Icon, string Title, string Description, string TrailingText);

public static class IntelligenceOfferCatalog
{
    // The API reports allowances only to subscribers, so the offer states them as fixed numbers until it can
    // send them to signed-out users too.
    public const int IntelligenceMessagesPerMonth = 2500;
    public const int SummariesPerMonth = 200;
    public const int TranslationsPerMonth = 300;
    public const int RewritesPerMonth = 150;
    public const int VoiceCommandsPerMonth = 300;

    public static IntelligencePerk[] CreatePerks() =>
    [
        new(AiQuotaBucketIds.Intelligence, WinoIconGlyph.DailyBriefing,
            Translator.WinoIntelligence_Perk_Briefing_Title,
            Translator.WinoIntelligence_Perk_Briefing_Description,
            Translator.WinoIntelligence_Perk_Briefing_Allowance),
        new(string.Empty, WinoIconGlyph.Tag,
            Translator.WinoIntelligence_Perk_Labels_Title,
            Translator.WinoIntelligence_Perk_Labels_Description,
            Translator.WinoIntelligence_Perk_Labels_Allowance),
        new(AiQuotaBucketIds.Summarize, WinoIconGlyph.Note,
            Translator.WinoIntelligence_Perk_Summaries_Title,
            Translator.WinoIntelligence_Perk_Summaries_Description,
            Translator.WinoIntelligence_Perk_Summaries_Allowance),
        new(AiQuotaBucketIds.Translate, WinoIconGlyph.Translate,
            Translator.WinoIntelligence_Perk_Translations_Title,
            Translator.WinoIntelligence_Perk_Translations_Description,
            Translator.WinoIntelligence_Perk_Translations_Allowance),
        new(AiQuotaBucketIds.Rewrite, WinoIconGlyph.Edit,
            Translator.WinoIntelligence_Perk_Rewrites_Title,
            Translator.WinoIntelligence_Perk_Rewrites_Description,
            Translator.WinoIntelligence_Perk_Rewrites_Allowance),
        new(IntelligenceUsage.AssistantBucket, WinoIconGlyph.Microphone,
            Translator.WinoIntelligence_Perk_Voice_Title,
            Translator.WinoIntelligence_Perk_Voice_Description,
            Translator.WinoIntelligence_Perk_Voice_Allowance),
    ];
}

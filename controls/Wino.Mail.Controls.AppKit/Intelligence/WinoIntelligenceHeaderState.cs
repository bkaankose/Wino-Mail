using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.Controls.AppKit.Intelligence;

/// <summary>
/// Everything the native Wino Intelligence header shows, already localized. The reader builds it
/// from WinoIntelligenceHeaderPresenter on every presenter change and hands it to
/// <see cref="WinoIntelligenceHeaderView.Update"/>. Mirrors the dependency properties of the
/// Windows WinoIntelligenceHeader control.
/// </summary>
public sealed record WinoIntelligenceHeaderState(
    string ContentKey,
    WinoIntelligenceProcessingState ProcessingState,
    bool IsSummaryAvailable,
    bool IsTranslateAvailable,
    bool IsProcessingAvailable,
    string BriefingFactText,
    string DeadlineText,
    string DeadlineDetailText,
    string VerificationCode,
    IReadOnlyList<WinoIntelligenceTile> Tiles,
    string SummaryText,
    WinoIntelligenceFeatureState SummaryState,
    IReadOnlyList<WinoIntelligenceLanguageOption> Languages,
    string SelectedSourceLanguage,
    string SelectedTargetLanguage,
    bool IsTranslationBusy,
    bool HasTranslationResult,
    bool IsTranslationApplied,
    string TranslationStatusText)
{
    public static WinoIntelligenceHeaderState Empty { get; } = new(
        string.Empty, WinoIntelligenceProcessingState.NotProcessed, false, false, false,
        string.Empty, string.Empty, string.Empty, string.Empty, [], string.Empty, WinoIntelligenceFeatureState.Idle,
        [], string.Empty, string.Empty, false, false, false, string.Empty);

    /// <summary>Processed, so passive insights (briefing, deadline) may be shown.</summary>
    public bool HasInsights => IsProcessingAvailable && ProcessingState == WinoIntelligenceProcessingState.Processed;

    public bool CanRequestProcessing => IsProcessingAvailable
        && ProcessingState is WinoIntelligenceProcessingState.NotProcessed or WinoIntelligenceProcessingState.Failed;

    public bool IsProcessingRunning => IsProcessingAvailable
        && ProcessingState is WinoIntelligenceProcessingState.Queued or WinoIntelligenceProcessingState.Processing;

    public bool HasBriefingFact => HasInsights && !string.IsNullOrWhiteSpace(BriefingFactText);

    public bool HasDeadline => HasInsights && !string.IsNullOrWhiteSpace(DeadlineText);

    /// <summary>The body has something to show: a feature, the processing prompt or a passive fact.</summary>
    public bool CanExpand => IsSummaryAvailable || IsTranslateAvailable || CanRequestProcessing || HasDeadline;
}

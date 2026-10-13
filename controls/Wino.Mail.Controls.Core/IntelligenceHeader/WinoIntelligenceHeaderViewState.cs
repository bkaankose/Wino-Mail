namespace Wino.Mail.Controls.Core.IntelligenceHeader;

/// <summary>The detail panel a native intelligence header shows; at most one is open.</summary>
public enum WinoIntelligenceHeaderPanel
{
    None,
    Summary,
    Translate
}

/// <summary>What the translation panel's run button does (and says).</summary>
public enum WinoIntelligenceTranslateRunKind
{
    Translate,
    TranslateAgain,
    ShowOriginal
}

/// <summary>
/// The view state of a native Wino Intelligence header (AppKit and UIKit WinoIntelligenceHeaderView): whether the body
/// is expanded, which detail panel is open, and the visibility and enabled rules derived from the presenter's
/// <see cref="WinoIntelligenceHeaderState"/>. Views apply it; user actions go through the methods, which return whether
/// the host should be asked for something.
/// </summary>
public sealed class WinoIntelligenceHeaderViewState
{
    private bool _expanded;
    private WinoIntelligenceHeaderPanel _openPanel;

    public WinoIntelligenceHeaderState State { get; private set; } = WinoIntelligenceHeaderState.Empty;

    /// <summary>The source picker lists "Detect" and every language.</summary>
    public WinoIntelligenceLanguageOption[] SourceOptions { get; private set; } = [];

    /// <summary>The target picker omits "Detect" (the option without a code).</summary>
    public WinoIntelligenceLanguageOption[] TargetOptions { get; private set; } = [];

    /// <summary>Whether the body is shown; false while there is nothing to expand.</summary>
    public bool IsExpanded => _expanded && State.CanExpand;

    public WinoIntelligenceHeaderPanel OpenPanel => _openPanel;

    /// <summary>
    /// Applies a presenter state. A new content key collapses the header and closes any panel. Returns which inputs
    /// changed by reference, so the view rebuilds its tiles or language pickers only when needed.
    /// </summary>
    public (bool TilesChanged, bool LanguagesChanged) Update(WinoIntelligenceHeaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.Equals(state.ContentKey, State.ContentKey, StringComparison.Ordinal))
        {
            _expanded = false;
            _openPanel = WinoIntelligenceHeaderPanel.None;
        }

        bool tilesChanged = !ReferenceEquals(state.Tiles, State.Tiles);
        bool languagesChanged = !ReferenceEquals(state.Languages, State.Languages);
        State = state;
        if (languagesChanged)
        {
            SourceOptions = state.Languages.ToArray();
            TargetOptions = state.Languages.Where(language => !string.IsNullOrWhiteSpace(language.Code)).ToArray();
        }

        Normalize();
        return (tilesChanged, languagesChanged);
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Normalize();
    }

    /// <summary>Flips the body; collapsing closes the open panel. False when there is nothing to expand.</summary>
    public bool ToggleExpanded()
    {
        if (!State.CanExpand) return false;

        _expanded = !_expanded;
        if (!_expanded) _openPanel = WinoIntelligenceHeaderPanel.None;
        Normalize();
        return true;
    }

    /// <summary>The Summarize entry point: opens the summary panel. True when a summary should be requested (none yet).</summary>
    public bool ClickSummary()
    {
        if (!State.IsSummaryAvailable) return false;

        Open(WinoIntelligenceHeaderPanel.Summary);
        return State.SummaryState == WinoIntelligenceFeatureState.Idle;
    }

    /// <summary>Expands the body and opens the summary panel; true when a summary should be requested.</summary>
    public bool ShowSummary()
    {
        _expanded = true;
        return ClickSummary();
    }

    /// <summary>Expands the body and opens the translate panel.</summary>
    public void ShowTranslation() => Open(WinoIntelligenceHeaderPanel.Translate);

    public void Open(WinoIntelligenceHeaderPanel panel)
    {
        _expanded = true;
        _openPanel = panel;
        Normalize();
    }

    public void Close(WinoIntelligenceHeaderPanel panel)
    {
        if (_openPanel == panel) _openPanel = WinoIntelligenceHeaderPanel.None;
        Normalize();
    }

    /// <summary>Cancel in the summary panel: closes it. True when a running summary should be canceled.</summary>
    public bool CancelSummary()
    {
        if (State.SummaryState != WinoIntelligenceFeatureState.Busy) return false;

        _openPanel = WinoIntelligenceHeaderPanel.None;
        Normalize();
        return true;
    }

    /// <summary>Cancel in the translate panel: closes it. True when a running translation should be canceled.</summary>
    public bool CancelTranslation()
    {
        if (!State.IsTranslationBusy) return false;

        Close(WinoIntelligenceHeaderPanel.Translate);
        return true;
    }

    /// <summary>Regenerate is allowed while a summary is available and not running.</summary>
    public bool CanRequestSummary => State.IsSummaryAvailable;

    /// <summary>The run button may start a translation only while one is available and none is running.</summary>
    public bool CanRequestTranslation => State.IsTranslateAvailable && !State.IsTranslationBusy;

    public bool IsSummaryPanelOpen => IsExpanded && _openPanel == WinoIntelligenceHeaderPanel.Summary;

    public bool IsTranslatePanelOpen => IsExpanded && _openPanel == WinoIntelligenceHeaderPanel.Translate;

    public bool IsSummaryWaitingShown => IsSummaryPanelOpen && State.SummaryState == WinoIntelligenceFeatureState.Busy;

    public bool IsSummaryTextShown => IsSummaryPanelOpen && State.SummaryState == WinoIntelligenceFeatureState.Done;

    public bool CanCopySummary => IsSummaryTextShown && !string.IsNullOrWhiteSpace(State.SummaryText);

    public bool CanRegenerateSummary => State.IsSummaryAvailable && !IsSummaryWaitingShown;

    public bool IsTranslationWaitingShown => IsTranslatePanelOpen && State.IsTranslationBusy;

    public bool IsTranslationAppliedShown => IsTranslatePanelOpen && State.HasTranslationResult && !State.IsTranslationBusy;

    public WinoIntelligenceTranslateRunKind TranslateRunKind
        => State.IsTranslationApplied ? WinoIntelligenceTranslateRunKind.ShowOriginal
            : State.HasTranslationResult ? WinoIntelligenceTranslateRunKind.TranslateAgain
            : WinoIntelligenceTranslateRunKind.Translate;

    /// <summary>The run button is off while translating and while the source equals the target.</summary>
    public bool IsTranslateRunEnabled
        => !State.IsTranslationBusy
            && (string.IsNullOrWhiteSpace(State.SelectedSourceLanguage)
                || !string.Equals(State.SelectedSourceLanguage, State.SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase));

    /// <summary>The label of the selected target language, or null when it is not in the list.</summary>
    public string? TargetLanguageLabel
        => State.Languages.FirstOrDefault(language => string.Equals(language.Code, State.SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase))?.Label;

    /// <summary>The picker row for a language code (empty means detect), or -1.</summary>
    public static int LanguageIndex(IReadOnlyList<WinoIntelligenceLanguageOption> options, string? code)
    {
        ArgumentNullException.ThrowIfNull(options);
        for (int index = 0; index < options.Count; index++)
            if (string.Equals(options[index].Code, code ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    /// <summary>Drops an expansion or panel the current state no longer allows.</summary>
    private void Normalize()
    {
        if (!State.CanExpand)
        {
            _expanded = false;
            _openPanel = WinoIntelligenceHeaderPanel.None;
        }

        if (_openPanel == WinoIntelligenceHeaderPanel.Summary && !State.IsSummaryAvailable) _openPanel = WinoIntelligenceHeaderPanel.None;
        if (_openPanel == WinoIntelligenceHeaderPanel.Translate && !State.IsTranslateAvailable) _openPanel = WinoIntelligenceHeaderPanel.None;
    }
}

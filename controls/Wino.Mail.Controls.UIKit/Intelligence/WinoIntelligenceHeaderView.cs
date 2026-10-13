using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.Controls.UIKit.ToDo;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Intelligence;

/// <summary>
/// Native Wino Intelligence header of the reader (UIKit twin of the AppKit <c>WinoIntelligenceHeaderView</c>, same
/// events and methods): a card with the sparkle, title and briefing headline (or the processing status) beside Copy code,
/// Process and the disclosure chevron; the intelligence tiles; and a collapsible body with the Summarize and Translate
/// entry points, each opening its panel (summary text, or the language menus and status). The view is passive:
/// <see cref="Update"/> applies a <see cref="WinoIntelligenceHeaderState"/>; expansion, the open panel and every
/// visibility rule come from the shared <see cref="WinoIntelligenceHeaderViewState"/>.
/// </summary>
public sealed class WinoIntelligenceHeaderView : WinoSurfaceView
{
    private readonly WinoIntelligenceHeaderViewState _view = new();
    private bool _synchronizing;

    private readonly UILabel _title;
    private readonly UILabel _briefing;
    private readonly UILabel _subtitle;
    private readonly UIActivityIndicatorView _processingSpinner;
    private readonly UILabel _processingStatus;
    private readonly UIStackView _processingRow;
    private readonly UIButton _copyCodeButton;
    private readonly UIButton _processButton;
    private readonly UIButton _chevron;
    private readonly WinoFlowView _tiles;
    private readonly UIStackView _body;
    private readonly UILabel _deadline;
    private readonly UILabel _insightsLocked;
    private readonly UIStackView _featureRow;
    private readonly WinoChipButton _summaryChip;
    private readonly WinoChipButton _translateChip;

    private readonly UIStackView _summaryPanel;
    private readonly UIStackView _summaryWaitingRow;
    private readonly UIActivityIndicatorView _summarySpinner;
    private readonly UILabel _summaryText;
    private readonly UIButton _summaryCopyButton;
    private readonly UIButton _summaryRegenerateButton;

    private readonly UIStackView _translatePanel;
    private readonly UIButton _sourcePicker;
    private readonly UIButton _targetPicker;
    private readonly UIButton _translationRunButton;
    private readonly UIStackView _translationWaitingRow;
    private readonly UIActivityIndicatorView _translationSpinner;
    private readonly UILabel _translationBusyText;
    private readonly UIStackView _translationAppliedRow;
    private readonly UILabel _translationStatusText;

    /// <summary>Process (or Retry) was tapped.</summary>
    public event EventHandler? ProcessRequested;

    /// <summary>Copy code was tapped; the host copies the verification code.</summary>
    public event EventHandler? CopyCodeRequested;

    /// <summary>A summary was requested (first tap or Regenerate).</summary>
    public event EventHandler? SummaryRequested;

    /// <summary>The running summary was canceled.</summary>
    public event EventHandler? SummaryCancelRequested;

    /// <summary>Translate / Show original / Translate again was tapped.</summary>
    public event EventHandler? TranslateRequested;

    /// <summary>The running translation was canceled.</summary>
    public event EventHandler? TranslationCancelRequested;

    /// <summary>The source language changed; the argument is its code (empty means detect).</summary>
    public event EventHandler<string>? SourceLanguageChanged;

    /// <summary>The target language changed; the argument is its code.</summary>
    public event EventHandler<string>? TargetLanguageChanged;

    public WinoIntelligenceHeaderView()
    {
        CornerRadius = WinoStyle.GroupRadius + 2;
        Fill = WinoBriefingCardView.CardFill;
        Stroke = WinoBriefingCardView.CardStroke;

        // ---- Header row ----
        var sparkle = new WinoIconView(WinoIconGlyph.Sparkle, 18, WinoStyle.Accent);
        _title = WinoStyle.Label(Translator.WinoIntelligence_HeaderTitle, WinoStyle.BodyStrong);
        _briefing = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 2);
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        var titleBlock = WinoLayout.VStack(2, _title, _briefing, _subtitle);
        titleBlock.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(titleBlock, UILayoutConstraintAxis.Horizontal, 1, 200);
        // The title block toggles the body, like the whole Windows header row.
        titleBlock.AddGestureRecognizer(new UITapGestureRecognizer(ToggleExpanded));

        _processingSpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        _processingStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _processingRow = WinoLayout.HStack(6, _processingSpinner, _processingStatus);

        _copyCodeButton = TextButton(Translator.Intelligence_ActionCopyCode, WinoIconGlyph.Copy);
        _copyCodeButton.PrimaryActionTriggered += (_, _) => { if (!string.IsNullOrWhiteSpace(CurrentState.VerificationCode)) CopyCodeRequested?.Invoke(this, EventArgs.Empty); };
        _processButton = TextButton(Translator.WinoIntelligence_Process, WinoIconGlyph.Sparkle);
        WinoAccessibility.Help(_processButton, Translator.WinoIntelligence_ProcessAutomation);
        _processButton.PrimaryActionTriggered += (_, _) => { if (CurrentState.CanRequestProcessing) ProcessRequested?.Invoke(this, EventArgs.Empty); };
        _chevron = WinoToDoStyle.IconButton(WinoIconGlyph.ChevronDown, Translator.WinoIntelligence_HeaderTitle, 13, 36, 36, WinoStyle.SecondaryText);
        _chevron.PrimaryActionTriggered += (_, _) => ToggleExpanded();

        var headerRow = WinoLayout.HStack(10, sparkle, titleBlock, _processingRow, _copyCodeButton, _processButton, _chevron);
        headerRow.Distribution = UIStackViewDistribution.Fill;

        // ---- Tiles ----
        _tiles = new WinoFlowView { Spacing = 6, LineSpacing = 6 };

        // ---- Body ----
        _deadline = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 0);
        _insightsLocked = WinoStyle.Label(Translator.WinoIntelligence_InsightsLocked, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _summaryChip = new WinoChipButton(WinoIconGlyph.TextDescription, Translator.WinoIntelligence_Summarize);
        _summaryChip.Clicked += (_, _) => SummaryChipClicked();
        _translateChip = new WinoChipButton(WinoIconGlyph.Translate, Translator.WinoIntelligence_Translate);
        _translateChip.Clicked += (_, _) => OpenPanel(WinoIntelligenceHeaderPanel.Translate);
        _featureRow = WinoLayout.HStack(8, _summaryChip, _translateChip, WinoLayout.Spacer());

        // ---- Summary panel ----
        var summaryHeading = WinoStyle.Label(Translator.WinoIntelligence_Summary, WinoStyle.BodyStrong);
        _summaryCopyButton = WinoToDoStyle.IconButton(WinoIconGlyph.Copy, Translator.WinoIntelligence_Copy, 14, 36, 36, WinoStyle.SecondaryText);
        _summaryCopyButton.PrimaryActionTriggered += (_, _) => CopySummary();
        _summaryRegenerateButton = WinoToDoStyle.IconButton(WinoIconGlyph.ArrowClockwise, Translator.WinoIntelligence_Regenerate, 14, 36, 36, WinoStyle.SecondaryText);
        _summaryRegenerateButton.PrimaryActionTriggered += (_, _) => { if (_view.CanRequestSummary) SummaryRequested?.Invoke(this, EventArgs.Empty); };
        var summaryClose = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel, 13, 36, 36, WinoStyle.SecondaryText);
        summaryClose.PrimaryActionTriggered += (_, _) => ClosePanel(WinoIntelligenceHeaderPanel.Summary);
        var summaryHeader = WinoLayout.HStack(4, summaryHeading, WinoLayout.Spacer(), _summaryCopyButton, _summaryRegenerateButton, summaryClose);

        _summarySpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        var summaryWaitingText = WinoStyle.Label(Translator.WinoIntelligence_Summarizing, WinoStyle.Description, WinoStyle.SecondaryText);
        var summaryCancel = PanelButton(Translator.Buttons_Cancel);
        summaryCancel.PrimaryActionTriggered += (_, _) => CancelSummary();
        _summaryWaitingRow = WinoLayout.HStack(8, _summarySpinner, summaryWaitingText, summaryCancel, WinoLayout.Spacer());

        _summaryText = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 0);
        _summaryPanel = WinoLayout.VStack(8, summaryHeader, _summaryWaitingRow, _summaryText);
        _summaryPanel.Alignment = UIStackViewAlignment.Fill;

        // ---- Translate panel ----
        var translateHeading = WinoStyle.Label(Translator.WinoIntelligence_TranslateMessage, WinoStyle.BodyStrong);
        var translateClose = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel, 13, 36, 36, WinoStyle.SecondaryText);
        translateClose.PrimaryActionTriggered += (_, _) => ClosePanel(WinoIntelligenceHeaderPanel.Translate);
        var translateHeader = WinoLayout.HStack(4, translateHeading, WinoLayout.Spacer(), translateClose);

        _sourcePicker = LanguagePicker(Translator.WinoIntelligence_SourceLanguage);
        _targetPicker = LanguagePicker(Translator.WinoIntelligence_TargetLanguage);
        var arrow = new WinoIconView(WinoIconGlyph.ArrowRight, 12, WinoStyle.SecondaryText);
        _translationRunButton = PanelButton(Translator.WinoIntelligence_Translate);
        _translationRunButton.PrimaryActionTriggered += (_, _) => { if (_view.CanRequestTranslation) TranslateRequested?.Invoke(this, EventArgs.Empty); };
        var pickers = WinoLayout.HStack(8,
            WinoLayout.VStack(2, WinoStyle.Label(Translator.WinoIntelligence_SourceLanguage, WinoStyle.Caption, WinoStyle.SecondaryText), _sourcePicker),
            arrow,
            WinoLayout.VStack(2, WinoStyle.Label(Translator.WinoIntelligence_TargetLanguage, WinoStyle.Caption, WinoStyle.SecondaryText), _targetPicker));
        pickers.Alignment = UIStackViewAlignment.Bottom;
        var runRow = WinoLayout.HStack(8, _translationRunButton, WinoLayout.Spacer());

        _translationSpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        _translationBusyText = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        var translationCancel = PanelButton(Translator.Buttons_Cancel);
        translationCancel.PrimaryActionTriggered += (_, _) => CancelTranslation();
        _translationWaitingRow = WinoLayout.HStack(8, _translationSpinner, _translationBusyText, translationCancel, WinoLayout.Spacer());

        var appliedIcon = new WinoIconView(WinoIconGlyph.CheckmarkCircle, 14, WinoStyle.Success);
        _translationStatusText = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _translationAppliedRow = WinoLayout.HStack(6, appliedIcon, _translationStatusText, WinoLayout.Spacer());

        _translatePanel = WinoLayout.VStack(8, translateHeader, pickers, runRow, _translationWaitingRow, _translationAppliedRow);
        _translatePanel.Alignment = UIStackViewAlignment.Fill;

        _body = WinoLayout.VStack(10, _deadline, _insightsLocked, _featureRow, _summaryPanel, _translatePanel);
        _body.Alignment = UIStackViewAlignment.Fill;

        var root = WinoLayout.Insets(WinoLayout.VStack(10, headerRow, _tiles, _body), 10, 14, 12, 8);
        root.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(root, this);

        Sync();
    }

    private WinoIntelligenceHeaderState CurrentState => _view.State;

    /// <summary>Whether the body is shown. Ignored while there is nothing to expand.</summary>
    public bool IsExpanded
    {
        get => _view.IsExpanded;
        set { _view.SetExpanded(value); Sync(); }
    }

    /// <summary>Applies the presenter state. A new content key collapses the header and closes any panel.</summary>
    public void Update(WinoIntelligenceHeaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (tilesChanged, languagesChanged) = _view.Update(state);
        if (tilesChanged) SyncTiles();
        if (languagesChanged) RebuildLanguagePickers();
        Sync();
    }

    /// <summary>Expands the body and opens the summary panel, requesting a summary when none exists yet.</summary>
    public void ShowSummary()
    {
        bool request = _view.ShowSummary();
        Sync();
        if (request) SummaryRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Expands the body and opens the translate panel.</summary>
    public void ShowTranslation()
    {
        _view.ShowTranslation();
        Sync();
    }

    /// <summary>A one-line description of the visible state, for the debug bridge.</summary>
    public string Dump()
        => $"expanded={IsExpanded} panel={_view.OpenPanel} state={CurrentState.ProcessingState} summary={CurrentState.SummaryState}" +
           $" tiles={CurrentState.Tiles.Count} translation=busy:{CurrentState.IsTranslationBusy},result:{CurrentState.HasTranslationResult},applied:{CurrentState.IsTranslationApplied}";

    private void ToggleExpanded()
    {
        if (_view.ToggleExpanded()) Sync();
    }

    private void SummaryChipClicked()
    {
        if (!CurrentState.IsSummaryAvailable) return;
        bool request = _view.ClickSummary();
        Sync();
        if (request) SummaryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenPanel(WinoIntelligenceHeaderPanel panel)
    {
        _view.Open(panel);
        Sync();
    }

    private void ClosePanel(WinoIntelligenceHeaderPanel panel)
    {
        _view.Close(panel);
        Sync();
    }

    private void CancelSummary()
    {
        if (!_view.CancelSummary()) return;
        SummaryCancelRequested?.Invoke(this, EventArgs.Empty);
        Sync();
    }

    private void CancelTranslation()
    {
        if (!_view.CancelTranslation()) return;
        TranslationCancelRequested?.Invoke(this, EventArgs.Empty);
        Sync();
    }

    private void CopySummary()
    {
        if (string.IsNullOrWhiteSpace(CurrentState.SummaryText)) return;
        UIPasteboard.General.String = CurrentState.SummaryText;
    }

    private void SyncTiles()
    {
        var previous = _tiles.Subviews;
        var chips = new List<UIView>();
        foreach (var tile in CurrentState.Tiles)
        {
            var chip = new WinoChipView(24) { CornerRadius = 4, Text = tile.IsAlwaysIconOnly ? string.Empty : tile.Text, MaxTextWidth = 220 };
            var tint = tile.IsWarning ? WinoStyle.Caution : WinoStyle.Accent;
            chip.Fill = tint.ColorWithAlpha(0.13f);
            chip.TextColor = tint;
            chip.SetGlyph(string.IsNullOrEmpty(tile.Glyph) ? WinoIcons.Glyph(WinoIconGlyph.Sparkle) : tile.Glyph, 12);
            chip.AccessibilityLabel = tile.AccessibleText;
            chips.Add(chip);
        }
        _tiles.SetItems(chips);
        foreach (var view in previous) view.Dispose();
    }

    /// <summary>Language menus: the source lists "Detect" and every language, the target omits "Detect".</summary>
    private void RebuildLanguagePickers()
    {
        _sourcePicker.Menu = LanguageMenu(_view.SourceOptions, CurrentState.SelectedSourceLanguage, code => SourceLanguageChanged?.Invoke(this, code));
        _targetPicker.Menu = LanguageMenu(_view.TargetOptions, CurrentState.SelectedTargetLanguage, code => TargetLanguageChanged?.Invoke(this, code));
    }

    private UIMenu LanguageMenu(WinoIntelligenceLanguageOption[] options, string selected, Action<string> changed)
    {
        int index = WinoIntelligenceHeaderViewState.LanguageIndex(options, selected);
        var actions = new UIMenuElement[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            var action = UIAction.Create(option.Label, null, null, _ => { if (!_synchronizing) changed(option.Code); });
            action.State = i == index ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[i] = action;
        }
        return UIMenu.Create(actions);
    }

    private static void SetPickerTitle(UIButton picker, WinoIntelligenceLanguageOption[] options, string code)
    {
        int index = WinoIntelligenceHeaderViewState.LanguageIndex(options, code);
        SetTitle(picker, index >= 0 ? options[index].Label : string.Empty);
    }

    private void Sync()
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            var state = CurrentState;
            var subtitle = WinoIntelligenceHeaderText.Subtitle(state.ProcessingState);
            _briefing.Text = state.BriefingFactText ?? string.Empty;
            _briefing.Hidden = !state.HasBriefingFact;
            _subtitle.Text = subtitle;
            _subtitle.Hidden = state.HasBriefingFact || state.IsProcessingRunning || string.IsNullOrEmpty(subtitle);
            _processingStatus.Text = subtitle;
            _processingRow.Hidden = !state.IsProcessingRunning;
            if (state.IsProcessingRunning) _processingSpinner.StartAnimating();
            else _processingSpinner.StopAnimating();

            SetTitle(_processButton, WinoIntelligenceHeaderText.ProcessTitle(state.ProcessingState));
            _processButton.Hidden = !state.CanRequestProcessing;
            _copyCodeButton.Hidden = string.IsNullOrWhiteSpace(state.VerificationCode);

            var expanded = _view.IsExpanded;
            _chevron.Hidden = !state.CanExpand;
            WinoToDoStyle.SetGlyph(_chevron, expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 13, WinoStyle.SecondaryText);
            WinoAccessibility.Label(_chevron, WinoIntelligenceHeaderText.HeaderAccessibility(state, expanded));

            _tiles.Hidden = state.Tiles.Count == 0;

            _body.Hidden = !expanded;
            _deadline.Text = WinoIntelligenceHeaderText.Deadline(state);
            _deadline.Hidden = !state.HasDeadline;
            _insightsLocked.Hidden = !state.CanRequestProcessing;

            _summaryChip.Hidden = !state.IsSummaryAvailable;
            _summaryChip.Checked = state.SummaryState == WinoIntelligenceFeatureState.Done;
            _summaryChip.AccessibilityLabel = WinoIntelligenceHeaderText.SummaryChipAccessibility(state.SummaryState);

            var target = _view.TargetLanguageLabel;
            _translateChip.Hidden = !state.IsTranslateAvailable;
            _translateChip.Checked = state.HasTranslationResult;
            _translateChip.Title = WinoIntelligenceHeaderText.TranslateChipTitle(target);
            _translateChip.AccessibilityLabel = WinoIntelligenceHeaderText.TranslateChipAccessibility(state, target);
            _featureRow.Hidden = _summaryChip.Hidden && _translateChip.Hidden;

            SyncPanels(state);
        }
        finally { _synchronizing = false; }
    }

    private void SyncPanels(WinoIntelligenceHeaderState state)
    {
        _summaryPanel.Hidden = !_view.IsSummaryPanelOpen;
        _summaryWaitingRow.Hidden = !_view.IsSummaryWaitingShown;
        if (_view.IsSummaryWaitingShown) _summarySpinner.StartAnimating();
        else _summarySpinner.StopAnimating();
        _summaryText.Text = state.SummaryText ?? string.Empty;
        _summaryText.Hidden = !_view.IsSummaryTextShown;
        _summaryCopyButton.Enabled = _view.CanCopySummary;
        _summaryRegenerateButton.Enabled = _view.CanRegenerateSummary;

        _translatePanel.Hidden = !_view.IsTranslatePanelOpen;
        _translationWaitingRow.Hidden = !_view.IsTranslationWaitingShown;
        if (_view.IsTranslationWaitingShown) _translationSpinner.StartAnimating();
        else _translationSpinner.StopAnimating();
        _translationBusyText.Text = state.TranslationStatusText ?? string.Empty;
        _translationAppliedRow.Hidden = !_view.IsTranslationAppliedShown;
        _translationStatusText.Text = state.TranslationStatusText ?? string.Empty;

        SetTitle(_translationRunButton, WinoIntelligenceHeaderText.RunTitle(_view.TranslateRunKind));
        _translationRunButton.Enabled = _view.IsTranslateRunEnabled;
        _sourcePicker.Enabled = !state.IsTranslationBusy;
        _targetPicker.Enabled = !state.IsTranslationBusy;
        RebuildLanguagePickers();
        SetPickerTitle(_sourcePicker, _view.SourceOptions, state.SelectedSourceLanguage);
        SetPickerTitle(_targetPicker, _view.TargetOptions, state.SelectedTargetLanguage);
    }

    private static void SetTitle(UIButton button, string title)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title;
        button.Configuration = configuration;
    }

    private static UIButton TextButton(string title, WinoIconGlyph glyph)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.Title = title;
        configuration.Image = WinoIcons.Image(glyph, 12);
        configuration.ImagePadding = 4;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 750, 760);
        return button;
    }

    private static UIButton PanelButton(string title)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.Title = title;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetContentCompressionResistancePriority(760, UILayoutConstraintAxis.Horizontal);
        return button;
    }

    private static UIButton LanguagePicker(string label)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        var picker = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(picker, label);
        picker.WidthAnchor.ConstraintGreaterThanOrEqualTo(120).Active = true;
        return picker;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ProcessRequested = null;
            CopyCodeRequested = null;
            SummaryRequested = null;
            SummaryCancelRequested = null;
            TranslateRequested = null;
            TranslationCancelRequested = null;
            SourceLanguageChanged = null;
            TargetLanguageChanged = null;
        }
        base.Dispose(disposing);
    }
}

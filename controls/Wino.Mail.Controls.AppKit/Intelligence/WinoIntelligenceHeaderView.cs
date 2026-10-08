using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Intelligence;

/// <summary>
/// Native Wino Intelligence header of the reading pane, laid out like the Windows
/// WinoIntelligenceHeader: a card with the sparkle, title and briefing headline (or the
/// processing status) beside the Copy code and Process actions and the disclosure chevron; the
/// intelligence tiles under it; and a collapsible body with the Summarize and Translate entry
/// points, each opening its detail panel (summary text, or the language pickers and status).
/// </summary>
/// <remarks>
/// The view is passive: <see cref="Update"/> applies a <see cref="WinoIntelligenceHeaderState"/>
/// and user actions are raised as events for the host to forward to the presenter. Only the
/// expansion and the open panel are view state; a new content key resets both.
/// </remarks>
public sealed partial class WinoIntelligenceHeaderView : WinoSurfaceView
{
    private enum Panel { None, Summary, Translate }

    private WinoIntelligenceHeaderState _state = WinoIntelligenceHeaderState.Empty;
    private bool _expanded;
    private Panel _openPanel;
    private bool _synchronizing;

    private readonly NSTextField _title;
    private readonly NSTextField _briefing;
    private readonly NSTextField _subtitle;
    private readonly NSStackView _titleBlock;
    private readonly NSProgressIndicator _processingSpinner;
    private readonly NSTextField _processingStatus;
    private readonly NSStackView _processingRow;
    private readonly NSButton _copyCodeButton;
    private readonly NSButton _processButton;
    private readonly NSButton _chevron;
    private readonly WinoFlowView _tiles;
    private readonly NSStackView _body;
    private readonly NSTextField _deadline;
    private readonly NSTextField _insightsLocked;
    private readonly NSStackView _featureRow;
    private readonly WinoChipButton _summaryChip;
    private readonly WinoChipButton _translateChip;

    /// <summary>Process (or Retry) was clicked.</summary>
    public event EventHandler? ProcessRequested;

    /// <summary>Copy code was clicked; the host copies the verification code.</summary>
    public event EventHandler? CopyCodeRequested;

    /// <summary>A summary was requested (first click or Regenerate).</summary>
    public event EventHandler? SummaryRequested;

    /// <summary>The running summary was canceled.</summary>
    public event EventHandler? SummaryCancelRequested;

    /// <summary>Translate / Show original / Translate again was clicked.</summary>
    public event EventHandler? TranslateRequested;

    /// <summary>The running translation was canceled.</summary>
    public event EventHandler? TranslationCancelRequested;

    /// <summary>The source language changed; the argument is its code (empty means detect).</summary>
    public event EventHandler<string>? SourceLanguageChanged;

    /// <summary>The target language changed; the argument is its code.</summary>
    public event EventHandler<string>? TargetLanguageChanged;

    public WinoIntelligenceHeaderView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        CornerRadius = WinoStyle.GroupRadius;
        Fill = WinoBriefingCardView.CardFill;
        Stroke = WinoBriefingCardView.CardStroke;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        AccessibilityLabel = Translator.WinoIntelligence_HeaderTitle;

        // ---- Header row ----
        var sparkle = new WinoIconView(WinoIconGlyph.Sparkle, 16, WinoStyle.Accent);
        _title = WinoStyle.Label(Translator.WinoIntelligence_HeaderTitle, WinoStyle.BodyStrong);
        _briefing = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 2);
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _titleBlock = WinoLayout.VStack(2, _title, _briefing, _subtitle);
        _titleBlock.Alignment = NSLayoutAttribute.Width;
        _titleBlock.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _titleBlock.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        // The title block toggles the body, like the whole Windows header row.
        _titleBlock.AddGestureRecognizer(new NSClickGestureRecognizer(() => ToggleExpanded()));

        _processingSpinner = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            ControlSize = NSControlSize.Small,
            Indeterminate = true,
            IsDisplayedWhenStopped = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _processingStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _processingRow = WinoLayout.HStack(6, _processingSpinner, _processingStatus);

        _copyCodeButton = TextButton(Translator.Intelligence_ActionCopyCode, WinoIconGlyph.Copy);
        _copyCodeButton.Activated += (_, _) => { if (!string.IsNullOrWhiteSpace(_state.VerificationCode)) CopyCodeRequested?.Invoke(this, EventArgs.Empty); };
        _processButton = TextButton(Translator.WinoIntelligence_Process, WinoIconGlyph.Sparkle);
        WinoAccessibility.Help(_processButton, Translator.WinoIntelligence_ProcessAutomation);
        _processButton.Activated += (_, _) => { if (_state.CanRequestProcessing) ProcessRequested?.Invoke(this, EventArgs.Empty); };
        _chevron = IconButton(WinoIconGlyph.ChevronDown, Translator.WinoIntelligence_HeaderTitle);
        _chevron.Activated += (_, _) => ToggleExpanded();

        var headerRow = WinoLayout.HStack(10, sparkle, _titleBlock, _processingRow, _copyCodeButton, _processButton, _chevron);
        headerRow.Alignment = NSLayoutAttribute.CenterY;

        // ---- Tiles (always visible while the header is) ----
        _tiles = new WinoFlowView { Spacing = 6, LineSpacing = 6 };

        // ---- Body ----
        _deadline = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _deadline.Selectable = true;
        _insightsLocked = WinoStyle.Label(Translator.WinoIntelligence_InsightsLocked, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        _summaryChip = new WinoChipButton(WinoIconGlyph.TextDescription, Translator.WinoIntelligence_Summarize);
        _summaryChip.Clicked += (_, _) => SummaryChipClicked();
        _translateChip = new WinoChipButton(WinoIconGlyph.Translate, Translator.WinoIntelligence_Translate);
        _translateChip.Clicked += (_, _) => OpenPanel(Panel.Translate);
        _featureRow = WinoLayout.HStack(8, _summaryChip, _translateChip, WinoLayout.Spacer());

        BuildPanels();

        // Width alignment instead of explicit width constraints: the stacks detach hidden rows, and
        // the rows must fill the card again when they come back.
        _body = WinoLayout.VStack(10, _deadline, _insightsLocked, _featureRow, _summaryPanel, _translatePanel);
        _body.Alignment = NSLayoutAttribute.Width;

        var root = WinoLayout.VStack(10, headerRow, _tiles, _body);
        root.Alignment = NSLayoutAttribute.Width;
        root.EdgeInsets = new NSEdgeInsets(10, 14, 12, 12);
        WinoLayout.Fill(root, this);

        Sync();
    }

    /// <summary>Whether the body is shown. Ignored while there is nothing to expand.</summary>
    public bool IsExpanded
    {
        get => _expanded && _state.CanExpand;
        set { _expanded = value; Sync(); }
    }

    /// <summary>Applies the presenter state. A new content key collapses the header and closes any panel.</summary>
    public void Update(WinoIntelligenceHeaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!string.Equals(state.ContentKey, _state.ContentKey, StringComparison.Ordinal))
        {
            _expanded = false;
            _openPanel = Panel.None;
        }
        var tilesChanged = !ReferenceEquals(state.Tiles, _state.Tiles);
        var languagesChanged = !ReferenceEquals(state.Languages, _state.Languages);
        _state = state;
        if (tilesChanged) SyncTiles();
        if (languagesChanged) RebuildLanguagePickers();
        Sync();
    }

    /// <summary>Expands the body and opens the summary panel, requesting a summary when none exists yet.</summary>
    public void ShowSummary()
    {
        _expanded = true;
        SummaryChipClicked();
    }

    /// <summary>Expands the body and opens the translate panel.</summary>
    public void ShowTranslation()
    {
        _expanded = true;
        OpenPanel(Panel.Translate);
    }

    /// <summary>A one-line description of the visible state, for the debug bridge.</summary>
    public string Dump()
        => $"expanded={IsExpanded} panel={_openPanel} state={_state.ProcessingState} summary={_state.SummaryState}" +
           $" summaryAvailable={_state.IsSummaryAvailable} translateAvailable={_state.IsTranslateAvailable}" +
           $" processAvailable={_state.IsProcessingAvailable} tiles={_state.Tiles.Count}" +
           $" briefing=\"{_state.BriefingFactText}\" code={(string.IsNullOrEmpty(_state.VerificationCode) ? "none" : "yes")}" +
           $" translation=busy:{_state.IsTranslationBusy},result:{_state.HasTranslationResult},applied:{_state.IsTranslationApplied},status:\"{_state.TranslationStatusText}\"" +
           $" source={(_state.SelectedSourceLanguage.Length == 0 ? "detect" : _state.SelectedSourceLanguage)} target={_state.SelectedTargetLanguage}" +
           $" summaryText={_state.SummaryText.Length}ch";

    private void ToggleExpanded()
    {
        if (!_state.CanExpand) return;
        _expanded = !_expanded;
        if (!_expanded) _openPanel = Panel.None;
        Sync();
    }

    private void SummaryChipClicked()
    {
        if (!_state.IsSummaryAvailable) return;
        OpenPanel(Panel.Summary);
        if (_state.SummaryState == WinoIntelligenceFeatureState.Idle) SummaryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenPanel(Panel panel)
    {
        _expanded = true;
        _openPanel = panel;
        Sync();
    }

    private void ClosePanel(Panel panel)
    {
        if (_openPanel == panel) _openPanel = Panel.None;
        Sync();
    }

    private void SyncTiles()
    {
        var previous = _tiles.Subviews;
        var chips = new List<NSView>();
        foreach (var tile in _state.Tiles)
        {
            var chip = new WinoChipView(22) { CornerRadius = 4, Text = tile.IsAlwaysIconOnly ? string.Empty : tile.Text, ToolTip = tile.AccessibleText, MaxTextWidth = 220 };
            var tint = tile.IsWarning ? WinoStyle.Caution : WinoStyle.Accent;
            chip.Fill = tint.ColorWithAlphaComponent((nfloat)0.13);
            chip.TextColor = tint;
            chip.SetGlyph(string.IsNullOrEmpty(tile.Glyph) ? WinoIcons.Glyph(WinoIconGlyph.Sparkle) : tile.Glyph, 12);
            chip.AccessibilityLabel = tile.AccessibleText;
            chips.Add(chip);
        }
        _tiles.SetItems(chips);
        foreach (var view in previous) view.Dispose();
    }

    private void Sync()
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            var state = _state;
            if (!state.CanExpand) { _expanded = false; _openPanel = Panel.None; }
            if (_openPanel == Panel.Summary && !state.IsSummaryAvailable) _openPanel = Panel.None;
            if (_openPanel == Panel.Translate && !state.IsTranslateAvailable) _openPanel = Panel.None;

            // Header row: the briefing replaces the status line; a processed message says nothing there.
            _briefing.StringValue = state.BriefingFactText ?? string.Empty;
            _briefing.Hidden = !state.HasBriefingFact;
            var subtitle = SubtitleText(state.ProcessingState);
            _subtitle.StringValue = subtitle;
            _subtitle.Hidden = state.HasBriefingFact || state.IsProcessingRunning || string.IsNullOrEmpty(subtitle);
            _processingStatus.StringValue = subtitle;
            _processingRow.Hidden = !state.IsProcessingRunning;
            if (state.IsProcessingRunning) _processingSpinner.StartAnimation(this);
            else _processingSpinner.StopAnimation(this);

            _processButton.Title = state.ProcessingState == WinoIntelligenceProcessingState.Failed
                ? Translator.WinoIntelligence_RetryProcess
                : Translator.WinoIntelligence_Process;
            _processButton.Hidden = !state.CanRequestProcessing;
            _copyCodeButton.Hidden = string.IsNullOrWhiteSpace(state.VerificationCode);

            var expanded = _expanded && state.CanExpand;
            _chevron.Hidden = !state.CanExpand;
            _chevron.Image = WinoIcons.Image(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12);
            var headerName = new List<string> { Translator.WinoIntelligence_HeaderTitle };
            if (state.HasBriefingFact) headerName.Add(state.BriefingFactText);
            else if (!string.IsNullOrEmpty(subtitle)) headerName.Add(subtitle);
            headerName.Add(expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed);
            WinoAccessibility.Label(_chevron, string.Join(", ", headerName));
            _chevron.ToolTip = expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;

            _tiles.Hidden = state.Tiles.Count == 0;

            // Body.
            _body.Hidden = !expanded;
            _deadline.StringValue = string.IsNullOrWhiteSpace(state.DeadlineDetailText) ? state.DeadlineText : state.DeadlineDetailText;
            _deadline.Hidden = !state.HasDeadline;
            _insightsLocked.Hidden = !state.CanRequestProcessing;

            _summaryChip.Hidden = !state.IsSummaryAvailable;
            _summaryChip.Checked = state.SummaryState == WinoIntelligenceFeatureState.Done;
            _summaryChip.AccessibilityLabel = state.SummaryState == WinoIntelligenceFeatureState.Busy
                ? Translator.WinoIntelligence_Summarizing
                : Translator.WinoIntelligence_Summarize;

            var target = state.Languages.FirstOrDefault(language => string.Equals(language.Code, state.SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase));
            var hint = target is null ? string.Empty : string.Format(Translator.WinoIntelligence_TranslateTargetHintFormat, target.Label);
            _translateChip.Hidden = !state.IsTranslateAvailable;
            _translateChip.Checked = state.HasTranslationResult;
            _translateChip.Title = string.IsNullOrEmpty(hint) ? Translator.WinoIntelligence_Translate : $"{Translator.WinoIntelligence_Translate} · {hint}";
            _translateChip.AccessibilityLabel = state.IsTranslationBusy
                ? state.TranslationStatusText
                : string.IsNullOrEmpty(hint) ? Translator.WinoIntelligence_Translate : $"{Translator.WinoIntelligence_Translate}, {hint}";
            _featureRow.Hidden = _summaryChip.Hidden && _translateChip.Hidden;

            SyncPanels();
        }
        finally { _synchronizing = false; }
    }

    private static string SubtitleText(WinoIntelligenceProcessingState state) => state switch
    {
        WinoIntelligenceProcessingState.Queued => Translator.WinoIntelligence_Queued,
        WinoIntelligenceProcessingState.Processing => Translator.WinoIntelligence_Processing,
        WinoIntelligenceProcessingState.Failed => Translator.WinoIntelligence_ProcessingFailed,
        WinoIntelligenceProcessingState.Unavailable => Translator.WinoIntelligence_Unavailable,
        WinoIntelligenceProcessingState.NotProcessed => Translator.WinoIntelligence_NotProcessed,
        _ => string.Empty,
    };

    private static NSButton TextButton(string title, WinoIconGlyph glyph)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Small,
            Image = WinoIcons.Image(glyph, 12),
            ImagePosition = NSCellImagePosition.ImageLeading,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        button.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        return button;
    }

    private static NSButton IconButton(WinoIconGlyph glyph, string label)
    {
        var button = new NSButton
        {
            Title = string.Empty,
            Bordered = false,
            Image = WinoIcons.Image(glyph, 13, null, label),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, 24, 24);
        return button;
    }
}

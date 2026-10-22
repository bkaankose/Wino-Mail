using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Presentation.AppKit;
using Wino.Presentation.Controls;

namespace Wino.Mail.Controls.AppKit.Intelligence;

/// <summary>
/// Native Wino Intelligence header of the reading pane, laid out like the Windows
/// WinoIntelligenceHeader: one header row (sparkle, title, the briefing fact, the status or
/// processing line and the disclosure chevron, then Process and Copy code at the trailing edge)
/// and, under a divider, the collapsible body: the deadline and locked-insights cards, the
/// Translate and Summarize chips, and the open feature's panel card. Classification tiles are not
/// shown here; as on Windows they appear only in the mail list.
/// </summary>
/// <remarks>
/// The view is passive: <see cref="Update"/> applies a <see cref="WinoIntelligenceHeaderState"/>
/// and user actions are raised as events for the host to forward to the presenter. Only the
/// expansion and the open panel are view state; a new content key resets both.
/// </remarks>
public sealed partial class WinoIntelligenceHeaderView : WinoSurfaceView
{
    /// <summary>Expansion, the open panel and the derived rules, shared with the UIKit header.</summary>
    private readonly WinoIntelligenceHeaderViewState _view = new();
    private bool _synchronizing;

    private WinoIntelligenceHeaderState CurrentState => _view.State;

    private readonly NSTextField _title;
    private readonly NSTextField _briefing;
    private readonly NSTextField _subtitle;
    private readonly NSProgressIndicator _processingSpinner;
    private readonly NSTextField _processingStatus;
    private readonly NSStackView _processingRow;
    private readonly NSButton _copyCodeButton;
    private readonly NSButton _processButton;
    private readonly NSButton _chevron;
    private readonly NSView _bodyHost;
    private readonly WinoSurfaceView _bodySeparator;
    private readonly NSTextField _deadline;
    private readonly NSView _deadlineCard;
    private readonly NSView _insightsLockedCard;
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

        // ---- Header row (Windows PART_HeaderRow: one line, MinHeight 40, padding 10,5) ----
        var sparkle = new WinoIconView(WinoIconGlyph.Sparkle, 16, WinoStyle.Accent);
        _title = WinoStyle.Label(Translator.WinoIntelligence_HeaderTitle, WinoStyle.BodyStrong);
        _title.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        _briefing = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _briefing.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _briefing.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _subtitle.SetContentCompressionResistancePriority(740, NSLayoutConstraintOrientation.Horizontal);
        foreach (var label in new[] { _title, _briefing, _subtitle }) label.Alignment = NSTextAlignment.Left;

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
        _processingRow.SetHuggingPriority(750, NSLayoutConstraintOrientation.Horizontal);

        _chevron = IconButton(WinoIconGlyph.ChevronDown, Translator.WinoIntelligence_HeaderTitle);
        _chevron.Activated += (_, _) => ToggleExpanded();

        // The toggle zone (Windows PART_HeaderToggleButton): the briefing takes the free width, so the
        // status and chevron sit just before the actions.
        var spacer = WinoLayout.Spacer();
        var toggleZone = WinoLayout.HStack(8, sparkle, _title, _briefing, spacer, _subtitle, _processingRow, _chevron);
        toggleZone.Distribution = NSStackViewDistribution.Fill;
        toggleZone.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        toggleZone.AddGestureRecognizer(new NSClickGestureRecognizer(() => ToggleExpanded()));

        _copyCodeButton = TextButton(Translator.Intelligence_ActionCopyCode, WinoIconGlyph.Copy);
        _copyCodeButton.Activated += (_, _) => { if (!string.IsNullOrWhiteSpace(CurrentState.VerificationCode)) CopyCodeRequested?.Invoke(this, EventArgs.Empty); };
        _processButton = TextButton(Translator.WinoIntelligence_Process, WinoIconGlyph.Sparkle);
        WinoAccessibility.Help(_processButton, Translator.WinoIntelligence_ProcessAutomation);
        _processButton.Activated += (_, _) => { if (CurrentState.CanRequestProcessing) ProcessRequested?.Invoke(this, EventArgs.Empty); };

        var headerRow = WinoLayout.HStack(10, toggleZone, _processButton, _copyCodeButton);
        headerRow.Distribution = NSStackViewDistribution.Fill;
        var headerHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(headerRow, headerHost, 5, 12, 5, 8);
        headerHost.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;

        // ---- Body (Windows PART_BodyRoot: a top divider, padding 12, spacing 10) ----
        _bodySeparator = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
        _bodySeparator.HeightAnchor.ConstraintEqualTo(1).Active = true;

        _deadline = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _deadline.Alignment = NSTextAlignment.Left;
        _deadline.Selectable = true;
        var deadlineIcon = new WinoIconView(WinoIconGlyph.Clock, 14, WinoStyle.Caution);
        var deadlineRow = WinoLayout.HStack(10, deadlineIcon, _deadline);
        _deadline.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _deadlineCard = InnerCard(deadlineRow, 8, 10, 4);

        var insightsLocked = WinoStyle.Label(Translator.WinoIntelligence_InsightsLocked, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        insightsLocked.Alignment = NSTextAlignment.Left;
        _insightsLockedCard = InnerCard(insightsLocked, 8, 10, 4);

        // Windows order: Translate, then Summarize.
        _translateChip = new WinoChipButton(WinoIconGlyph.Translate, Translator.WinoIntelligence_Translate);
        _translateChip.Clicked += (_, _) => OpenPanel(WinoIntelligenceHeaderPanel.Translate);
        _summaryChip = new WinoChipButton(WinoIconGlyph.TextDescription, Translator.WinoIntelligence_Summarize);
        _summaryChip.Clicked += (_, _) => SummaryChipClicked();
        _featureRow = WinoLayout.HStack(6, _translateChip, _summaryChip, WinoLayout.Spacer());

        BuildPanels();

        // The padding lives on the host, so the rows span exactly the padded width.
        var body = WinoLayout.VStack(10, _deadlineCard, _insightsLockedCard, _featureRow, _summaryCard, _translateCard);
        FillWidth(body);
        _bodyHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(body, _bodyHost, 12);

        var root = WinoLayout.VStack(0, headerHost, _bodySeparator, _bodyHost);
        FillWidth(root);
        WinoLayout.Fill(root, this);

        Sync();
    }

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
        var (_, languagesChanged) = _view.Update(state);
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
           $" summaryAvailable={CurrentState.IsSummaryAvailable} translateAvailable={CurrentState.IsTranslateAvailable}" +
           $" processAvailable={CurrentState.IsProcessingAvailable} tiles={CurrentState.Tiles.Count}" +
           $" briefing=\"{CurrentState.BriefingFactText}\" code={(string.IsNullOrEmpty(CurrentState.VerificationCode) ? "none" : "yes")}" +
           $" translation=busy:{CurrentState.IsTranslationBusy},result:{CurrentState.HasTranslationResult},applied:{CurrentState.IsTranslationApplied},status:\"{CurrentState.TranslationStatusText}\"" +
           $" source={(CurrentState.SelectedSourceLanguage.Length == 0 ? "detect" : CurrentState.SelectedSourceLanguage)} target={CurrentState.SelectedTargetLanguage}" +
           $" summaryText={CurrentState.SummaryText.Length}ch";

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

    private void Sync()
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            var state = CurrentState;

            // Header row: the briefing replaces the status line; a processed message says nothing there.
            _briefing.StringValue = state.BriefingFactText ?? string.Empty;
            _briefing.ToolTip = state.BriefingFactText;
            _briefing.Hidden = !state.HasBriefingFact;
            var subtitle = WinoIntelligenceHeaderText.Subtitle(state.ProcessingState);
            _subtitle.StringValue = subtitle;
            _subtitle.Hidden = state.HasBriefingFact || state.IsProcessingRunning || string.IsNullOrEmpty(subtitle);
            _processingStatus.StringValue = subtitle;
            _processingRow.Hidden = !state.IsProcessingRunning;
            if (state.IsProcessingRunning) _processingSpinner.StartAnimation(this);
            else _processingSpinner.StopAnimation(this);

            _processButton.Title = WinoIntelligenceHeaderText.ProcessTitle(state.ProcessingState);
            _processButton.Hidden = !state.CanRequestProcessing;
            _copyCodeButton.Hidden = string.IsNullOrWhiteSpace(state.VerificationCode);

            var expanded = _view.IsExpanded;
            _chevron.Hidden = !state.CanExpand;
            _chevron.Image = WinoIcons.Image(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12);
            WinoAccessibility.Label(_chevron, WinoIntelligenceHeaderText.HeaderAccessibility(state, expanded));
            _chevron.ToolTip = WinoIntelligenceHeaderText.ExpansionText(expanded);

            // Body.
            _bodyHost.Hidden = !expanded;
            _bodySeparator.Hidden = !expanded;
            _deadline.StringValue = WinoIntelligenceHeaderText.Deadline(state);
            _deadlineCard.Hidden = !state.HasDeadline;
            _insightsLockedCard.Hidden = !state.CanRequestProcessing;

            _summaryChip.Hidden = !state.IsSummaryAvailable;
            _summaryChip.Checked = state.SummaryState == WinoIntelligenceFeatureState.Done;
            _summaryChip.AccessibilityLabel = WinoIntelligenceHeaderText.SummaryChipAccessibility(state.SummaryState);

            var target = _view.TargetLanguageLabel;
            _translateChip.Hidden = !state.IsTranslateAvailable;
            _translateChip.Checked = state.HasTranslationResult;
            _translateChip.Title = WinoIntelligenceHeaderText.TranslateChipTitle(target);
            _translateChip.AccessibilityLabel = WinoIntelligenceHeaderText.TranslateChipAccessibility(state, target);
            _featureRow.Hidden = _summaryChip.Hidden && _translateChip.Hidden;

            SyncPanels();
        }
        finally { _synchronizing = false; }
    }

    private static NSButton TextButton(string title, WinoIconGlyph glyph)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Small,
            Font = WinoStyle.Caption,
            Image = WinoIcons.Image(glyph, 12),
            ImagePosition = NSCellImagePosition.ImageLeading,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        button.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        return button;
    }

    /// <summary>
    /// Stretches every row of a vertical stack to its width. Vertical stacks have no reliable
    /// width alignment (with it the rows drifted to the trailing edge); a leading stack with width
    /// constraints keeps them, and the constraints stay while a hidden row is detached.
    /// </summary>
    private static void FillWidth(NSStackView stack)
    {
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var row in stack.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }

    /// <summary>A Windows fact or panel card: subtle fill, card stroke and the given padding and corner radius.</summary>
    private static WinoSurfaceView InnerCard(NSView content, double vertical, double horizontal, double radius)
    {
        var card = new WinoSurfaceView { CornerRadius = radius, Fill = WinoStyle.SubtleFill, Stroke = WinoBriefingCardView.CardStroke };
        WinoLayout.Fill(content, card, vertical, horizontal, vertical, horizontal);
        return card;
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

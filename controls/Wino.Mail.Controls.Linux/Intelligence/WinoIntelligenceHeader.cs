using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The Wino Intelligence header of the reading pane (Windows WinoIntelligenceHeader, macOS WinoIntelligenceHeaderView):
/// one header row (sparkle, title, the briefing fact, the status or processing line, the disclosure chevron, then Process
/// and Copy code) and, under a divider, the collapsible body: the deadline and locked-insights cards, the Translate and
/// Summarize chips, and the open feature's panel. Classification tiles are not shown here; as on Windows and macOS they
/// appear only in the mail list.
/// </summary>
/// <remarks>
/// Passive: <see cref="Apply"/> takes a <see cref="WinoIntelligenceHeaderState"/>; user actions are raised as events for
/// the host to forward to the presenter. Expansion and the open panel live in the shared
/// <see cref="WinoIntelligenceHeaderViewState"/>, so a new content key resets both.
/// </remarks>
public sealed partial class WinoIntelligenceHeader : IDisposable
{
    private readonly WinoIntelligenceHeaderViewState _view = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Label _briefing;
    private readonly Gtk.Label _subtitle;
    private readonly WinoIntelligenceProgressRing _processingRing = new(16);
    private readonly Gtk.Label _processingStatus;
    private readonly Gtk.Box _processingRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.Button _copyCode;
    private readonly Gtk.Button _process;
    private readonly Gtk.Label _processLabel;
    private readonly Gtk.Button _chevron;
    private readonly Gtk.Separator _bodySeparator = Gtk.Separator.New(Gtk.Orientation.Horizontal);
    private readonly Gtk.Box _body = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
    private readonly Gtk.Label _deadline;
    private readonly Gtk.Widget _deadlineCard;
    private readonly Gtk.Widget _insightsLockedCard;
    private readonly Gtk.Box _featureRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly WinoChipButton _translateChip;
    private readonly WinoChipButton _summaryChip;
    private bool _synchronizing;
    private bool _disposed;

    public WinoIntelligenceHeader()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass("wino-intelligence-header");
        WinoA11y.Label(_root, Translator.WinoIntelligence_HeaderTitle);

        var sparkle = WinoIcons.Label(WinoIconGlyph.Sparkle, 16);
        sparkle.AddCssClass("accent");
        var title = Gtk.Label.New(Translator.WinoIntelligence_HeaderTitle);
        title.AddCssClass("heading");
        _briefing = Caption();
        _briefing.SetEllipsize(Pango.EllipsizeMode.End);
        _briefing.SetHexpand(true);
        _briefing.SetMaxWidthChars(48);
        _subtitle = Caption();
        _processingStatus = Caption();
        _processingRow.Append(_processingRing.Root);
        _processingRow.Append(_processingStatus);
        _chevron = Gtk.Button.New();
        _chevron.AddCssClass("flat");
        _chevron.AddCssClass("circular");
        _chevron.SetChild(WinoIcons.Label(WinoIconGlyph.ChevronDown, 12));
        _chevron.SetValign(Gtk.Align.Center);
        _subscriptions.Add(GtkSignals.OnClicked(_chevron, ToggleExpanded));

        var toggleZone = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        toggleZone.SetHexpand(true);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        foreach (var part in (Gtk.Widget[])[sparkle, title, _briefing, spacer, _subtitle, _processingRow, _chevron]) toggleZone.Append(part);
        var click = Gtk.GestureClick.New();
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.ReleasedSignalArgs>(
            h => click.OnReleased += h, h => click.OnReleased -= h, (_, args) =>
            {
                // The chevron handles its own click; the rest of the zone toggles too, as on Windows.
                var picked = toggleZone.Pick(args.X, args.Y, Gtk.PickFlags.Default);
                if (picked is not null && (ReferenceEquals(picked, _chevron) || picked.IsAncestor(_chevron))) return;
                ToggleExpanded();
            }));
        toggleZone.AddController(click);

        (_process, _processLabel) = TextButton(Translator.WinoIntelligence_Process, WinoIconGlyph.Sparkle);
        _process.SetTooltipText(Translator.WinoIntelligence_ProcessAutomation);
        _subscriptions.Add(GtkSignals.OnClicked(_process, () =>
        {
            if (_view.State.CanRequestProcessing) ProcessRequested?.Invoke();
        }));
        (_copyCode, _) = TextButton(Translator.Intelligence_ActionCopyCode, WinoIconGlyph.Copy);
        _subscriptions.Add(GtkSignals.OnClicked(_copyCode, () =>
        {
            if (!string.IsNullOrWhiteSpace(_view.State.VerificationCode)) CopyCodeRequested?.Invoke();
        }));

        var headerRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        headerRow.AddCssClass("wino-intelligence-header-row");
        headerRow.Append(toggleZone);
        headerRow.Append(_process);
        headerRow.Append(_copyCode);

        _deadline = Gtk.Label.New(string.Empty);
        _deadline.SetXalign(0);
        _deadline.SetWrap(true);
        _deadline.SetWrapMode(Pango.WrapMode.WordChar);
        _deadline.SetHexpand(true);
        var deadlineIcon = WinoIcons.Label(WinoIconGlyph.Clock, 14);
        deadlineIcon.AddCssClass("warning");
        var deadlineRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        deadlineRow.Append(deadlineIcon);
        deadlineRow.Append(_deadline);
        _deadlineCard = InnerCard(deadlineRow);
        var locked = Gtk.Label.New(Translator.WinoIntelligence_InsightsLocked);
        locked.SetXalign(0);
        locked.SetWrap(true);
        locked.AddCssClass("dim-label");
        _insightsLockedCard = InnerCard(locked);

        // Windows order: Translate, then Summarize.
        _translateChip = new WinoChipButton(WinoIconGlyph.Translate, Translator.WinoIntelligence_Translate, Translator.WinoIntelligence_Translate);
        _translateChip.Clicked += TranslateChipClicked;
        _summaryChip = new WinoChipButton(WinoIconGlyph.TextDescription, Translator.WinoIntelligence_Summarize, Translator.WinoIntelligence_Summarize);
        _summaryChip.Clicked += SummaryChipClicked;
        _featureRow.Append(_translateChip.Root);
        _featureRow.Append(_summaryChip.Root);

        BuildPanels();
        foreach (var part in (Gtk.Widget[])[_deadlineCard, _insightsLockedCard, _featureRow, _summaryCard, _translateCard]) _body.Append(part);
        _body.AddCssClass("wino-intelligence-header-body");

        _root.Append(headerRow);
        _root.Append(_bodySeparator);
        _root.Append(_body);
        Sync();
    }

    public Gtk.Widget Root => _root;

    /// <summary>Process (or Retry) was clicked.</summary>
    public event Action? ProcessRequested;

    /// <summary>Copy code was clicked; the host copies the verification code.</summary>
    public event Action? CopyCodeRequested;

    /// <summary>A summary was requested (first open or Regenerate).</summary>
    public event Action? SummaryRequested;

    public event Action? SummaryCancelRequested;

    /// <summary>The summary text was copied to the clipboard.</summary>
    public event Action? SummaryCopied;

    /// <summary>Translate, Translate again or Show original was clicked.</summary>
    public event Action? TranslateRequested;

    public event Action? TranslationCancelRequested;

    /// <summary>The source language changed; the argument is its code (empty means detect).</summary>
    public event Action<string>? SourceLanguageChanged;

    public event Action<string>? TargetLanguageChanged;

    public WinoIntelligenceHeaderState State => _view.State;

    public bool IsExpanded
    {
        get => _view.IsExpanded;
        set
        {
            _view.SetExpanded(value);
            Sync();
        }
    }

    public WinoIntelligenceHeaderPanel OpenPanel => _view.OpenPanel;

    /// <summary>Applies the presenter state. A new content key collapses the header and closes any panel.</summary>
    public void Apply(WinoIntelligenceHeaderState state)
    {
        if (_disposed) return;
        var (_, languagesChanged) = _view.Update(state);
        if (languagesChanged) RebuildLanguagePickers();
        Sync();
    }

    /// <summary>Expands the body and opens the summary panel, requesting a summary when none exists yet.</summary>
    public void ShowSummary()
    {
        var request = _view.ShowSummary();
        Sync();
        if (request) SummaryRequested?.Invoke();
    }

    public void ShowTranslation()
    {
        _view.ShowTranslation();
        Sync();
    }

    /// <summary>One line describing the visible state, for Debug actions.</summary>
    public string Describe()
    {
        var state = _view.State;
        return $"expanded={IsExpanded} panel={_view.OpenPanel} state={state.ProcessingState} summary={state.SummaryState} summaryAvailable={state.IsSummaryAvailable} "
            + $"translateAvailable={state.IsTranslateAvailable} processAvailable={state.IsProcessingAvailable} process={_process.GetVisible()} copyCode={_copyCode.GetVisible()} "
            + $"ring={_processingRing.IsTicking} briefing=\"{state.BriefingFactText}\" deadline={_deadlineCard.GetVisible()} locked={_insightsLockedCard.GetVisible()} "
            + $"translation=busy:{state.IsTranslationBusy},result:{state.HasTranslationResult},applied:{state.IsTranslationApplied} run=\"{_translateRun.GetLabel()}\" runEnabled={_translateRun.GetSensitive()} "
            + $"source={(state.SelectedSourceLanguage.Length == 0 ? "detect" : state.SelectedSourceLanguage)} target={state.SelectedTargetLanguage} summaryText={state.SummaryText.Length}ch";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ProcessRequested = null;
        CopyCodeRequested = null;
        SummaryRequested = null;
        SummaryCancelRequested = null;
        SummaryCopied = null;
        TranslateRequested = null;
        TranslationCancelRequested = null;
        SourceLanguageChanged = null;
        TargetLanguageChanged = null;
        _translateChip.Clicked -= TranslateChipClicked;
        _summaryChip.Clicked -= SummaryChipClicked;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _processingRing.Dispose();
        _summaryRing.Dispose();
        _translationRing.Dispose();
        _translateChip.Dispose();
        _summaryChip.Dispose();
    }

    private void ToggleExpanded()
    {
        if (_view.ToggleExpanded()) Sync();
    }

    private void TranslateChipClicked()
    {
        _view.Open(WinoIntelligenceHeaderPanel.Translate);
        Sync();
    }

    private void SummaryChipClicked()
    {
        if (!_view.State.IsSummaryAvailable) return;
        var request = _view.ClickSummary();
        Sync();
        if (request) SummaryRequested?.Invoke();
    }

    private void ClosePanel(WinoIntelligenceHeaderPanel panel)
    {
        _view.Close(panel);
        Sync();
    }

    private void Sync()
    {
        if (_synchronizing || _disposed) return;
        _synchronizing = true;
        try
        {
            var state = _view.State;
            // The briefing replaces the status line; a processed message says nothing there.
            _briefing.SetText(state.BriefingFactText ?? string.Empty);
            _briefing.SetTooltipText(string.IsNullOrWhiteSpace(state.BriefingFactText) ? null : state.BriefingFactText);
            _briefing.SetVisible(state.HasBriefingFact);
            var subtitle = WinoIntelligenceHeaderText.Subtitle(state.ProcessingState);
            _subtitle.SetText(subtitle);
            _subtitle.SetVisible(!state.HasBriefingFact && !state.IsProcessingRunning && !string.IsNullOrEmpty(subtitle));
            _processingStatus.SetText(subtitle);
            _processingRow.SetVisible(state.IsProcessingRunning);
            _processingRing.Active = state.IsProcessingRunning;

            var processTitle = WinoIntelligenceHeaderText.ProcessTitle(state.ProcessingState);
            _processLabel.SetText(processTitle);
            WinoA11y.Rename(_process, processTitle);
            _process.SetVisible(state.CanRequestProcessing);
            _copyCode.SetVisible(!string.IsNullOrWhiteSpace(state.VerificationCode));

            var expanded = _view.IsExpanded;
            _chevron.SetVisible(state.CanExpand);
            if (_chevron.GetChild() is Gtk.Label chevronGlyph)
                WinoIcons.SetGlyph(chevronGlyph, WinoIcons.Glyph(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown));
            WinoA11y.Rename(_chevron, WinoIntelligenceHeaderText.HeaderAccessibility(state, expanded));
            _chevron.SetTooltipText(WinoIntelligenceHeaderText.ExpansionText(expanded));

            _body.SetVisible(expanded);
            _bodySeparator.SetVisible(expanded);
            _deadline.SetText(WinoIntelligenceHeaderText.Deadline(state));
            _deadlineCard.SetVisible(state.HasDeadline);
            _insightsLockedCard.SetVisible(state.CanRequestProcessing);

            _summaryChip.Visible = state.IsSummaryAvailable;
            _summaryChip.Checked = state.SummaryState == WinoIntelligenceFeatureState.Done;
            WinoA11y.Rename(_summaryChip.Button, WinoIntelligenceHeaderText.SummaryChipAccessibility(state.SummaryState));
            var target = _view.TargetLanguageLabel;
            _translateChip.Visible = state.IsTranslateAvailable;
            _translateChip.Checked = state.HasTranslationResult;
            _translateChip.Title = WinoIntelligenceHeaderText.TranslateChipTitle(target);
            WinoA11y.Rename(_translateChip.Button, WinoIntelligenceHeaderText.TranslateChipAccessibility(state, target));
            _featureRow.SetVisible(state.IsSummaryAvailable || state.IsTranslateAvailable);

            SyncPanels();
        }
        finally { _synchronizing = false; }
    }

    private static Gtk.Label Caption()
    {
        var label = Gtk.Label.New(string.Empty);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        label.SetXalign(0);
        return label;
    }

    private static (Gtk.Button Button, Gtk.Label Label) TextButton(string title, WinoIconGlyph glyph)
    {
        var content = WinoIcons.ButtonContent(glyph, title, 12);
        var label = content.GetLastChild() as Gtk.Label ?? Gtk.Label.New(title);
        var button = Gtk.Button.New();
        button.SetChild(content);
        button.AddCssClass("wino-intelligence-header-button");
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Label(button, title);
        return (button, label);
    }

    /// <summary>A fact or panel card: subtle fill and a hairline border.</summary>
    private static Gtk.Widget InnerCard(Gtk.Widget content)
    {
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("wino-intelligence-inner-card");
        card.Append(content);
        return card;
    }

    private static Gtk.Button IconButton(WinoIconGlyph glyph, string label)
    {
        var button = WinoIcons.Button(glyph, label, 13);
        button.SetTooltipText(label);
        button.SetValign(Gtk.Align.Center);
        return button;
    }
}

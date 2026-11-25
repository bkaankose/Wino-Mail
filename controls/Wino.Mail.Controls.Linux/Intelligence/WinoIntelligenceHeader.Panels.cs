using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The detail panels: the summary (waiting row with Cancel, the selectable text, Copy / Regenerate / Close) and the
/// translation (source and target drop-downs, the run button that also returns to the original, the waiting row and the
/// applied status). At most one is open, as on Windows.
/// </summary>
public sealed partial class WinoIntelligenceHeader
{
    private readonly WinoIntelligenceProgressRing _summaryRing = new(16);
    private readonly WinoIntelligenceProgressRing _translationRing = new(16);
    private readonly Gtk.Box _summaryCard = CardBox();
    private readonly Gtk.Box _summaryWaitingRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _summaryText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _summaryCopy = IconButton(WinoIconGlyph.Copy, Translator.WinoIntelligence_Copy);
    private readonly Gtk.Button _summaryRegenerate = IconButton(WinoIconGlyph.ArrowClockwise, Translator.WinoIntelligence_Regenerate);
    private readonly Gtk.Box _translateCard = CardBox();
    private readonly Gtk.DropDown _sourcePicker = LanguagePicker(Translator.WinoIntelligence_SourceLanguage);
    private readonly Gtk.DropDown _targetPicker = LanguagePicker(Translator.WinoIntelligence_TargetLanguage);
    private readonly Gtk.Button _translateRun = PanelButton(Translator.WinoIntelligence_Translate);
    private readonly Gtk.Box _translationWaitingRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _translationBusyText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _translationAppliedRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.Label _translationStatusText = Gtk.Label.New(string.Empty);

    private void BuildPanels()
    {
        // Summary.
        var summaryHeading = Gtk.Label.New(Translator.WinoIntelligence_Summary);
        summaryHeading.AddCssClass("heading");
        summaryHeading.SetXalign(0);
        summaryHeading.SetHexpand(true);
        _subscriptions.Add(GtkSignals.OnClicked(_summaryCopy, CopySummary));
        _subscriptions.Add(GtkSignals.OnClicked(_summaryRegenerate, () =>
        {
            if (_view.CanRequestSummary) SummaryRequested?.Invoke();
        }));
        var summaryClose = IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel);
        _subscriptions.Add(GtkSignals.OnClicked(summaryClose, () => ClosePanel(WinoIntelligenceHeaderPanel.Summary)));
        var summaryHeader = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        foreach (var part in (Gtk.Widget[])[summaryHeading, _summaryCopy, _summaryRegenerate, summaryClose]) summaryHeader.Append(part);

        var summaryWaiting = Gtk.Label.New(Translator.WinoIntelligence_Summarizing);
        summaryWaiting.AddCssClass("dim-label");
        var summaryCancel = PanelButton(Translator.Buttons_Cancel);
        _subscriptions.Add(GtkSignals.OnClicked(summaryCancel, CancelSummary));
        foreach (var part in (Gtk.Widget[])[_summaryRing.Root, summaryWaiting, summaryCancel]) _summaryWaitingRow.Append(part);

        // Model output: plain text, selectable, never markup.
        _summaryText.SetUseMarkup(false);
        _summaryText.SetXalign(0);
        _summaryText.SetWrap(true);
        _summaryText.SetWrapMode(Pango.WrapMode.WordChar);
        _summaryText.SetSelectable(true);

        var summaryPanel = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        foreach (var part in (Gtk.Widget[])[summaryHeader, _summaryWaitingRow, _summaryText]) summaryPanel.Append(part);
        _summaryCard.Append(summaryPanel);
        WinoA11y.Label(_summaryCard, Translator.WinoIntelligence_Summary);

        // Translation.
        var translateHeading = Gtk.Label.New(Translator.WinoIntelligence_TranslateMessage);
        translateHeading.AddCssClass("heading");
        translateHeading.SetXalign(0);
        translateHeading.SetHexpand(true);
        var translateClose = IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel);
        _subscriptions.Add(GtkSignals.OnClicked(translateClose, () => ClosePanel(WinoIntelligenceHeaderPanel.Translate)));
        var translateHeader = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        translateHeader.Append(translateHeading);
        translateHeader.Append(translateClose);

        _subscriptions.Add(GtkSignals.OnNotify(_sourcePicker, "selected", () => PickerChanged(_sourcePicker, _view.SourceOptions, SourceLanguageChanged)));
        _subscriptions.Add(GtkSignals.OnNotify(_targetPicker, "selected", () => PickerChanged(_targetPicker, _view.TargetOptions, TargetLanguageChanged)));
        var arrow = WinoIcons.Label(WinoIconGlyph.ArrowRight, 12);
        arrow.AddCssClass("dim-label");
        arrow.SetValign(Gtk.Align.End);
        arrow.SetMarginBottom(8);
        _translateRun.SetValign(Gtk.Align.End);
        _subscriptions.Add(GtkSignals.OnClicked(_translateRun, () =>
        {
            if (_view.CanRequestTranslation) TranslateRequested?.Invoke();
        }));
        var pickers = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foreach (var part in (Gtk.Widget[])[Labelled(Translator.WinoIntelligence_SourceLanguage, _sourcePicker), arrow,
                     Labelled(Translator.WinoIntelligence_TargetLanguage, _targetPicker), _translateRun]) pickers.Append(part);

        _translationBusyText.AddCssClass("dim-label");
        var translationCancel = PanelButton(Translator.Buttons_Cancel);
        _subscriptions.Add(GtkSignals.OnClicked(translationCancel, CancelTranslation));
        foreach (var part in (Gtk.Widget[])[_translationRing.Root, _translationBusyText, translationCancel]) _translationWaitingRow.Append(part);

        var appliedIcon = WinoIcons.Label(WinoIconGlyph.CheckmarkCircle, 13);
        appliedIcon.AddCssClass("success");
        _translationStatusText.AddCssClass("dim-label");
        _translationStatusText.SetSelectable(true);
        _translationStatusText.SetXalign(0);
        _translationStatusText.SetWrap(true);
        _translationAppliedRow.Append(appliedIcon);
        _translationAppliedRow.Append(_translationStatusText);

        var translatePanel = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        foreach (var part in (Gtk.Widget[])[translateHeader, pickers, _translationWaitingRow, _translationAppliedRow]) translatePanel.Append(part);
        _translateCard.Append(translatePanel);
        WinoA11y.Label(_translateCard, Translator.WinoIntelligence_TranslateMessage);
    }

    private void SyncPanels()
    {
        var state = _view.State;
        _summaryCard.SetVisible(_view.IsSummaryPanelOpen);
        var summaryBusy = _view.IsSummaryWaitingShown;
        _summaryWaitingRow.SetVisible(summaryBusy);
        _summaryRing.Active = summaryBusy;
        _summaryText.SetText(state.SummaryText ?? string.Empty);
        _summaryText.SetVisible(_view.IsSummaryTextShown);
        _summaryCopy.SetSensitive(_view.CanCopySummary);
        _summaryRegenerate.SetSensitive(_view.CanRegenerateSummary);

        _translateCard.SetVisible(_view.IsTranslatePanelOpen);
        var translationBusy = _view.IsTranslationWaitingShown;
        _translationWaitingRow.SetVisible(translationBusy);
        _translationRing.Active = translationBusy;
        _translationBusyText.SetText(state.TranslationStatusText ?? string.Empty);
        _translationAppliedRow.SetVisible(_view.IsTranslationAppliedShown);
        _translationStatusText.SetText(state.TranslationStatusText ?? string.Empty);

        var run = WinoIntelligenceHeaderText.RunTitle(_view.TranslateRunKind);
        _translateRun.SetLabel(run);
        WinoA11y.Rename(_translateRun, run);
        _translateRun.SetSensitive(_view.IsTranslateRunEnabled);
        _sourcePicker.SetSensitive(!state.IsTranslationBusy);
        _targetPicker.SetSensitive(!state.IsTranslationBusy);
        Select(_sourcePicker, _view.SourceOptions, state.SelectedSourceLanguage);
        Select(_targetPicker, _view.TargetOptions, state.SelectedTargetLanguage);
    }

    /// <summary>The source picker lists "Detect" and every language; the target picker omits "Detect".</summary>
    private void RebuildLanguagePickers()
    {
        var synchronizing = _synchronizing;
        _synchronizing = true;
        try
        {
            _sourcePicker.SetModel(Gtk.StringList.New([.. _view.SourceOptions.Select(static language => language.Label)]));
            _targetPicker.SetModel(Gtk.StringList.New([.. _view.TargetOptions.Select(static language => language.Label)]));
            WinoA11y.Label(_sourcePicker, Translator.WinoIntelligence_SourceLanguage);
            WinoA11y.Label(_targetPicker, Translator.WinoIntelligence_TargetLanguage);
        }
        finally { _synchronizing = synchronizing; }
    }

    private static void Select(Gtk.DropDown picker, WinoIntelligenceLanguageOption[] options, string code)
    {
        var index = WinoIntelligenceHeaderViewState.LanguageIndex(options, code);
        if (index >= 0 && picker.GetSelected() != (uint)index) picker.SetSelected((uint)index);
    }

    private void PickerChanged(Gtk.DropDown picker, WinoIntelligenceLanguageOption[] options, Action<string>? changed)
    {
        if (_synchronizing) return;
        var index = (int)picker.GetSelected();
        if (index < 0 || index >= options.Length) return;
        changed?.Invoke(options[index].Code);
    }

    private void CancelSummary()
    {
        if (!_view.CancelSummary()) return;
        SummaryCancelRequested?.Invoke();
        Sync();
    }

    private void CancelTranslation()
    {
        if (!_view.CancelTranslation()) return;
        TranslationCancelRequested?.Invoke();
        Sync();
    }

    private void CopySummary()
    {
        var text = _view.State.SummaryText;
        if (string.IsNullOrWhiteSpace(text)) return;
        _root.GetClipboard().SetText(text);
        SummaryCopied?.Invoke();
    }

    private static Gtk.Box CardBox()
    {
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("wino-intelligence-inner-card");
        return card;
    }

    private static Gtk.Button PanelButton(string title)
    {
        var button = Gtk.Button.NewWithLabel(title);
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Label(button, title);
        return button;
    }

    private static Gtk.DropDown LanguagePicker(string label)
    {
        var picker = Gtk.DropDown.New(Gtk.StringList.New([]), null);
        picker.SetTooltipText(label);
        // The longest language name must not set the reader's minimum width; a narrow reader ellipsizes it.
        picker.SetSizeRequest(96, -1);
        picker.SetHexpand(false);
        WinoA11y.Label(picker, label);
        return picker;
    }

    private static Gtk.Widget Labelled(string caption, Gtk.Widget content)
    {
        var label = Gtk.Label.New(caption);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        label.SetXalign(0);
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        box.Append(label);
        box.Append(content);
        return box;
    }
}

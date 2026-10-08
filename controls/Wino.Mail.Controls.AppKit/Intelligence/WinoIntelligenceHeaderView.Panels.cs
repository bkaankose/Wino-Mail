using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Intelligence;

/// <summary>
/// The detail panels of the header: the summary (waiting row with Cancel, the summary text,
/// Copy / Regenerate / Close) and the translation (source and target pickers, the run button
/// that also toggles back to the original, the waiting row and the applied status).
/// Exactly one panel is open at a time, as on Windows.
/// </summary>
public sealed partial class WinoIntelligenceHeaderView
{
    private NSStackView _summaryPanel = null!;
    private NSStackView _summaryWaitingRow = null!;
    private NSProgressIndicator _summarySpinner = null!;
    private NSTextField _summaryText = null!;
    private NSButton _summaryCopyButton = null!;
    private NSButton _summaryRegenerateButton = null!;

    private NSStackView _translatePanel = null!;
    private NSPopUpButton _sourcePicker = null!;
    private NSPopUpButton _targetPicker = null!;
    private NSButton _translationRunButton = null!;
    private NSStackView _translationWaitingRow = null!;
    private NSProgressIndicator _translationSpinner = null!;
    private NSTextField _translationBusyText = null!;
    private NSStackView _translationAppliedRow = null!;
    private NSTextField _translationStatusText = null!;
    private WinoIntelligenceLanguageOption[] _sourceOptions = [];
    private WinoIntelligenceLanguageOption[] _targetOptions = [];

    private void BuildPanels()
    {
        // ---- Summary ----
        var summaryHeading = WinoStyle.Label(Translator.WinoIntelligence_Summary, WinoStyle.BodyStrong);
        _summaryCopyButton = IconButton(WinoIconGlyph.Copy, Translator.WinoIntelligence_Copy);
        _summaryCopyButton.Activated += (_, _) => CopySummary();
        _summaryRegenerateButton = IconButton(WinoIconGlyph.ArrowClockwise, Translator.WinoIntelligence_Regenerate);
        _summaryRegenerateButton.Activated += (_, _) => { if (_state.IsSummaryAvailable) SummaryRequested?.Invoke(this, EventArgs.Empty); };
        var summaryClose = IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel);
        summaryClose.Activated += (_, _) => ClosePanel(Panel.Summary);
        var summaryHeader = WinoLayout.HStack(4, summaryHeading, WinoLayout.Spacer(), _summaryCopyButton, _summaryRegenerateButton, summaryClose);

        _summarySpinner = Spinner();
        var summaryWaitingText = WinoStyle.Label(Translator.WinoIntelligence_Summarizing, WinoStyle.Body, WinoStyle.SecondaryText);
        var summaryCancel = PanelButton(Translator.Buttons_Cancel);
        summaryCancel.Activated += (_, _) => CancelSummary();
        _summaryWaitingRow = WinoLayout.HStack(8, _summarySpinner, summaryWaitingText, summaryCancel, WinoLayout.Spacer());

        _summaryText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _summaryText.Selectable = true;

        _summaryPanel = WinoLayout.VStack(8, summaryHeader, _summaryWaitingRow, _summaryText);
        _summaryPanel.Alignment = NSLayoutAttribute.Width;
        WinoAccessibility.Label(_summaryPanel, Translator.WinoIntelligence_Summary);

        // ---- Translation ----
        var translateHeading = WinoStyle.Label(Translator.WinoIntelligence_TranslateMessage, WinoStyle.BodyStrong);
        var translateClose = IconButton(WinoIconGlyph.Dismiss, Translator.WinoIntelligence_ClosePanel);
        translateClose.Activated += (_, _) => ClosePanel(Panel.Translate);
        var translateHeader = WinoLayout.HStack(4, translateHeading, WinoLayout.Spacer(), translateClose);

        _sourcePicker = LanguagePicker(Translator.WinoIntelligence_SourceLanguage);
        _sourcePicker.Activated += (_, _) => PickerChanged(_sourcePicker, _sourceOptions, SourceLanguageChanged);
        _targetPicker = LanguagePicker(Translator.WinoIntelligence_TargetLanguage);
        _targetPicker.Activated += (_, _) => PickerChanged(_targetPicker, _targetOptions, TargetLanguageChanged);
        var sourceLabel = WinoStyle.Label(Translator.WinoIntelligence_SourceLanguage, WinoStyle.Caption, WinoStyle.SecondaryText);
        var targetLabel = WinoStyle.Label(Translator.WinoIntelligence_TargetLanguage, WinoStyle.Caption, WinoStyle.SecondaryText);
        var arrow = new WinoIconView(WinoIconGlyph.ArrowRight, 12, WinoStyle.SecondaryText);
        _translationRunButton = PanelButton(Translator.WinoIntelligence_Translate);
        _translationRunButton.Activated += (_, _) => { if (_state.IsTranslateAvailable && !_state.IsTranslationBusy) TranslateRequested?.Invoke(this, EventArgs.Empty); };
        var pickers = WinoLayout.HStack(8,
            WinoLayout.VStack(2, sourceLabel, _sourcePicker),
            arrow,
            WinoLayout.VStack(2, targetLabel, _targetPicker),
            _translationRunButton,
            WinoLayout.Spacer());
        pickers.Alignment = NSLayoutAttribute.Bottom;

        _translationSpinner = Spinner();
        _translationBusyText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        var translationCancel = PanelButton(Translator.Buttons_Cancel);
        translationCancel.Activated += (_, _) => CancelTranslation();
        _translationWaitingRow = WinoLayout.HStack(8, _translationSpinner, _translationBusyText, translationCancel, WinoLayout.Spacer());

        var appliedIcon = new WinoIconView(WinoIconGlyph.CheckmarkCircle, 13, WinoStyle.Success);
        _translationStatusText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        _translationStatusText.Selectable = true;
        _translationAppliedRow = WinoLayout.HStack(6, appliedIcon, _translationStatusText, WinoLayout.Spacer());

        _translatePanel = WinoLayout.VStack(8, translateHeader, pickers, _translationWaitingRow, _translationAppliedRow);
        _translatePanel.Alignment = NSLayoutAttribute.Width;
        WinoAccessibility.Label(_translatePanel, Translator.WinoIntelligence_TranslateMessage);
    }

    private void SyncPanels()
    {
        var state = _state;
        var expanded = _expanded && state.CanExpand;
        var summaryOpen = expanded && _openPanel == Panel.Summary;
        var translateOpen = expanded && _openPanel == Panel.Translate;

        _summaryPanel.Hidden = !summaryOpen;
        var summaryBusy = summaryOpen && state.SummaryState == WinoIntelligenceFeatureState.Busy;
        var summaryDone = summaryOpen && state.SummaryState == WinoIntelligenceFeatureState.Done;
        _summaryWaitingRow.Hidden = !summaryBusy;
        if (summaryBusy) _summarySpinner.StartAnimation(this);
        else _summarySpinner.StopAnimation(this);
        _summaryText.StringValue = state.SummaryText ?? string.Empty;
        _summaryText.Hidden = !summaryDone;
        _summaryCopyButton.Enabled = summaryDone && !string.IsNullOrWhiteSpace(state.SummaryText);
        _summaryRegenerateButton.Enabled = state.IsSummaryAvailable && !summaryBusy;

        _translatePanel.Hidden = !translateOpen;
        var translationBusy = translateOpen && state.IsTranslationBusy;
        _translationWaitingRow.Hidden = !translationBusy;
        if (translationBusy) _translationSpinner.StartAnimation(this);
        else _translationSpinner.StopAnimation(this);
        _translationBusyText.StringValue = state.TranslationStatusText ?? string.Empty;
        _translationAppliedRow.Hidden = !(translateOpen && state.HasTranslationResult && !state.IsTranslationBusy);
        _translationStatusText.StringValue = state.TranslationStatusText ?? string.Empty;

        _translationRunButton.Title = state.IsTranslationApplied
            ? Translator.WinoIntelligence_ShowOriginal
            : state.HasTranslationResult ? Translator.WinoIntelligence_TranslateAgain : Translator.WinoIntelligence_Translate;
        _translationRunButton.Enabled = !state.IsTranslationBusy
            && (string.IsNullOrWhiteSpace(state.SelectedSourceLanguage)
                || !string.Equals(state.SelectedSourceLanguage, state.SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase));
        _sourcePicker.Enabled = !state.IsTranslationBusy;
        _targetPicker.Enabled = !state.IsTranslationBusy;
        Select(_sourcePicker, _sourceOptions, state.SelectedSourceLanguage);
        Select(_targetPicker, _targetOptions, state.SelectedTargetLanguage);
    }

    /// <summary>The source picker lists "Detect" and every language; the target picker omits "Detect".</summary>
    private void RebuildLanguagePickers()
    {
        _sourceOptions = _state.Languages.ToArray();
        _targetOptions = _state.Languages.Where(language => !string.IsNullOrWhiteSpace(language.Code)).ToArray();
        _sourcePicker.RemoveAllItems();
        _sourcePicker.AddItems(_sourceOptions.Select(language => language.Label).ToArray());
        _targetPicker.RemoveAllItems();
        _targetPicker.AddItems(_targetOptions.Select(language => language.Label).ToArray());
    }

    private static void Select(NSPopUpButton picker, WinoIntelligenceLanguageOption[] options, string code)
    {
        var index = Array.FindIndex(options, language => string.Equals(language.Code, code ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && picker.IndexOfSelectedItem != index) picker.SelectItem(index);
    }

    private void PickerChanged(NSPopUpButton picker, WinoIntelligenceLanguageOption[] options, EventHandler<string>? changed)
    {
        if (_synchronizing) return;
        var index = (int)picker.IndexOfSelectedItem;
        if (index < 0 || index >= options.Length) return;
        changed?.Invoke(this, options[index].Code);
    }

    private void CancelSummary()
    {
        if (_state.SummaryState != WinoIntelligenceFeatureState.Busy) return;
        _openPanel = Panel.None;
        SummaryCancelRequested?.Invoke(this, EventArgs.Empty);
        Sync();
    }

    private void CancelTranslation()
    {
        if (!_state.IsTranslationBusy) return;
        TranslationCancelRequested?.Invoke(this, EventArgs.Empty);
        ClosePanel(Panel.Translate);
    }

    private void CopySummary()
    {
        if (string.IsNullOrWhiteSpace(_state.SummaryText)) return;
        var pasteboard = NSPasteboard.GeneralPasteboard;
        pasteboard.ClearContents();
        pasteboard.SetStringForType(_state.SummaryText, NSPasteboard.NSPasteboardTypeString);
    }

    private static NSProgressIndicator Spinner() => new()
    {
        Style = NSProgressIndicatorStyle.Spinning,
        ControlSize = NSControlSize.Small,
        Indeterminate = true,
        IsDisplayedWhenStopped = false,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private static NSButton PanelButton(string title)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        return button;
    }

    private static NSPopUpButton LanguagePicker(string label)
    {
        var picker = new NSPopUpButton { ControlSize = NSControlSize.Small, ToolTip = label, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(picker, label);
        picker.WidthAnchor.ConstraintGreaterThanOrEqualTo(140).Active = true;
        return picker;
    }
}

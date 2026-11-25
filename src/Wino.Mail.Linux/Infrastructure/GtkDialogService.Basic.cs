using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

// Alerts, the text input and the notices that need no screen. Widgets of an alert are created and read inside the
// main-thread part of PresentAsync; the awaited result is a plain value.
internal sealed partial class GtkDialogService
{
    private const string Cancel = "cancel";
    private const string Primary = "primary";
    private const string Secondary = "secondary";

    public async Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon)
        => await AlertAsync(new AlertRequest(title ?? string.Empty, message ?? string.Empty, [new AlertResponse(Primary, Translator.Buttons_OK)])
        {
            CloseResponse = Primary,
            DefaultResponse = Primary,
        });

    public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
    {
        // As on macOS: the shared "Yes" of the discard-draft question becomes the destructive verb, and Enter cancels.
        var isDiscardDraft = title == Translator.DialogMessage_DiscardDraftConfirmationTitle && confirmationButtonTitle == Translator.Buttons_Yes;
        var isDestructive = isDiscardDraft || confirmationButtonTitle == Translator.FolderOperation_Delete || confirmationButtonTitle == Translator.Buttons_Delete;
        return await AlertAsync(new AlertRequest(title ?? string.Empty, question ?? string.Empty,
            [
                new AlertResponse(Cancel, Translator.Buttons_Cancel),
                new AlertResponse(Primary, isDiscardDraft ? Translator.Buttons_Discard : confirmationButtonTitle,
                    isDestructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = isDestructive ? Cancel : Primary,
        }) == Primary;
    }

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText,
        string cancelButtonText, WinoCustomMessageDialogIcon? icon = null)
        => await AlertAsync(new AlertRequest(title ?? string.Empty, description ?? string.Empty,
            [
                new AlertResponse(Cancel, cancelButtonText),
                new AlertResponse(Secondary, secondaryButtonText),
                new AlertResponse(Primary, primaryButtonText, Adw.ResponseAppearance.Suggested),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = Primary,
            PreferWideLayout = true,
        }) switch
        {
            Primary => ThreeButtonDialogResult.Primary,
            Secondary => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel,
        };

    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon,
        string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
        => CustomMessageAsync(title, description, approveButtonText, Adw.ResponseAppearance.Suggested, cancelButtonText, dontAskAgainConfigurationKey);

    public Task<bool> ShowHardDeleteConfirmationAsync()
        => CustomMessageAsync(Translator.DialogMessage_HardDeleteConfirmationMessage, Translator.DialogMessage_HardDeleteConfirmationTitle,
            Translator.Buttons_Yes, Adw.ResponseAppearance.Destructive, Translator.Buttons_No, string.Empty);

    public Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
        => PresentAsync(async owner =>
        {
            var current = currentInput ?? string.Empty;
            var entry = Gtk.Entry.New();
            entry.SetText(current);
            entry.SetActivatesDefault(true);
            WinoA11y.Label(entry, dialogTitle);
            IDisposable? changed = null;
            string answer;
            try
            {
                answer = await AlertPresenter.PresentAsync(owner, new AlertRequest(dialogTitle ?? string.Empty, dialogDescription ?? string.Empty,
                    [
                        new AlertResponse(Cancel, Translator.Buttons_Cancel),
                        new AlertResponse(Primary, primaryButtonText, Adw.ResponseAppearance.Suggested, !string.IsNullOrWhiteSpace(current)),
                    ])
                {
                    CloseResponse = Cancel,
                    DefaultResponse = Primary,
                    ExtraChild = entry,
                    InitialFocus = entry,
                    Configured = dialog => changed = GtkSignals.OnChanged(entry,
                        () => dialog.SetResponseEnabled(Primary, !string.IsNullOrWhiteSpace(entry.GetText()))),
                });
            }
            finally { changed?.Dispose(); }
            // The Windows contract: the new text only when it was confirmed and changed, otherwise empty.
            var text = entry.GetText();
            return answer == Primary && text != current ? text : string.Empty;
        }, string.Empty);

    public async Task<CalendarEventTargetType?> ShowRecurringEventDeleteDialogAsync()
        => await AlertAsync(new AlertRequest(Translator.DialogMessage_DeleteRecurringEventTitle, Translator.DialogMessage_DeleteRecurringEventMessage,
            [
                new AlertResponse(Cancel, Translator.Buttons_Cancel),
                new AlertResponse(Secondary, Translator.CalendarContextMenu_AllEventsInSeries, Adw.ResponseAppearance.Destructive),
                new AlertResponse(Primary, Translator.CalendarContextMenu_ThisEventOnly, Adw.ResponseAppearance.Destructive),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = Cancel,
            PreferWideLayout = true,
        }) switch
        {
            Primary => CalendarEventTargetType.Single,
            Secondary => CalendarEventTargetType.Series,
            _ => null,
        };

    public void ShowNotSupportedMessage()
        => InfoBarMessage(Translator.Info_UnsupportedFunctionalityTitle, Translator.Info_UnsupportedFunctionalityDescription, InfoBarMessageType.Information);

    public void ShowReadOnlyCalendarMessage()
        => InfoBarMessage(Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message, InfoBarMessageType.Warning);

    private async Task<bool> CustomMessageAsync(string title, string description, string approveButtonText, Adw.ResponseAppearance appearance,
        string cancelButtonText, string dontAskAgainConfigurationKey)
    {
        var dontAsk = !string.IsNullOrEmpty(dontAskAgainConfigurationKey);
        // Same contract as Windows and macOS: a key already marked "don't ask again" answers without showing.
        if (dontAsk && configuration.Get(dontAskAgainConfigurationKey, false)) return false;

        var (answer, suppressed) = await PresentAsync(async owner =>
        {
            var check = dontAsk ? Gtk.CheckButton.NewWithLabel(Translator.Dialog_DontAskAgain) : null;
            AlertResponse[] responses = string.IsNullOrEmpty(cancelButtonText)
                ? [new AlertResponse(Primary, approveButtonText, appearance)]
                : [new AlertResponse(Cancel, cancelButtonText), new AlertResponse(Primary, approveButtonText, appearance)];
            var response = await AlertPresenter.PresentAsync(owner, new AlertRequest(title ?? string.Empty, description ?? string.Empty, responses)
            {
                CloseResponse = string.IsNullOrEmpty(cancelButtonText) ? AlertPresenter.Closed : Cancel,
                DefaultResponse = appearance == Adw.ResponseAppearance.Destructive ? (string.IsNullOrEmpty(cancelButtonText) ? null : Cancel) : Primary,
                ExtraChild = check,
            });
            return (response, check?.GetActive() ?? false);
        }, (AlertPresenter.Closed, false));

        if (suppressed) configuration.Set(dontAskAgainConfigurationKey, true);
        return answer == Primary;
    }
}

using System.Collections.ObjectModel;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Dialogs for the Linux proof of concept. Info bars are toasts, and the certificate trust and open-link questions
/// are real dialogs. Every other dialog is answered as the user dismissing it (confirmations declined, pickers
/// empty) and logged, because its screen does not exist yet.
/// </summary>
internal sealed partial class GtkDialogService(IDispatcher dispatcher, Func<Views.MainWindow?> window) : IMailDialogService
{
    private static T Dismiss<T>(string name, string? detail, T answer)
    {
        Serilog.Log.Information("Dialog {Dialog} is not available on Linux yet and was dismissed.", name);
        return answer;
    }

    private static Task<T> DismissAsync<T>(string name, string? detail, T answer) => Task.FromResult(Dismiss(name, detail, answer));

    private static Task DismissAsync(string name, string? detail = null)
    {
        Dismiss(name, detail, true);
        return Task.CompletedTask;
    }

    public Task<string> PickWindowsFolderAsync() => DismissAsync(nameof(PickWindowsFolderAsync), null, string.Empty);

    public Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters) => DismissAsync(nameof(PickWindowsFileContentAsync), null, Array.Empty<byte>());

    public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        => DismissAsync(nameof(ShowConfirmationDialogAsync), $"{title}: {question}", false);

    public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon)
        => DismissAsync(nameof(ShowMessageAsync), $"{icon} {title}: {message}");



    public void ShowNotSupportedMessage() => Dismiss(nameof(ShowNotSupportedMessage), null, true);

    public Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
        => DismissAsync(nameof(ShowTextInputDialogAsync), dialogTitle, string.Empty);

    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon,
        string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
        => DismissAsync(nameof(ShowWinoCustomMessageDialogAsync), $"{title}: {description}", false);

    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        => Task.FromException<AccountCreationDialogResult>(new NotSupportedException(
            Dismiss(nameof(ShowAccountProviderSelectionDialogAsync), null, "Account provider selection has no screen on Linux yet.")));

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult)
        => new GtkAccountCreationDialog();

    public Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters) => DismissAsync(nameof(PickFilesAsync), null, new List<SharedFile>());

    public Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
        => DismissAsync(nameof(PickFilesMetadataAsync), null, new List<PickedFileMetadata>());

    public Task<string> PickFilePathAsync(string saveFileName) => DismissAsync(nameof(PickFilePathAsync), saveFileName, string.Empty);

    public Task<MailAccount?> PickAccountAsync(IReadOnlyList<MailAccount> accounts, string title)
        => DismissAsync<MailAccount?>(nameof(PickAccountAsync), title, null);

    public void ShowReadOnlyCalendarMessage() => Dismiss(nameof(ShowReadOnlyCalendarMessage), null, true);

    public Task<CalendarEventTargetType?> ShowRecurringEventDeleteDialogAsync()
        => DismissAsync<CalendarEventTargetType?>(nameof(ShowRecurringEventDeleteDialogAsync), null, null);

    public Task<bool> ShowHardDeleteConfirmationAsync() => DismissAsync(nameof(ShowHardDeleteConfirmationAsync), null, false);

    public Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText,
        string cancelButtonText, WinoCustomMessageDialogIcon? icon = null)
        => DismissAsync(nameof(ShowThreeButtonDialogAsync), $"{title}: {description}", ThreeButtonDialogResult.Cancel);

    public Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
        => DismissAsync(nameof(HandleSystemFolderConfigurationDialogAsync), accountId.ToString());

    public Task<IMailItemFolder?> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
        => DismissAsync<IMailItemFolder?>(nameof(ShowMoveMailFolderDialogAsync), null, null);

    public Task<MailAccount?> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
        => DismissAsync<MailAccount?>(nameof(ShowAccountPickerDialogAsync), null, null);

    public Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
        => DismissAsync(nameof(ShowSingleCalendarPickerDialogAsync), null, new AccountCalendarPickingResult(null, false));

    public Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
        => DismissAsync<ContactCreateDestination?>(nameof(ShowContactDestinationPickerDialogAsync), null, null);

    public Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
        => DismissAsync<ContactListCreationResult?>(nameof(ShowNewContactListDialogAsync), null, null);

    public Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
        => DismissAsync<AccountTaskList?>(nameof(ShowTaskListPickerDialogAsync), null, null);

    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
        => DismissAsync(nameof(ShowAccountReorderDialogAsync));

    public Task<IMailItemFolder?> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
        => DismissAsync<IMailItemFolder?>(nameof(PickFolderAsync), reason.ToString(), null);

    public Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
        => DismissAsync<AccountSignature?>(nameof(ShowSignatureEditorDialog), null, null);

    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
        => DismissAsync<ICreateAccountAliasDialog>(nameof(ShowCreateAccountAliasDialogAsync), null, new GtkAliasDialogResult());

    public Task<MailCategoryDialogResult?> ShowEditMailCategoryDialogAsync(MailCategory? category = null)
        => DismissAsync<MailCategoryDialogResult?>(nameof(ShowEditMailCategoryDialogAsync), category?.Name, null);

    public Task<FolderRenameDialogResult?> ShowRenameFolderDialogAsync(string folderName, string? displayName, bool canRenameOnServer)
        => DismissAsync<FolderRenameDialogResult?>(nameof(ShowRenameFolderDialogAsync), folderName, null);

    public Task ShowMessageSourceDialogAsync(string messageSource) => DismissAsync(nameof(ShowMessageSourceDialogAsync));

    public Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
        => DismissAsync(nameof(ShowImapValidationFailedDialogAsync), errorMessage);


    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null)
        => DismissAsync(nameof(ShowKeyboardShortcutDialogAsync), null, KeyboardShortcutDialogResult.Canceled());

    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync() => DismissAsync<WinoAccount?>(nameof(ShowWinoAccountRegistrationDialogAsync), null, null);

    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync() => DismissAsync<WinoAccount?>(nameof(ShowWinoAccountLoginDialogAsync), null, null);

    public Task<bool> ShowWinoAccountExportDialogAsync() => DismissAsync(nameof(ShowWinoAccountExportDialogAsync), null, false);

    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request)
        => DismissAsync<string?>(nameof(ShowWinoAccountSyncSecretDialogAsync), null, null);

    public Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
        => DismissAsync<UnlimitedAccountsPurchaseChannel?>(nameof(ShowUnlimitedAccountsPurchaseChannelDialogAsync), null, null);


    private sealed class GtkAliasDialogResult : ICreateAccountAliasDialog
    {
        public MailAccountAlias? CreatedAccountAlias { get; set; }
    }

    private sealed class GtkAccountCreationDialog : IAccountCreationDialog
    {
        public AccountCreationDialogState State { get; set; }

        public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource) => Task.CompletedTask;

        public void Complete(bool cancel) { }
    }
}

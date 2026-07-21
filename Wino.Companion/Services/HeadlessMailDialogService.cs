#nullable enable
using System.Collections.ObjectModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Companion.Services;

/// <summary>
/// The companion never creates UI. Any legacy backend path that still asks for a
/// dialog receives the cancel value; UI workflows must collect the choice before
/// sending their command.
/// </summary>
public sealed class HeadlessMailDialogService : IMailDialogService
{
    private static T Cancel<T>() => default!;

    public void ShowReadOnlyCalendarMessage() { }
    public Task<bool> ShowHardDeleteConfirmationAsync() => Task.FromResult(false);
    public Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null) => Task.FromResult(default(ThreeButtonDialogResult));
    public Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService) => Task.CompletedTask;
    public Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders) => Task.FromResult(Cancel<IMailItemFolder>());
    public Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts) => Task.FromResult(Cancel<MailAccount>());
    public Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups) => Task.FromResult(Cancel<AccountCalendarPickingResult>());
    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => Task.CompletedTask;
    public Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService) => Task.FromResult(Cancel<IMailItemFolder>());
    public Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null) => Task.FromResult(Cancel<AccountSignature>());
    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync() => Task.FromResult(Cancel<ICreateAccountAliasDialog>());
    public Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null!) => Task.FromResult(Cancel<MailCategoryDialogResult>());
    public Task ShowMessageSourceDialogAsync(string messageSource) => Task.CompletedTask;
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null!) => Task.FromResult(Cancel<KeyboardShortcutDialogResult>());
    public Task<AccountContact?> ShowEditContactDialogAsync(AccountContact? contact = null) => Task.FromResult<AccountContact?>(null);
    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync() => Task.FromResult<WinoAccount?>(null);
    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync() => Task.FromResult<WinoAccount?>(null);
    public Task<WinoAccountSyncExportResult?> ShowWinoAccountExportDialogAsync() => Task.FromResult<WinoAccountSyncExportResult?>(null);
    public Task<string> PickWindowsFolderAsync() => Task.FromResult(Cancel<string>());
    public Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle) => Task.FromResult(false);
    public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon) => Task.CompletedTask;
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) { }
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action) { }
    public void ShowNotSupportedMessage() { }
    public Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText) => Task.FromResult(Cancel<string>());
    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "") => Task.FromResult(false);
    public Task<bool> ShowCustomThemeBuilderDialogAsync() => Task.FromResult(false);
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders) => Task.FromResult(Cancel<AccountCreationDialogResult>());
    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult) => throw new NotSupportedException("Account creation dialogs are a UWP concern.");
    public Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters) => Task.FromResult(new List<SharedFile>());
    public Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters) => Task.FromResult(new List<PickedFileMetadata>());
    public Task<string> PickFilePathAsync(string saveFileName) => Task.FromResult(Cancel<string>());
}

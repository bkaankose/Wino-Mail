#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
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

namespace Wino.BackgroundService.Services;

/// <summary>
/// The request pipeline (WinoRequestProcessor) can ask for user input mid-preparation
/// (hard delete confirmation, folder picker for moves, rename inputs). There is no UI in
/// the companion, so every prompt resolves to its cancel/negative default and logs a
/// warning. Flows that genuinely need these prompts must resolve them in the UI process
/// before the request crosses the pipe.
/// </summary>
public sealed class HeadlessMailDialogService : IMailDialogService
{
    private static readonly ILogger Logger = Log.ForContext<HeadlessMailDialogService>();

    private static T Cancelled<T>(string member, T fallbackValue)
    {
        Logger.Warning("Interactive dialog {Member} was requested in the headless companion; returning the cancel default. This flow must collect input in the UI process.", member);
        return fallbackValue;
    }

    public Task<bool> ShowHardDeleteConfirmationAsync() => Task.FromResult(Cancelled(nameof(ShowHardDeleteConfirmationAsync), false));

    public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        => Task.FromResult(Cancelled(nameof(ShowConfirmationDialogAsync), false));

    public Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
        => Task.FromResult(Cancelled<string>(nameof(ShowTextInputDialogAsync), null!));

    public Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
        => Task.FromResult(Cancelled<IMailItemFolder>(nameof(PickFolderAsync), null!));

    public Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
        => Task.FromResult(Cancelled<IMailItemFolder>(nameof(ShowMoveMailFolderDialogAsync), null!));

    public Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null)
        => Task.FromResult(Cancelled(nameof(ShowThreeButtonDialogAsync), default(ThreeButtonDialogResult)));

    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
        => Task.FromResult(Cancelled(nameof(ShowWinoCustomMessageDialogAsync), false));

    public void ShowReadOnlyCalendarMessage() { }

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType)
        => Logger.Information("Companion info bar message suppressed: {Title} - {Message}", title, message);

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
        => Logger.Information("Companion info bar message suppressed: {Title} - {Message}", title, message);

    public void ShowNotSupportedMessage() { }

    public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon) => Task.CompletedTask;

    public Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService) => Task.CompletedTask;

    public Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
        => Task.FromResult(Cancelled<MailAccount>(nameof(ShowAccountPickerDialogAsync), null!));

    public Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
        => Task.FromResult(Cancelled<AccountCalendarPickingResult>(nameof(ShowSingleCalendarPickerDialogAsync), null!));

    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => Task.CompletedTask;

    public Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
        => Task.FromResult(Cancelled<AccountSignature>(nameof(ShowSignatureEditorDialog), null!));

    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
        => Task.FromResult(Cancelled<ICreateAccountAliasDialog>(nameof(ShowCreateAccountAliasDialogAsync), null!));

    public Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null!)
        => Task.FromResult(Cancelled<MailCategoryDialogResult>(nameof(ShowEditMailCategoryDialogAsync), null!));

    public Task ShowMessageSourceDialogAsync(string messageSource) => Task.CompletedTask;

    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null!)
        => Task.FromResult(Cancelled<KeyboardShortcutDialogResult>(nameof(ShowKeyboardShortcutDialogAsync), null!));

    public Task<AccountContact?> ShowEditContactDialogAsync(AccountContact? contact = null)
        => Task.FromResult(Cancelled<AccountContact?>(nameof(ShowEditContactDialogAsync), null));

    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
        => Task.FromResult(Cancelled<WinoAccount?>(nameof(ShowWinoAccountRegistrationDialogAsync), null));

    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
        => Task.FromResult(Cancelled<WinoAccount?>(nameof(ShowWinoAccountLoginDialogAsync), null));

    public Task<WinoAccountSyncExportResult?> ShowWinoAccountExportDialogAsync()
        => Task.FromResult(Cancelled<WinoAccountSyncExportResult?>(nameof(ShowWinoAccountExportDialogAsync), null));

    public Task<string> PickWindowsFolderAsync()
        => Task.FromResult(Cancelled<string>(nameof(PickWindowsFolderAsync), null!));

    public Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
        => Task.FromResult(Cancelled<byte[]>(nameof(PickWindowsFileContentAsync), null!));

    public Task<bool> ShowCustomThemeBuilderDialogAsync()
        => Task.FromResult(Cancelled(nameof(ShowCustomThemeBuilderDialogAsync), false));

    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        => Task.FromResult(Cancelled<AccountCreationDialogResult>(nameof(ShowAccountProviderSelectionDialogAsync), null!));

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult)
        => throw new NotSupportedException("Account creation dialogs are a UI process concern.");

    public Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
        => Task.FromResult(Cancelled(nameof(PickFilesAsync), new List<SharedFile>()));

    public Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
        => Task.FromResult(Cancelled(nameof(PickFilesMetadataAsync), new List<PickedFileMetadata>()));

    public Task<string> PickFilePathAsync(string saveFileName)
        => Task.FromResult(Cancelled<string>(nameof(PickFilePathAsync), null!));
}

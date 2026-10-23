#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Contacts;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Domain.Interfaces;

public interface IMailDialogService : IDialogServiceBase
{
    void ShowReadOnlyCalendarMessage();
    Task<bool> ShowHardDeleteConfirmationAsync();
    Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title,
                                                             string description,
                                                             string primaryButtonText,
                                                             string secondaryButtonText,
                                                             string cancelButtonText,
                                                             MailCalCustomMessageDialogIcon? icon = null);
    Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService);

    // Custom dialogs
    Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders);
    Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts);
    Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups);
    Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations);
    Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount);
    Task<AccountTaskList?> ShowTaskListPickerDialogAsync(
        IReadOnlyList<AccountTaskList> taskLists,
        IReadOnlyList<MailAccount> accounts);

    /// <summary>
    /// Displays a dialog to the user for reordering accounts.
    /// </summary>
    /// <param name="availableAccounts">Available accounts in order.</param>
    /// <returns>Result model that has dict of AccountId-AccountOrder.</returns>
    Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts);

    /// <summary>
    /// Presents a dialog to the user for selecting folder.
    /// </summary>
    /// <param name="accountId">Account to get folders for.</param>
    /// <param name="reason">The reason behind the picking operation
    /// <returns>Selected folder structure. Null if none.</returns>
    Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService);

    /// <summary>
    /// Presents a dialog to the user for signature creation/modification.
    /// </summary>
    /// <returns>Signature information. Null if canceled.</returns>
    Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null);

    /// <summary>
    /// Presents a dialog to the user for account alias creation/modification.
    /// </summary>
    /// <returns>Created alias model if not canceled.</returns>
    Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync();

    /// <summary>
    /// Presents a dialog to the user for mail category creation/modification.
    /// </summary>
#pragma warning disable CS8625
    Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null);
#pragma warning restore CS8625

    /// <summary>
    /// Presents a dialog to the user to show email source.
    /// </summary>
    Task ShowMessageSourceDialogAsync(string messageSource);

    /// <summary>
    /// Presents the detailed error and temporary protocol transcript from a failed IMAP/SMTP validation.
    /// </summary>
    Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog);

    /// <summary>
    /// Shows a fail-closed server-certificate consent dialog and lets the user inspect
    /// the complete certificate before making an endpoint-scoped trust decision.
    /// </summary>
    Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData);

    /// <summary>
    /// Presents a dialog to the user for keyboard shortcut creation/modification.
    /// </summary>
    /// <param name="existingShortcut">Existing shortcut to edit, or null for new shortcut.</param>
    /// <returns>Dialog result with shortcut information.</returns>
#pragma warning disable CS8625
    Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null);
#pragma warning restore CS8625

    /// <summary>
    /// Asks for the backup password that locks or unlocks a backup file.
    /// Returns null when the user cancels.
    /// </summary>
    Task<string?> ShowBackupPasswordDialogAsync(SyncSnapshotSecretRequest request);
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces;

public interface IMailDialogService : IDialogServiceBase
{
    /// <summary>Plays the Wino Assistant demo video. Only Windows ships the video; other heads keep this no-op.</summary>
    Task ShowWinoAssistantDemoAsync() => Task.CompletedTask;

    void ShowReadOnlyCalendarMessage();

    /// <summary>
    /// Asks whether deleting an occurrence of a recurring event removes only that occurrence or the whole series.
    /// </summary>
    /// <returns>The chosen scope, or null when the user cancels.</returns>
    Task<CalendarEventTargetType?> ShowRecurringEventDeleteDialogAsync();
    Task<bool> ShowHardDeleteConfirmationAsync();
    Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title,
                                                             string description,
                                                             string primaryButtonText,
                                                             string secondaryButtonText,
                                                             string cancelButtonText,
                                                             WinoCustomMessageDialogIcon? icon = null);
    Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService);

    // Custom dialogs
    Task<IMailItemFolder?> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders);
    Task<MailAccount?> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts);
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
    Task<IMailItemFolder?> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService);

    /// <summary>
    /// Presents a dialog to the user for signature creation/modification.
    /// </summary>
    /// <returns>Signature information. Null if canceled.</returns>
    Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null);

    /// <summary>
    /// Presents a dialog to the user for account alias creation/modification.
    /// </summary>
    /// <returns>Created alias model if not canceled.</returns>
    Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync();

    /// <summary>
    /// Presents a dialog to the user for mail category creation/modification.
    /// </summary>
    /// <returns>The edited category, or null when the user cancels.</returns>
    Task<MailCategoryDialogResult?> ShowEditMailCategoryDialogAsync(MailCategory? category = null);

    /// <summary>
    /// Asks for a folder's server name and its local-only display name.
    /// </summary>
    /// <param name="canRenameOnServer">False keeps the server name read-only, as for system folders.</param>
    /// <returns>The entered names, or null when the user cancels.</returns>
    Task<FolderRenameDialogResult?> ShowRenameFolderDialogAsync(string folderName, string? displayName, bool canRenameOnServer);

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
    /// A dangerous prompt keeps its accept button disabled until the user acknowledges the risk.
    /// </summary>
    Task<bool> ShowServerCertificateTrustDialogAsync(ServerCertificateTrustPrompt prompt);

    /// <summary>
    /// Presents a dialog to the user for keyboard shortcut creation/modification.
    /// </summary>
    /// <param name="existingShortcut">Existing shortcut to edit, or null for new shortcut.</param>
    /// <returns>Dialog result with shortcut information.</returns>
    Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null);

    Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync();

    Task<WinoAccount?> ShowWinoAccountLoginDialogAsync();

    /// <summary>
    /// Confirms a backup to the Wino Account. Returns false when the user closes the dialog.
    /// </summary>
    Task<bool> ShowWinoAccountExportDialogAsync();

    /// <summary>
    /// Asks for the Wino Account password or the sync passphrase that unlocks a sync snapshot.
    /// Returns null when the user cancels.
    /// </summary>
    Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request);

    Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync();

    /// <summary>
    /// Shows where a link from a message goes before it opens.
    /// </summary>
    /// <param name="linkText">The anchor's visible text, or null when the renderer had no anchor.</param>
    /// <param name="originalUri">The link as written in the message.</param>
    /// <param name="cleanedUri">The link that will open, after tracking parameters were removed.</param>
    /// <param name="removedParameterCount">How many tracking parameters were removed.</param>
    /// <param name="browserName">The browser chosen for the account, or null for the system default.</param>
    /// <returns>True when the user chose to open the link.</returns>
    Task<bool> ShowOpenLinkConfirmationAsync(string? linkText, Uri originalUri, Uri cleanedUri, int removedParameterCount, string? browserName);
}

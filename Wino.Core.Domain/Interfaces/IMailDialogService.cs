using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Interfaces;

public interface IMailDialogService : IDialogServiceBase
{
    Task<bool> ShowHardDeleteConfirmationAsync();
    Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService);

    // Custom dialogs
    Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders);
    Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts);

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
    Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature signatureModel = null);

    /// <summary>
    /// Presents a dialog to the user for account alias creation/modification.
    /// </summary>
    /// <returns>Created alias model if not canceled.</returns>
    Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync();

    /// <summary>
    /// Presents a dialog to the user to show email source.
    /// </summary>
    Task ShowMessageSourceDialogAsync(string messageSource);

    /// <summary>
    /// Presents a dialog to the user for keyboard shortcut creation/modification.
    /// </summary>
    /// <param name="existingShortcut">Existing shortcut to edit, or null for new shortcut.</param>
    /// <returns>Dialog result with shortcut information.</returns>
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
    Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null);
#pragma warning restore CS8625
}

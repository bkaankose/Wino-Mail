using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Interfaces
{
    public interface IDialogService
    {
        Task<string> PickWindowsFolderAsync();
        Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters);
        Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle);
        Task<bool> ShowHardDeleteConfirmationAsync();
        Task<IStoreRatingDialog> ShowRatingDialogAsync();
        Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService);
        Task<bool> ShowCustomThemeBuilderDialogAsync();

        Task ShowMessageAsync(string message, string title);
        void InfoBarMessage(string title, string message, InfoBarMessageType messageType);
        void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action);

        void ShowNotSupportedMessage();

        // Custom dialogs
        Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders);
        Task<AccountCreationDialogResult> ShowNewAccountMailProviderDialogAsync(List<IProviderDetail> availableProviders);
        IAccountCreationDialog GetAccountCreationDialog(MailProviderType type);
        Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription);
        Task<MailAccount> ShowEditAccountDialogAsync(MailAccount account);
        Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts);

        /// <summary>
        /// Presents a dialog to the user for selecting folder.
        /// </summary>
        /// <param name="accountId">Account to get folders for.</param>
        /// <param name="reason">The reason behind the picking operation
        /// <returns>Selected folder structure. Null if none.</returns>
        Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService);
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Common;

namespace Wino.Core.Domain.Interfaces;

public interface IDialogServiceBase
{
    Task<string> PickWindowsFolderAsync();
    Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters);
    Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle);
    Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon);
    void InfoBarMessage(string title, string message, InfoBarMessageType messageType);
    void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action);
    void ShowNotSupportedMessage();
    Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText);
    Task<bool> ShowWinoCustomMessageDialogAsync(string title,
                                        string description,
                                        string approveButtonText,
                                        WinoCustomMessageDialogIcon? icon,
                                        string cancelButtonText = "",
                                        string dontAskAgainConfigurationKey = "");
    Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders);
    IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult);
    Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters);
    Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters);
    Task<string> PickFilePathAsync(string saveFileName);

#nullable enable
    /// <summary>Platform-neutral name for picking a folder path; empty or null when the user cancels.</summary>
    Task<string> PickFolderAsync() => PickWindowsFolderAsync();

    /// <summary>
    /// True when the head shows its own account picker for a new mail from several accounts, in reply to
    /// CreateNewMailWithMultipleAccountsRequested (Windows anchors a flyout to the navigation item).
    /// Other heads leave it false and the mail shell asks <see cref="PickAccountAsync"/>.
    /// </summary>
    bool HandlesMultipleAccountNewMailPicker => false;

    /// <summary>Lets the user choose one of <paramref name="accounts"/>; null when the user cancels.</summary>
    Task<MailAccount?> PickAccountAsync(IReadOnlyList<MailAccount> accounts, string title)
        => Task.FromResult<MailAccount?>(accounts.Count > 0 ? accounts[0] : null);
#nullable restore
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    Task<bool> ShowCustomThemeBuilderDialogAsync();
    Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders);
    IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult);
    Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters);
    Task<string> PickFilePathAsync(string saveFileName);
}

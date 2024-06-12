using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Toolkit.Uwp.Helpers;
using Serilog;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Messages.Shell;
using Wino.Core.Messages.Synchronization;
using Wino.Core.Requests;
using Wino.Core.UWP.Extensions;
using Wino.Dialogs;

namespace Wino.Services
{
    public class DialogService : IDialogService
    {
        private SemaphoreSlim _presentationSemaphore = new SemaphoreSlim(1);

        private readonly IThemeService _themeService;

        public DialogService(IThemeService themeService)
        {
            _themeService = themeService;
        }

        public void ShowNotSupportedMessage()
        {
            InfoBarMessage(Translator.Info_UnsupportedFunctionalityTitle, Translator.Info_UnsupportedFunctionalityDescription, InfoBarMessageType.Error);
        }

        public async Task ShowMessageAsync(string message, string title)
        {
            var dialog = new WinoMessageDialog()
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentation(() => dialog.ShowDialogAsync(title, message));
        }

        /// <summary>
        /// Waits for PopupRoot to be available before presenting the dialog and returns the result after presentation.
        /// </summary>
        /// <param name="dialog">Dialog to present and wait for closing.</param>
        /// <returns>Dialog result from WinRT.</returns>
        private async Task<ContentDialogResult> HandleDialogPresentationAsync(ContentDialog dialog)
        {
            await _presentationSemaphore.WaitAsync();

            try
            {
                return await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Handling dialog service failed. Dialog was {dialog.GetType().Name}");
            }
            finally
            {
                _presentationSemaphore.Release();
            }

            return ContentDialogResult.None;
        }

        /// <summary>
        /// Waits for PopupRoot to be available before executing the given Task that returns customized result.
        /// </summary>
        /// <param name="executionTask">Task that presents the dialog and returns result.</param>
        /// <returns>Dialog result from the custom dialog.</returns>
        private async Task<bool> HandleDialogPresentation(Func<Task<bool>> executionTask)
        {
            await _presentationSemaphore.WaitAsync();

            try
            {
                return await executionTask();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Handling dialog service failed.");
            }
            finally
            {
                _presentationSemaphore.Release();
            }

            return false;
        }

        public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        {
            var dialog = new ConfirmationDialog()
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            return await HandleDialogPresentation(() => dialog.ShowDialogAsync(title, question, confirmationButtonTitle));
        }

        public async Task<AccountCreationDialogResult> ShowNewAccountMailProviderDialogAsync(List<IProviderDetail> availableProviders)
        {
            var dialog = new NewAccountDialog
            {
                Providers = availableProviders,
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(dialog);

            return dialog.Result;
        }

        public IAccountCreationDialog GetAccountCreationDialog(MailProviderType type)
        {
            IAccountCreationDialog dialog = null;

            if (type == MailProviderType.IMAP4)
            {
                dialog = new NewImapSetupDialog
                {
                    RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
                };
            }
            else
            {
                dialog = new AccountCreationDialog
                {
                    RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
                };
            }

            return dialog;
        }

        public void InfoBarMessage(string title, string message, InfoBarMessageType messageType)
            => WeakReferenceMessenger.Default.Send(new InfoBarMessageRequested(messageType, title, message));

        public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
            => WeakReferenceMessenger.Default.Send(new InfoBarMessageRequested(messageType, title, message, actionButtonText, action));

        public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription)
        {
            var inputDialog = new TextInputDialog()
            {
                CurrentInput = currentInput,
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme(),
                Title = dialogTitle
            };

            inputDialog.SetDescription(dialogDescription);

            await HandleDialogPresentationAsync(inputDialog);

            if (inputDialog.HasInput.GetValueOrDefault() && !currentInput.Equals(inputDialog.CurrentInput))
                return inputDialog.CurrentInput;

            return string.Empty;
        }

        public async Task<string> PickWindowsFolderAsync()
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };

            picker.FileTypeFilter.Add("*");

            var pickedFolder = await picker.PickSingleFolderAsync();

            if (pickedFolder != null)
            {
                Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.AddOrReplace("FolderPickerToken", pickedFolder);

                return pickedFolder.Path;
            }

            return string.Empty;
        }

        public async Task<MailAccount> ShowEditAccountDialogAsync(MailAccount account)
        {
            var editAccountDialog = new AccountEditDialog(account)
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(editAccountDialog);

            return editAccountDialog.IsSaved ? editAccountDialog.Account : null;
        }

        public async Task<IStoreRatingDialog> ShowRatingDialogAsync()
        {
            var storeDialog = new StoreRatingDialog()
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(storeDialog);

            return storeDialog;
        }

        public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
        {
            try
            {
                var configurableFolder = await folderService.GetFoldersAsync(accountId);

                var systemFolderConfigurationDialog = new SystemFolderConfigurationDialog(configurableFolder)
                {
                    RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
                };

                await HandleDialogPresentationAsync(systemFolderConfigurationDialog);

                var configuration = systemFolderConfigurationDialog.Configuration;

                if (configuration != null)
                {
                    var updatedAccount = await folderService.UpdateSystemFolderConfigurationAsync(accountId, configuration);

                    // Update account menu item and force re-synchronization.
                    WeakReferenceMessenger.Default.Send(new AccountUpdatedMessage(updatedAccount));

                    var options = new SynchronizationOptions()
                    {
                        AccountId = updatedAccount.Id,
                        Type = SynchronizationType.Full,
                    };

                    WeakReferenceMessenger.Default.Send(new NewSynchronizationRequested(options));
                }

                if (configuration != null)
                {
                    InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);
                }
            }
            catch (Exception ex)
            {
                InfoBarMessage(Translator.Error_FailedToSetupSystemFolders_Title, ex.Message, InfoBarMessageType.Error);
            }
        }

        public async Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
        {
            var moveDialog = new MoveMailDialog(availableFolders)
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(moveDialog);

            return moveDialog.SelectedFolder;
        }

        public async Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
        {
            var allFolders = await folderService.GetFolderStructureForAccountAsync(accountId, true);

            return await ShowMoveMailFolderDialogAsync(allFolders.Folders);
        }

        public async Task<bool> ShowCustomThemeBuilderDialogAsync()
        {
            var themeBuilderDialog = new CustomThemeBuilderDialog()
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            var dialogResult = await HandleDialogPresentationAsync(themeBuilderDialog);

            return dialogResult == ContentDialogResult.Primary;
        }

        private async Task<StorageFile> PickFileAsync(params object[] typeFilters)
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail
            };

            foreach (var filter in typeFilters)
            {
                picker.FileTypeFilter.Add(filter.ToString());
            }

            var file = await picker.PickSingleFileAsync();

            if (file == null) return null;

            Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.AddOrReplace("FilePickerPath", file);

            return file;
        }

        public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
        {
            var file = await PickFileAsync(typeFilters);

            if (file == null) return Array.Empty<byte>();

            return await file.ReadBytesAsync();
        }

        public Task<bool> ShowHardDeleteConfirmationAsync() => ShowConfirmationDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage, Translator.DialogMessage_HardDeleteConfirmationTitle, Translator.Buttons_Yes);

        public async Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
        {
            var accountPicker = new AccountPickerDialog(availableAccounts)
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(accountPicker);

            return accountPicker.PickedAccount;
        }

        public async Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature signatureModel = null)
        {
            SignatureEditorDialog signatureEditorDialog;
            if (signatureModel != null)
            {
                signatureEditorDialog = new SignatureEditorDialog(signatureModel)
                {
                    RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
                };
            }
            else
            {
                signatureEditorDialog = new SignatureEditorDialog()
                {
                    RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
                };
            }

            var result = await HandleDialogPresentationAsync(signatureEditorDialog);

            return result == ContentDialogResult.Primary ? signatureEditorDialog.Result : null;
        }
    }
}

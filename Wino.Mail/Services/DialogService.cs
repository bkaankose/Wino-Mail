using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Toolkit.Uwp.Helpers;
using Serilog;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.UWP.Extensions;
using Wino.Dialogs;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Services
{
    public class DialogService : IDialogService
    {
        private SemaphoreSlim _presentationSemaphore = new SemaphoreSlim(1);

        private readonly IThemeService _themeService;
        private readonly IConfigurationService _configurationService;

        public DialogService(IThemeService themeService, IConfigurationService configurationService)
        {
            _themeService = themeService;
            _configurationService = configurationService;
        }

        public void ShowNotSupportedMessage()
            => InfoBarMessage(Translator.Info_UnsupportedFunctionalityTitle,
                              Translator.Info_UnsupportedFunctionalityDescription,
                              InfoBarMessageType.Error);

        public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon = WinoCustomMessageDialogIcon.Information)
            => ShowWinoCustomMessageDialogAsync(title, message, Translator.Buttons_Close, icon);

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

        public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
            => ShowWinoCustomMessageDialogAsync(title, question, confirmationButtonTitle, WinoCustomMessageDialogIcon.Question, Translator.Buttons_Cancel, string.Empty);

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

        public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
        {
            var inputDialog = new TextInputDialog()
            {
                CurrentInput = currentInput,
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme(),
                Title = dialogTitle
            };

            inputDialog.SetDescription(dialogDescription);
            inputDialog.SetPrimaryButtonText(primaryButtonText);

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

        public async Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
        {
            var createAccountAliasDialog = new CreateAccountAliasDialog()
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(createAccountAliasDialog);

            return createAccountAliasDialog;
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

                    InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);

                    // Update account menu item and force re-synchronization.
                    WeakReferenceMessenger.Default.Send(new AccountUpdatedMessage(updatedAccount));

                    var options = new SynchronizationOptions()
                    {
                        AccountId = updatedAccount.Id,
                        Type = SynchronizationType.Full,
                    };

                    WeakReferenceMessenger.Default.Send(new NewSynchronizationRequested(options, SynchronizationSource.Client));
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

        public Task<bool> ShowHardDeleteConfirmationAsync()
            => ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage,
                                               Translator.DialogMessage_HardDeleteConfirmationTitle,
                                               Translator.Buttons_Yes,
                                               WinoCustomMessageDialogIcon.Warning,
                                               Translator.Buttons_No);

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

        public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
        {
            var accountReorderDialog = new AccountReorderDialog(availableAccounts)
            {
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(accountReorderDialog);
        }

        public async Task<bool> ShowWinoCustomMessageDialogAsync(string title,
                                                                 string description,
                                                                 string approveButtonText,
                                                                 WinoCustomMessageDialogIcon? icon,
                                                                 string cancelButtonText = "",
                                                                 string dontAskAgainConfigurationKey = "")

        {
            // This config key has been marked as don't ask again already.
            // Return immidiate result without presenting the dialog.

            bool isDontAskEnabled = !string.IsNullOrEmpty(dontAskAgainConfigurationKey);

            if (isDontAskEnabled && _configurationService.Get(dontAskAgainConfigurationKey, false)) return false;

            var informationContainer = new CustomMessageDialogInformationContainer(title, description, icon.Value, isDontAskEnabled);

            var dialog = new ContentDialog
            {
                Style = (Style)App.Current.Resources["WinoDialogStyle"],
                RequestedTheme = _themeService.RootTheme.ToWindowsElementTheme(),
                DefaultButton = ContentDialogButton.Primary,
                PrimaryButtonText = approveButtonText,
                ContentTemplate = (DataTemplate)App.Current.Resources["CustomWinoContentDialogContentTemplate"],
                Content = informationContainer
            };

            if (!string.IsNullOrEmpty(cancelButtonText))
            {
                dialog.SecondaryButtonText = cancelButtonText;
            }

            var dialogResult = await HandleDialogPresentationAsync(dialog);

            // Mark this key to not ask again if user checked the checkbox.
            if (informationContainer.IsDontAskChecked)
            {
                _configurationService.Set(dontAskAgainConfigurationKey, true);
            }

            return dialogResult == ContentDialogResult.Primary;
        }

        private object GetDontAskDialogContentWithIcon(string description, WinoCustomMessageDialogIcon icon, string dontAskKey = "")
        {
            var iconPresenter = new ContentPresenter()
            {
                ContentTemplate = (DataTemplate)App.Current.Resources[$"WinoCustomMessageDialog{icon}IconTemplate"],
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var viewBox = new Viewbox
            {
                Child = iconPresenter,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var descriptionTextBlock = new TextBlock()
            {
                Text = description,
                TextWrapping = TextWrapping.WrapWholeWords,
                VerticalAlignment = VerticalAlignment.Center
            };

            var containerGrid = new Grid()
            {
                Children =
                {
                    viewBox,
                    descriptionTextBlock
                },
                RowSpacing = 6,
                ColumnSpacing = 12
            };

            containerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(32, GridUnitType.Pixel) });
            containerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(descriptionTextBlock, 1);

            // Add don't ask again checkbox if key is provided.
            if (!string.IsNullOrEmpty(dontAskKey))
            {
                var dontAskCheckBox = new CheckBox() { Content = Translator.Dialog_DontAskAgain };

                dontAskCheckBox.Checked += (c, r) => { _configurationService.Set(dontAskKey, dontAskCheckBox.IsChecked.GetValueOrDefault()); };

                containerGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Auto) });
                containerGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Auto) });

                Grid.SetRow(dontAskCheckBox, 1);
                Grid.SetColumnSpan(dontAskCheckBox, 2);
                containerGrid.Children.Add(dontAskCheckBox);
            }

            return containerGrid;
        }
    }
}

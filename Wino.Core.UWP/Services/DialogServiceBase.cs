using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Common;
using Wino.Core.UWP.Dialogs;
using Wino.Core.UWP.Extensions;
using Wino.Dialogs;
using Wino.Messaging.Client.Shell;

namespace Wino.Core.UWP.Services
{
    public class DialogServiceBase : IDialogServiceBase
    {
        private SemaphoreSlim _presentationSemaphore = new SemaphoreSlim(1);

        protected IThemeService ThemeService { get; }
        protected IConfigurationService ConfigurationService { get; }

        protected IApplicationResourceManager<ResourceDictionary> ApplicationResourceManager { get; }

        public DialogServiceBase(IThemeService themeService, IConfigurationService configurationService, IApplicationResourceManager<ResourceDictionary> applicationResourceManager)
        {
            ThemeService = themeService;
            ConfigurationService = configurationService;
            ApplicationResourceManager = applicationResourceManager;
        }

        public async Task<MailAccount> ShowEditAccountDialogAsync(MailAccount account)
        {
            var editAccountDialog = new AccountEditDialog(account)
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(editAccountDialog);

            return editAccountDialog.IsSaved ? editAccountDialog.Account : null;
        }

        public async Task<string> PickFilePathAsync(string saveFileName)
        {
            var picker = new FolderPicker()
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };

            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return string.Empty;

            StorageApplicationPermissions.FutureAccessList.Add(folder);

            return folder.Path;

            //var picker = new FileSavePicker
            //{
            //    SuggestedStartLocation = PickerLocationId.Desktop,
            //    SuggestedFileName = saveFileName
            //};

            //picker.FileTypeChoices.Add(Translator.FilteringOption_All, [".*"]);

            //var file = await picker.PickSaveFileAsync();
            //if (file == null) return string.Empty;

            //StorageApplicationPermissions.FutureAccessList.Add(file);

            //return file.Path;
        }

        public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
        {
            var returnList = new List<SharedFile>();
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.Desktop
            };

            foreach (var filter in typeFilters)
            {
                picker.FileTypeFilter.Add(filter.ToString());
            }

            var files = await picker.PickMultipleFilesAsync();
            if (files == null) return returnList;

            foreach (var file in files)
            {
                StorageApplicationPermissions.FutureAccessList.Add(file);

                var sharedFile = await file.ToSharedFileAsync();
                returnList.Add(sharedFile);
            }

            return returnList;
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

            StorageApplicationPermissions.FutureAccessList.Add(file);

            return file;
        }

        public virtual IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult)
        {
            return new AccountCreationDialog
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };
        }

        public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
        {
            var file = await PickFileAsync(typeFilters);

            if (file == null) return [];

            return await file.ToByteArrayAsync();
        }

        public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon = WinoCustomMessageDialogIcon.Information)
          => ShowWinoCustomMessageDialogAsync(title, message, Translator.Buttons_Close, icon);

        public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
            => ShowWinoCustomMessageDialogAsync(title, question, confirmationButtonTitle, WinoCustomMessageDialogIcon.Question, Translator.Buttons_Cancel, string.Empty);

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

            if (isDontAskEnabled && ConfigurationService.Get(dontAskAgainConfigurationKey, false)) return false;

            var informationContainer = new CustomMessageDialogInformationContainer(title, description, icon.Value, isDontAskEnabled);

            var dialog = new ContentDialog
            {
                Style = ApplicationResourceManager.GetResource<Style>("WinoDialogStyle"),
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme(),
                DefaultButton = ContentDialogButton.Primary,
                PrimaryButtonText = approveButtonText,
                ContentTemplate = ApplicationResourceManager.GetResource<DataTemplate>("CustomWinoContentDialogContentTemplate"),
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
                ConfigurationService.Set(dontAskAgainConfigurationKey, true);
            }

            return dialogResult == ContentDialogResult.Primary;
        }

        /// <summary>
        /// Waits for PopupRoot to be available before presenting the dialog and returns the result after presentation.
        /// </summary>
        /// <param name="dialog">Dialog to present and wait for closing.</param>
        /// <returns>Dialog result from WinRT.</returns>
        public async Task<ContentDialogResult> HandleDialogPresentationAsync(ContentDialog dialog)
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


        public void InfoBarMessage(string title, string message, InfoBarMessageType messageType)
            => WeakReferenceMessenger.Default.Send(new InfoBarMessageRequested(messageType, title, message));

        public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
            => WeakReferenceMessenger.Default.Send(new InfoBarMessageRequested(messageType, title, message, actionButtonText, action));

        public void ShowNotSupportedMessage()
            => InfoBarMessage(Translator.Info_UnsupportedFunctionalityTitle,
                      Translator.Info_UnsupportedFunctionalityDescription,
                      InfoBarMessageType.Error);

        public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
        {
            var inputDialog = new TextInputDialog()
            {
                CurrentInput = currentInput,
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme(),
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

        public async Task<bool> ShowCustomThemeBuilderDialogAsync()
        {
            var themeBuilderDialog = new CustomThemeBuilderDialog()
            {
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            var dialogResult = await HandleDialogPresentationAsync(themeBuilderDialog);

            return dialogResult == ContentDialogResult.Primary;
        }

        public async Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        {
            var dialog = new NewAccountDialog
            {
                Providers = availableProviders,
                RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
            };

            await HandleDialogPresentationAsync(dialog);

            return dialog.Result;
        }
    }
}

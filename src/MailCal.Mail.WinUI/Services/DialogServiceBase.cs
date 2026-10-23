using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.Common;
using MailCal.Dialogs;
using MailCal.Mail.WinUI.Dialogs;
using MailCal.Mail.WinUI.Extensions;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Messaging.Client.Shell;
using WinRT;
using WinRT.Interop;

namespace MailCal.Mail.WinUI.Services;

public class DialogServiceBase : IDialogServiceBase
{
    private SemaphoreSlim _presentationSemaphore = new SemaphoreSlim(1);

    protected INewThemeService ThemeService { get; }
    protected IConfigurationService ConfigurationService { get; }
    protected IApplicationResourceManager<ResourceDictionary> ApplicationResourceManager { get; }

    public DialogServiceBase(INewThemeService themeService, IConfigurationService configurationService, IApplicationResourceManager<ResourceDictionary> applicationResourceManager)
    {
        ThemeService = themeService;
        ConfigurationService = configurationService;
        ApplicationResourceManager = applicationResourceManager;
    }

    /// <summary>
    /// The XamlRoot a new dialog should open in: the window the user is working in, so a dialog
    /// raised from a secondary window opens there instead of behind it on the main window. A
    /// hidden window cannot present a dialog, so that case falls back to the main window.
    /// </summary>
    protected XamlRoot? GetXamlRoot()
    {
        var activeWindow = MailCalApplication.Current.Services.GetService<IMailCalWindowManager>()?.ActiveWindow;

        if (activeWindow is { Visible: true, Content.XamlRoot: { } activeXamlRoot })
            return activeXamlRoot;

        return MailCalApplication.MainWindow?.Content?.XamlRoot;
    }

    public async Task<string> PickFilePathAsync(string saveFileName)
    {
        var picker = new FolderPicker()
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };

        picker.FileTypeFilter.Add("*");

        var mainWindow = MailCalApplication.MainWindow;
        if (mainWindow == null) return string.Empty;

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return string.Empty;

        StorageApplicationPermissions.FutureAccessList.Add(folder);

        return $"{Path.Combine(folder.Path, saveFileName)}";

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

        var mainWindow = MailCalApplication.MainWindow;
        if (mainWindow == null) return returnList;

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);

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

    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
    {
        var returnList = new List<PickedFileMetadata>();
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.Desktop
        };

        foreach (var filter in typeFilters)
        {
            picker.FileTypeFilter.Add(filter.ToString());
        }

        var mainWindow = MailCalApplication.MainWindow;
        if (mainWindow == null) return returnList;

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files == null) return returnList;

        foreach (var file in files)
        {
            StorageApplicationPermissions.FutureAccessList.Add(file);

            var basicProperties = await file.GetBasicPropertiesAsync();
            returnList.Add(new PickedFileMetadata(file.Path, (long)basicProperties.Size));
        }

        return returnList;
    }

    private async Task<StorageFile?> PickFileAsync(params object[] typeFilters)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail
        };

        foreach (var filter in typeFilters)
        {
            picker.FileTypeFilter.Add(filter.ToString());
        }

        var mainWindow = MailCalApplication.MainWindow;
        if (mainWindow == null) return null;

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSingleFileAsync();

        if (file == null) return null;

        StorageApplicationPermissions.FutureAccessList.Add(file);

        return file;
    }

    public virtual IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult)
    {
        return new AccountCreationDialog(dialog => StartSessionDialog(dialog))
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

    public Task ShowMessageAsync(string message, string title, MailCalCustomMessageDialogIcon icon = MailCalCustomMessageDialogIcon.Information)
      => ShowMailCalCustomMessageDialogAsync(title, message, Translator.Buttons_Close, icon);

    public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        => ShowMailCalCustomMessageDialogAsync(title, question, confirmationButtonTitle, MailCalCustomMessageDialogIcon.Question, Translator.Buttons_Cancel, string.Empty);

    public async Task<bool> ShowMailCalCustomMessageDialogAsync(string title,
                                                             string description,
                                                             string approveButtonText,
                                                             MailCalCustomMessageDialogIcon? icon,
                                                             string cancelButtonText = "",
                                                             string dontAskAgainConfigurationKey = "")

    {
        // This config key has been marked as don't ask again already.
        // Return immidiate result without presenting the dialog.

        bool isDontAskEnabled = !string.IsNullOrEmpty(dontAskAgainConfigurationKey);

        if (isDontAskEnabled && ConfigurationService.Get(dontAskAgainConfigurationKey, false)) return false;

        var informationContainer = new CustomMessageDialogInformationContainer(title, description, icon ?? MailCalCustomMessageDialogIcon.Information, isDontAskEnabled);

        var dialog = new MailCalMessageDialog(informationContainer, approveButtonText, cancelButtonText)
        {
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

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
            // A dialog keeps the XamlRoot it was first given. Assigning a different one throws
            // "This element is already associated with a XamlRoot", so only an unrooted dialog
            // is rooted here.
            dialog.XamlRoot ??= GetXamlRoot();

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
    /// Opens a progress dialog that stays up while other work runs and is closed by its owner
    /// (account creation, browser sign-in). It cannot wait in the presentation queue, because the
    /// work it covers may itself need a queued dialog. WinUI allows one open ContentDialog per
    /// XamlRoot, so it opens only when no other dialog owns the window.
    /// </summary>
    /// <returns>
    /// Null when the dialog was not started. Otherwise a task that completes with true once the
    /// dialog has opened, or false when presentation failed.
    /// </returns>
    protected Task<bool>? StartSessionDialog(ContentDialog dialog, XamlRoot? xamlRoot = null)
    {
        dialog.XamlRoot ??= xamlRoot ?? GetXamlRoot();

        if (dialog.XamlRoot is not { } root || IsContentDialogOpen(root))
        {
            Log.Warning("{Dialog} was not shown because another dialog owns the window.", dialog.GetType().Name);
            return null;
        }

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        dialog.Opened += OnOpened;
        _ = PresentAsync();

        return opened.Task;

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            dialog.Opened -= OnOpened;
            opened.TrySetResult(true);
        }

        async Task PresentAsync()
        {
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Presenting {Dialog} failed.", dialog.GetType().Name);
            }
            finally
            {
                dialog.Opened -= OnOpened;
                opened.TrySetResult(false);
            }
        }
    }

    [DynamicWindowsRuntimeCast(typeof(ContentDialog))]
    private static bool IsContentDialogOpen(XamlRoot xamlRoot)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is ContentDialog)
                return true;
        }

        return false;
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

        var mainWindow = MailCalApplication.MainWindow;
        if (mainWindow == null) return string.Empty;

        nint windowHandle = WindowNative.GetWindowHandle(mainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);

        var pickedFolder = await picker.PickSingleFolderAsync();

        if (pickedFolder != null)
        {
            Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.AddOrReplace("FolderPickerToken", pickedFolder);

            return pickedFolder.Path;
        }

        return string.Empty;
    }

    public async Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
    {
        var dialog = new NewAccountDialog
        {
            Providers = availableProviders,
            RequestedTheme = ThemeService.RootTheme.ToWindowsElementTheme()
        };

        await HandleDialogPresentationAsync(dialog);

        return dialog.Result ?? null!;
    }

}

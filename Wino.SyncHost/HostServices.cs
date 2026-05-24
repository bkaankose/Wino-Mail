using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.SyncHost;

internal sealed class HostDispatcher : IDispatcher
{
    public Task ExecuteOnUIThread(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

internal sealed class HostNativeAppService : INativeAppService
{
    private string? _mimeMessagesFolder;
    private string? _editorBundlePath;

    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = static () => IntPtr.Zero;

    public string GetWebAuthenticationBrokerUri() => string.Empty;

    public async Task<string> GetMimeMessageStoragePath()
    {
        if (!string.IsNullOrWhiteSpace(_mimeMessagesFolder))
            return _mimeMessagesFolder;

        var mimeFolder = await ApplicationData.Current.LocalFolder
            .CreateFolderAsync("Mime", CreationCollisionOption.OpenIfExists)
            .AsTask()
            .ConfigureAwait(false);

        _mimeMessagesFolder = mimeFolder.Path;
        return _mimeMessagesFolder;
    }

    public Task<string> GetEditorBundlePathAsync()
    {
        _editorBundlePath ??= Path.Combine(AppContext.BaseDirectory, "JS", "editor.html");
        return Task.FromResult(_editorBundlePath);
    }

    public async Task LaunchFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath).AsTask().ConfigureAwait(false);
        await Launcher.LaunchFileAsync(file).AsTask().ConfigureAwait(false);
    }

    public Task<bool> LaunchUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri).AsTask();

    public bool IsAppRunning() => true;

    public string GetFullAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    public Task PinAppToTaskbarAsync() => Task.CompletedTask;

    public string GetCalendarAttachmentsFolderPath()
    {
        var attachmentsFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
        Directory.CreateDirectory(attachmentsFolder);
        return attachmentsFolder;
    }
}

internal sealed class HeadlessKeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed() => false;
    public bool IsShiftKeyPressed() => false;
}

internal sealed class HeadlessStoreManagementService : IStoreManagementService
{
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromResult(StorePurchaseResult.NotPurchased);
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
}

internal sealed class HeadlessMailDialogService : IMailDialogService
{
    public Task<string> PickWindowsFolderAsync() => Task.FromResult(string.Empty);
    public Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle) => Task.FromResult(false);
    public Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon) => Task.CompletedTask;
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) { }
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action) { }
    public void ShowNotSupportedMessage() { }
    public Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText) => Task.FromResult(currentInput);
    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "") => Task.FromResult(false);
    public Task<bool> ShowCustomThemeBuilderDialogAsync() => Task.FromResult(false);
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders) => Task.FromResult<AccountCreationDialogResult>(null!);
    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult) => throw new NotSupportedException();
    public Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters) => Task.FromResult(new List<SharedFile>());
    public Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters) => Task.FromResult(new List<PickedFileMetadata>());
    public Task<string> PickFilePathAsync(string saveFileName) => Task.FromResult(string.Empty);
    public void ShowReadOnlyCalendarMessage() { }
    public Task<bool> ShowHardDeleteConfirmationAsync() => Task.FromResult(false);
    public Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null) => Task.FromResult(ThreeButtonDialogResult.Cancel);
    public Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService) => Task.CompletedTask;
    public Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders) => Task.FromResult(availableFolders.FirstOrDefault()!);
    public Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts) => Task.FromResult(availableAccounts.FirstOrDefault()!);
    public Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups) => Task.FromResult<AccountCalendarPickingResult>(null!);
    public Task ShowAccountReorderDialogAsync(System.Collections.ObjectModel.ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => Task.CompletedTask;
    public Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService) => Task.FromResult<IMailItemFolder>(null!);
    public Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null) => Task.FromResult<AccountSignature>(null!);
    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync() => Task.FromResult<ICreateAccountAliasDialog>(null!);
    public Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null!) => Task.FromResult<MailCategoryDialogResult>(null!);
    public Task ShowMessageSourceDialogAsync(string messageSource) => Task.CompletedTask;
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null!) => Task.FromResult<KeyboardShortcutDialogResult>(null!);
    public Task<AccountContact?> ShowEditContactDialogAsync(AccountContact? contact = null) => Task.FromResult<AccountContact?>(null);
    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync() => Task.FromResult<WinoAccount?>(null);
    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync() => Task.FromResult<WinoAccount?>(null);
    public Task<WinoAccountSyncExportResult?> ShowWinoAccountExportDialogAsync() => Task.FromResult<WinoAccountSyncExportResult?>(null);
}

using AppKit;
using Foundation;
using Wino.Presentation.AppKit;
using System.Collections.ObjectModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Window-owned native sheets; specialized future screens fail explicitly until ported.</summary>
public sealed class AppKitDialogService(IDispatcher dispatcher, Func<NSWindow?> owner, Action<Exception> error) : IMailDialogService
{
    private readonly SemaphoreSlim _presentations = new(1, 1);
    private async Task<T> PresentAsync<T>(Func<NSWindow, Task<T>> present)
    {
        await _presentations.WaitAsync();
        try
        {
            Task<T>? operation = null;
            await dispatcher.ExecuteOnUIThread(() => operation = present(owner() ?? throw new InvalidOperationException("No owning window is available.")));
            return await operation!;
        }
        finally { _presentations.Release(); }
    }
    private Task<int> AlertAsync(string title, string description, string[] buttons, NSView? accessory = null) => PresentAsync(window =>
    {
        var alert = new NSAlert { MessageText = title, InformativeText = description, AlertStyle = NSAlertStyle.Informational, AccessoryView = accessory };
        foreach (string button in buttons) alert.AddButton(button);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ =>
        {
            window.EndSheet(alert.Window, NSModalResponse.Cancel);
        }, window);
        alert.BeginSheet(window, response =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); }
            completion.TrySetResult((int)response - 1000);
            alert.Dispose();
        });
        return completion.Task;
    });
    public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle) =>
        await AlertAsync(title, question, new[] { confirmationButtonTitle, Translator.Buttons_Cancel }) == 0;
    public async Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon) =>
        await AlertAsync(title, message, new[] { "OK" });
    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null) =>
        await AlertAsync(title, description, new[] { primaryButtonText, secondaryButtonText, cancelButtonText }) switch { 0 => ThreeButtonDialogResult.Primary, 1 => ThreeButtonDialogResult.Secondary, _ => ThreeButtonDialogResult.Cancel };
    public Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
    {
        if (!string.IsNullOrEmpty(dontAskAgainConfigurationKey)) return Future<bool>();
        return ConfirmCustomAsync(title, description, approveButtonText, cancelButtonText);
    }
    private async Task<bool> ConfirmCustomAsync(string title, string description, string approve, string cancel) =>
        await AlertAsync(title, description, new[] { approve, string.IsNullOrEmpty(cancel) ? Translator.Buttons_Cancel : cancel }) == 0;
    public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
    {
        NSTextField? field = null;
        await dispatcher.ExecuteOnUIThread(() => field = new NSTextField(new CoreGraphics.CGRect(0, 0, 360, 28)) { StringValue = currentInput });
        try
        {
            if (await AlertAsync(dialogTitle, dialogDescription, new[] { primaryButtonText, Translator.Buttons_Cancel }, field) != 0) return null!;
            string? input = null;
            await dispatcher.ExecuteOnUIThread(() => input = field!.StringValue);
            return input!;
        }
        finally { await dispatcher.ExecuteOnUIThread(() => field?.Dispose()); }
    }
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) => ShowNotice(title, message);
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action) => ShowActionNotice(title, message, actionButtonText, action);
    private async void ShowNotice(string title, string message)
    {
        try { await ShowMessageAsync(message, title, WinoCustomMessageDialogIcon.Information); }
        catch (Exception exception) { error(exception); }
    }
    private async void ShowActionNotice(string title, string message, string button, Action action)
    {
        try { if (await ShowConfirmationDialogAsync(message, title, button)) action(); }
        catch (Exception exception) { error(exception); }
    }
    public void ShowNotSupportedMessage() => ShowNotice("Wino Mail", "This feature is not available in the macOS foundation yet.");
    public void ShowReadOnlyCalendarMessage() => ShowNotSupportedMessage();
    public Task<bool> ShowHardDeleteConfirmationAsync() => ShowConfirmationDialogAsync("Permanently delete the selected messages?", "Delete messages", "Delete");
    private Task<string[]> PickAsync(bool directory, bool multiple, params object[] typeFilters) => PresentAsync(window =>
    {
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = directory; panel.CanChooseFiles = !directory; panel.AllowsMultipleSelection = multiple;
        var extensions = typeFilters.OfType<string>().Select(value => value.TrimStart('.')).Where(value => value.Length > 0 && value != "*").ToArray();
        if (extensions.Length > 0) panel.AllowedFileTypes = extensions;
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => panel.Cancel(null), window);
        panel.BeginSheet(window, response =>
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose();
            completion.TrySetResult((long)response == 1 ? panel.Urls.Select(url => url.Path!).ToArray() : Array.Empty<string>());
            panel.Dispose();
        });
        return completion.Task;
    });
    public async Task<string> PickWindowsFolderAsync() => (await PickAsync(true, false)).FirstOrDefault()!;
    public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
    {
        string? path = (await PickAsync(false, false, typeFilters)).FirstOrDefault();
        return path is null ? null! : await File.ReadAllBytesAsync(path);
    }
    public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
    {
        var result = new List<SharedFile>();
        foreach (string path in await PickAsync(false, true, typeFilters)) result.Add(new(path, await File.ReadAllBytesAsync(path)));
        return result;
    }
    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters) =>
        (await PickAsync(false, true, typeFilters)).Select(path => new PickedFileMetadata(path, new FileInfo(path).Length)).ToList();
    public Task<string> PickFilePathAsync(string saveFileName) => PresentAsync(window =>
    {
        var panel = NSSavePanel.SavePanel; panel.NameFieldStringValue = saveFileName;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => panel.Cancel(null), window);
        panel.BeginSheet(window, response => { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); completion.TrySetResult((long)response == 1 ? panel.Url?.Path! : null!); panel.Dispose(); });
        return completion.Task;
    });
    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult result) => new AccountProgress(dispatcher, owner, error);
    private static Task<T> Future<T>() => Task.FromException<T>(new NotSupportedException("This specialized dialog has not been ported to macOS."));
    private static Task Future() => Task.FromException(new NotSupportedException("This specialized dialog has not been ported to macOS."));
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders) => Future<AccountCreationDialogResult>();
    public Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService) => Future();
    public Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders) => Future<IMailItemFolder>();
    public Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts) => Future<MailAccount>();
    public Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups) => Future<AccountCalendarPickingResult>();
    public Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations) => Future<ContactCreateDestination?>();
    public Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount) => Future<ContactListCreationResult?>();
    public Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts) => Future<AccountTaskList?>();
    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => Future();
    public Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService) => Future<IMailItemFolder>();
    public Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null) => Future<AccountSignature>();
    public Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync() => Future<ICreateAccountAliasDialog>();
    public Task<MailCategoryDialogResult> ShowEditMailCategoryDialogAsync(MailCategory category = null!) => Future<MailCategoryDialogResult>();
    public Task ShowMessageSourceDialogAsync(string messageSource) => ShowMessageAsync(messageSource, "Message source", WinoCustomMessageDialogIcon.Information);
    public Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog) => ShowMessageAsync(errorMessage, "Connection failed", WinoCustomMessageDialogIcon.Information);
    public Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData) => Task.FromResult(false);
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null!) => Future<KeyboardShortcutDialogResult>();
    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync() => Future<WinoAccount?>();
    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync() => Future<WinoAccount?>();
    public Task<bool> ShowWinoAccountExportDialogAsync() => ShowConfirmationDialogAsync("Export your backup to Wino Account?", "Backup", "Export");
    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request) => Future<string?>();
    public Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync() => Future<UnlimitedAccountsPurchaseChannel?>();
    // Account work never owns the modal gate: browser authentication must remain presentable.
    private sealed class AccountProgress(IDispatcher dispatcher, Func<NSWindow?> owner, Action<Exception> error) : IAccountCreationDialog
    {
        private NSWindow? _window;
        private CancellationTokenSource? _cancellation;
        public AccountCreationDialogState State { get; set; }
        public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource)
        {
            _cancellation = cancellationTokenSource;
            return dispatcher.ExecuteOnUIThread(() =>
            {
                _window = new NSWindow(new CoreGraphics.CGRect(0, 0, 420, 160), NSWindowStyle.Titled, NSBackingStore.Buffered, false) { Title = "Adding account", ReleasedWhenClosed = false };
                var label = new NSTextField { StringValue = "Complete sign-in in your browser. Wino Mail will finish preparing the account.", Editable = false, Bordered = false, DrawsBackground = false };
                var cancel = new NSButton { Title = "Cancel" };
                cancel.Activated += (_, _) => cancellationTokenSource.Cancel();
                var content = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, label, cancel);
                Layout.Fill(content, _window.ContentView!, 20);
                _window.Center(); _window.MakeKeyAndOrderFront(null);
            });
        }
        public async void Complete(bool cancel)
        {
            try { if (cancel) _cancellation?.Cancel(); await dispatcher.ExecuteOnUIThread(() => { _window?.Close(); _window?.Dispose(); _window = null; }); }
            catch (Exception exception) { error(exception); }
        }
    }
}

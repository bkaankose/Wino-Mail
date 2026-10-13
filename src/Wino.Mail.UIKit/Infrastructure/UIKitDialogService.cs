using System.Collections.ObjectModel;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using UniformTypeIdentifiers;
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
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// UIKit dialogs (the Mac AppKitDialogService): UIAlertController for prompts and text input,
/// UIDocumentPickerViewController for picking and exporting files, navigation sheets for pickers (folders, accounts,
/// calendars, contact destinations, task lists), and an inline banner for info-bar messages. Forms, the Wino Account
/// sheets, the signature editor, account order and keyboard shortcuts live in the partial files beside this one.
/// The shared interface declares several results non-nullable although every platform answers a cancelled dialog
/// with null; this service keeps that contract.
/// </summary>
public sealed partial class UIKitDialogService(IDispatcher dispatcher, IUIKitShellHost host, IServiceProvider services) : IMailDialogService
{
    private readonly SemaphoreSlim _presentations = new(1, 1);

    #region Presentation

    /// <summary>Runs one presentation at a time on the top-most controller.</summary>
    private async Task<T> PresentAsync<T>(Func<UIViewController, Task<T>> present, T unavailable)
    {
        await _presentations.WaitAsync();
        try
        {
            Task<T> operation = Task.FromResult(unavailable);
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (host.TopViewController is { } presenter) operation = present(presenter);
                else Serilog.Log.Warning("A dialog was requested while nothing is on screen.");
            });
            return await operation;
        }
        finally { _presentations.Release(); }
    }

    /// <summary>An alert; returns the index of the chosen button, or -1 for the cancel button.</summary>
    private Task<int> AlertAsync(string? title, string? message, IReadOnlyList<string> buttons, string? cancel = null,
        int destructiveIndex = -1, Action<UIAlertController>? configure = null, Action<UIAlertController>? completed = null)
        => PresentAsync(presenter =>
        {
            var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            for (int index = 0; index < buttons.Count; index++)
            {
                int chosen = index;
                var style = index == destructiveIndex ? UIAlertActionStyle.Destructive : UIAlertActionStyle.Default;
                alert.AddAction(UIAlertAction.Create(buttons[index], style, _ => { completed?.Invoke(alert); completion.TrySetResult(chosen); }));
            }
            if (cancel is not null)
                alert.AddAction(UIAlertAction.Create(cancel, UIAlertActionStyle.Cancel, _ => { completed?.Invoke(alert); completion.TrySetResult(-1); }));
            if (alert.Actions is { Length: > 0 } actions) alert.PreferredAction = actions[0];
            configure?.Invoke(alert);
            presenter.PresentViewController(alert, true, null);
            return completion.Task;
        }, -1);

    #endregion

    #region Prompts

    public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        => await AlertAsync(title, question, [confirmationButtonTitle], Translator.Buttons_Cancel) == 0;

    public async Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon)
        => await AlertAsync(title, message, [Translator.Buttons_OK]);

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null)
        => await AlertAsync(title, description, [primaryButtonText, secondaryButtonText], cancelButtonText) switch
        {
            0 => ThreeButtonDialogResult.Primary,
            1 => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel
        };

    /// <summary>
    /// Same contract as Windows: a key already marked "don't ask again" answers false without showing. iOS alerts have
    /// no suppression checkbox, so a "Don't ask again" button records the choice and answers false.
    /// </summary>
    public async Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
    {
        var configuration = services.GetService<IConfigurationService>();
        var dontAskKey = string.IsNullOrEmpty(dontAskAgainConfigurationKey) || configuration is null ? null : dontAskAgainConfigurationKey;
        if (dontAskKey is not null && configuration is not null && configuration.Get(dontAskKey, false)) return false;

        var buttons = new List<string> { approveButtonText };
        if (dontAskKey is not null) buttons.Add(Translator.Dialog_DontAskAgain);
        var destructive = icon is WinoCustomMessageDialogIcon.Warning or WinoCustomMessageDialogIcon.Error ? 0 : -1;
        var result = await AlertAsync(title, description, buttons, string.IsNullOrEmpty(cancelButtonText) ? null : cancelButtonText, destructive);
        if (result == 1 && dontAskKey is not null && configuration is not null) configuration.Set(dontAskKey, true);
        return result == 0;
    }

    public Task<bool> ShowHardDeleteConfirmationAsync()
        => ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage, Translator.DialogMessage_HardDeleteConfirmationTitle,
            Translator.Buttons_Yes, WinoCustomMessageDialogIcon.Warning, Translator.Buttons_No);

    /// <summary>Null when cancelled (Windows and Mac contract).</summary>
    public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
    {
        string? input = null;
        var choice = await AlertAsync(dialogTitle, dialogDescription, [primaryButtonText], Translator.Buttons_Cancel,
            configure: alert => alert.AddTextField(field => field.Text = currentInput ?? string.Empty),
            completed: alert => input = alert.TextFields?.FirstOrDefault()?.Text);
        return choice == 0 ? input ?? string.Empty : null;
    }

    public Task<bool> ShowWinoAccountExportDialogAsync() => ShowConfirmationDialogAsync(
        $"{Translator.WinoAccount_Management_ExportDialog_Confirmation}\n\n{Translator.WinoAccount_Management_ExportDialog_EncryptionNotice}\n\n{Translator.WinoAccount_Management_ExportDialog_AccountsDisclaimer} {Translator.WinoAccount_Management_ExportDialog_AccountsRelogin_Device}",
        Translator.WinoAccount_Management_ExportDialog_Title, Translator.WinoAccount_Management_LocalDataExportAction);

    /// <summary>Windows ImapValidationFailedDialog: the error, the protocol log, Close and Copy diagnostics.</summary>
    public async Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
    {
        var log = protocolLog ?? string.Empty;
        // An alert body does not scroll well; the full log is what Copy diagnostics puts on the pasteboard.
        var shown = log.Length > 1500 ? "…" + log[^1500..] : log;
        var message = string.IsNullOrWhiteSpace(shown) ? errorMessage : $"{errorMessage}\n\n{Translator.ImapValidationFailedDialog_ProtocolLog}\n{shown}";
        var choice = await AlertAsync(Translator.ImapValidationFailedDialog_Title, message, [Translator.Buttons_Close, Translator.ImapValidationFailedDialog_Copy]);
        if (choice != 1) return;
        await dispatcher.ExecuteOnUIThread(() => UIPasteboard.General.String = $"{errorMessage}{Environment.NewLine}{Environment.NewLine}{log}");
    }

    #endregion

    #region Info bar

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) => ShowBanner(title, message, messageType, null, null);

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
        => ShowBanner(title, message, messageType, actionButtonText, action);

    public void ShowNotSupportedMessage()
        => ShowBanner(Translator.Info_UnsupportedFunctionalityTitle, Translator.Info_UnsupportedFunctionalityDescription, InfoBarMessageType.Information, null, null);

    public void ShowReadOnlyCalendarMessage()
        => ShowBanner(Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message, InfoBarMessageType.Warning, null, null);

    /// <summary>Windows ShellInfoBar as a banner at the top of the window; it closes itself after a few seconds.</summary>
    private async void ShowBanner(string title, string message, InfoBarMessageType type, string? actionTitle, Action? action)
    {
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (host.Window is not { } window) return;
                var severity = type switch
                {
                    InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
                    InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
                    InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
                    _ => WinoInfoBarSeverity.Informational
                };
                var bar = new WinoInfoBar(severity, title, message) { IsClosable = true, AutoDismissInterval = TimeSpan.FromSeconds(actionTitle is null ? 5 : 10) };
                if (actionTitle is not null && action is not null)
                {
                    bar.ActionTitle = actionTitle;
                    bar.ActionInvoked += (_, _) => { action(); bar.RemoveFromSuperview(); };
                }
                bar.Closed += (_, _) => bar.RemoveFromSuperview();
                bar.TranslatesAutoresizingMaskIntoConstraints = false;
                window.AddSubview(bar);
                var guide = window.SafeAreaLayoutGuide;
                NSLayoutConstraint.ActivateConstraints(
                [
                    bar.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 8),
                    bar.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
                    bar.WidthAnchor.ConstraintLessThanOrEqualTo(560),
                    bar.LeadingAnchor.ConstraintGreaterThanOrEqualTo(guide.LeadingAnchor, 12),
                    bar.TrailingAnchor.ConstraintLessThanOrEqualTo(guide.TrailingAnchor, -12)
                ]);
                var fill = bar.WidthAnchor.ConstraintEqualTo(guide.WidthAnchor, 1, -24);
                fill.Priority = 750;
                fill.Active = true;
            });
        }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Could not show an info bar message."); }
    }

    #endregion

    #region Files

    /// <summary>Opens the document picker; returns the picked URLs (copies inside the app when <paramref name="asCopy"/>), or none.</summary>
    private Task<NSUrl[]> PickDocumentsAsync(UTType[] types, bool multiple, bool asCopy)
        => PresentAsync(presenter =>
        {
            var completion = new TaskCompletionSource<NSUrl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var picker = new UIDocumentPickerViewController(types, asCopy) { AllowsMultipleSelection = multiple };
            picker.DidPickDocumentAtUrls += (_, args) => completion.TrySetResult(args.Urls ?? []);
            picker.WasCancelled += (_, _) => completion.TrySetResult([]);
            presenter.PresentViewController(picker, true, null);
            return completion.Task;
        }, []);

    private static UTType[] ContentTypes(object[] typeFilters)
    {
        var types = typeFilters.OfType<string>()
            .Select(value => value.TrimStart('*').TrimStart('.'))
            .Where(value => value.Length > 0)
            .Select(extension => UTType.CreateFromExtension(extension))
            .OfType<UTType>()
            .ToArray();
        return types.Length > 0 ? types : [UTTypes.Item];
    }

    /// <summary>A folder the user picked; its security scope stays open for the caller. Empty when cancelled.</summary>
    public async Task<string> PickWindowsFolderAsync()
    {
        var urls = await PickDocumentsAsync([UTTypes.Folder], false, false);
        if (urls.FirstOrDefault() is not { } url) return string.Empty;
        url.StartAccessingSecurityScopedResource();
        return url.Path ?? string.Empty;
    }

    /// <summary>Like Windows, a cancelled pick is an empty array; shared callers test Length.</summary>
    public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
    {
        var urls = await PickDocumentsAsync(ContentTypes(typeFilters), false, true);
        return urls.FirstOrDefault()?.Path is { } path ? await File.ReadAllBytesAsync(path) : [];
    }

    public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
    {
        var result = new List<SharedFile>();
        foreach (var url in await PickDocumentsAsync(ContentTypes(typeFilters), true, true))
            if (url.Path is { } path) result.Add(new(path, await File.ReadAllBytesAsync(path)));
        return result;
    }

    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
        => (await PickDocumentsAsync(ContentTypes(typeFilters), true, true))
            .Select(url => url.Path).OfType<string>()
            .Select(path => new PickedFileMetadata(path, new FileInfo(path).Length)).ToList();

    /// <summary>
    /// iOS has no save panel that returns a writable path before the file exists. Saved files go to the app's Documents
    /// folder (shown in Files under On My iPhone › Wino Mail, Info.plist UIFileSharingEnabled), under a name that does
    /// not overwrite an existing file.
    /// </summary>
    public Task<string> PickFilePathAsync(string saveFileName)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(documents);
        var name = string.IsNullOrWhiteSpace(saveFileName) ? "Wino Mail" : Path.GetFileName(saveFileName);
        var path = Path.Combine(documents, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (int index = 2; File.Exists(path); index++) path = Path.Combine(documents, $"{stem} ({index}){extension}");
        return Task.FromResult(path);
    }

    #endregion

    #region Pickers

    public async Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
    {
        var picked = await PickAsync(Translator.MoveMailDialog_Title, null, availableFolders ?? [],
            folder => folder.ChildFolders ?? [],
            folder => folder.FolderName ?? string.Empty,
            folder => SpecialFolderGlyphs.GetGlyph(folder.SpecialFolderType) is var glyph && glyph != WinoIconGlyph.None ? glyph : WinoIconGlyph.Folder,
            folder => folder.IsMoveTarget,
            folder => folder.IsMoveTarget ? null : string.Format(Translator.MoveMailDialog_InvalidFolderMessage, folder.FolderName));
        return picked;
    }

    public async Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
    {
        var structure = await folderService.GetFolderStructureForAccountAsync(accountId, false);
        return await ShowMoveMailFolderDialogAsync(structure.Folders);
    }

    public async Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
    {
        var picked = await PickAsync(Translator.AccountPickerDialog_Title, null, availableAccounts ?? [],
            _ => [],
            account => string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} — {account.Address}",
            account => WinoAccountIconView.ProviderGlyph(Wino.Mail.ViewModels.Data.MailAccountIconInfoFactory.CreateProviderFallback(account.ProviderType, account.SpecialImapProvider).Provider, false),
            _ => true);
        return picked;
    }

    /// <summary>Windows SingleCalendarPickerDialog: calendars under their account; read-only calendars shown disabled.</summary>
    public async Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
    {
        var groups = (availableCalendarGroups ?? []).Select(group => new PickerSection<AccountCalendar>(
            string.IsNullOrWhiteSpace(group.Account?.Address) || group.Account?.Address == group.Account?.Name
                ? group.Account?.Name ?? string.Empty
                : $"{group.Account?.Name} ({group.Account?.Address})",
            group.Calendars ?? [])).ToList();
        var picked = await PickSectionsAsync(Translator.CalendarEventCompose_PickCalendarTitle, Translator.CalendarEventCompose_DefaultCalendarHint, groups,
            _ => [],
            calendar => calendar.IsReadOnly ? $"{calendar.Name}  ·  {Translator.CalendarPicker_ReadOnly}" : calendar.Name ?? string.Empty,
            _ => WinoIconGlyph.Calendar,
            calendar => !calendar.IsReadOnly,
            calendar => calendar.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
            calendar => WinoStyle.FromHexString(calendar.BackgroundColorHex));
        return new AccountCalendarPickingResult(picked, false);
    }

    public async Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
        => await PickAsync(Translator.PeopleSettings_DestinationPicker_Title, null, destinations ?? [],
            _ => [],
            destination => destination.IsReadOnly ? $"{destination.DisplayName}  ·  {Translator.CalendarPicker_ReadOnly}" : destination.DisplayName,
            _ => WinoIconGlyph.People,
            destination => !destination.IsReadOnly,
            destination => destination.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null);

    public async Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
    {
        var owners = (accounts ?? []).ToDictionary(account => account.Id);
        return await PickAsync(Translator.ToDoPage_Title, null, taskLists ?? [],
            _ => [],
            list => owners.TryGetValue(list.MailAccountId, out var owner) && !string.IsNullOrWhiteSpace(owner.Name) ? $"{list.Title} — {owner.Name}" : list.Title ?? string.Empty,
            _ => WinoIconGlyph.TaskList,
            _ => true);
    }

    private sealed record PickerSection<T>(string? Title, IReadOnlyList<T> Items);

    private Task<T?> PickAsync<T>(string title, string? description, IReadOnlyList<T> roots, Func<T, IEnumerable<T>> children,
        Func<T, string> text, Func<T, WinoIconGlyph> glyph, Func<T, bool> selectable, Func<T, string?>? invalid = null) where T : class
        => PickSectionsAsync(title, description, [new PickerSection<T>(null, roots)], children, text, glyph, selectable, invalid, null);

    /// <summary>
    /// A navigation sheet with a search field and the items as rows (children indented under their parents).
    /// Tapping a selectable row picks it; Cancel or swiping the sheet away picks nothing.
    /// </summary>
    private Task<T?> PickSectionsAsync<T>(string title, string? description, IReadOnlyList<PickerSection<T>> sections, Func<T, IEnumerable<T>> children,
        Func<T, string> text, Func<T, WinoIconGlyph> glyph, Func<T, bool> selectable, Func<T, string?>? invalid, Func<T, UIColor?>? tint) where T : class
        => PresentAsync(presenter =>
        {
            var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var picker = new PickerViewController<T>(description, sections, children, text, glyph, selectable, invalid, tint, item => completion.TrySetResult(item)) { Title = title };
            var sheet = new UINavigationController(picker) { ModalPresentationStyle = UIModalPresentationStyle.FormSheet };
            if (sheet.PresentationController is { } presentation) presentation.Delegate = picker.Dismissal;
            presenter.PresentViewController(sheet, true, null);
            return completion.Task;
        }, null);

    private sealed class PickerViewController<T>(string? description, IReadOnlyList<PickerSection<T>> sections, Func<T, IEnumerable<T>> children,
        Func<T, string> text, Func<T, WinoIconGlyph> glyph, Func<T, bool> selectable, Func<T, string?>? invalid, Func<T, UIColor?>? tint,
        Action<T?> finished) : UITableViewController(UITableViewStyle.InsetGrouped), IUISearchResultsUpdating where T : class
    {
        private const string CellId = "picker";
        private List<(string? Title, List<(T Item, int Depth)> Rows)> _visible = [];
        private bool _done;

        public UIAdaptivePresentationControllerDelegate Dismissal { get; } = new SheetDismissal();

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            ((SheetDismissal)Dismissal).Dismissed = () => Finish(null);
            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Cancel, (_, _) => Close(null));
            var search = new UISearchController((UIViewController?)null) { ObscuresBackgroundDuringPresentation = false };
            search.SearchResultsUpdater = this;
            search.SearchBar.Placeholder = Translator.SearchBarPlaceholder;
            NavigationItem.SearchController = search;
            NavigationItem.HidesSearchBarWhenScrolling = false;
            TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellId);
            Filter(null);
        }

        public void UpdateSearchResultsForSearchController(UISearchController searchController) => Filter(searchController.SearchBar.Text);

        private IEnumerable<(T Item, int Depth)> Flatten(IEnumerable<T> items, int depth)
            => items.SelectMany(item => new[] { (item, depth) }.Concat(Flatten(children(item), depth + 1)));

        private void Filter(string? query)
        {
            _visible = sections.Select(section =>
            {
                var rows = Flatten(section.Items, 0).ToList();
                if (!string.IsNullOrWhiteSpace(query))
                    rows = rows.Where(row => text(row.Item).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Select(row => (row.Item, 0)).ToList();
                return (section.Title, rows);
            }).Where(section => section.rows.Count > 0).ToList();
            TableView.ReloadData();
        }

        public override nint NumberOfSections(UITableView tableView) => _visible.Count;
        public override nint RowsInSection(UITableView tableView, nint section) => _visible[(int)section].Rows.Count;
        public override string? TitleForHeader(UITableView tableView, nint section) => _visible[(int)section].Title;
        public override string? TitleForFooter(UITableView tableView, nint section) => section == _visible.Count - 1 ? description : null;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var (item, depth) = _visible[indexPath.Section].Rows[indexPath.Row];
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            var content = UIListContentConfiguration.CellConfiguration;
            content.Text = text(item);
            content.SecondaryText = invalid?.Invoke(item);
            content.Image = WinoIcons.Image(glyph(item), 18, tint?.Invoke(item));
            content.TextProperties.Color = selectable(item) ? WinoStyle.PrimaryText : WinoStyle.TertiaryText;
            cell.ContentConfiguration = content;
            cell.IndentationLevel = depth;
            cell.IndentationWidth = 16;
            cell.SelectionStyle = selectable(item) ? UITableViewCellSelectionStyle.Default : UITableViewCellSelectionStyle.None;
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            var item = _visible[indexPath.Section].Rows[indexPath.Row].Item;
            tableView.DeselectRow(indexPath, true);
            if (selectable(item)) Close(item);
        }

        private void Close(T? item)
        {
            Finish(item);
            NavigationItem.SearchController?.DismissViewController(false, null);
            DismissViewController(true, null);
        }

        private void Finish(T? item)
        {
            if (_done) return;
            _done = true;
            finished(item);
        }
    }

    private sealed class SheetDismissal : UIAdaptivePresentationControllerDelegate
    {
        public Action? Dismissed { get; set; }
        public override void DidDismiss(UIPresentationController presentationController) => Dismissed?.Invoke();
    }

    // Presentation controllers keep their delegates weakly; the service keeps them until their sheet closes.
    private readonly HashSet<SheetDismissal> _dismissals = [];

    /// <summary>Windows MessageSourceDialog: the raw source in a read-only monospaced text view with Copy.</summary>
    public Task ShowMessageSourceDialogAsync(string messageSource) => PresentAsync(presenter =>
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new UIViewController { Title = Translator.MessageSourceDialog_Title };
        page.View = new UITextView
        {
            Editable = false,
            Text = messageSource ?? string.Empty,
            Font = UIFont.GetMonospacedSystemFont(12, UIFontWeight.Regular),
            BackgroundColor = UIColor.SystemBackground,
        };
        var sheet = new UINavigationController(page) { ModalPresentationStyle = UIModalPresentationStyle.PageSheet };
        var dismissal = new SheetDismissal();
        void Closed()
        {
            _dismissals.Remove(dismissal);
            completion.TrySetResult(true);
        }
        dismissal.Dismissed = Closed;
        _dismissals.Add(dismissal);
        page.NavigationItem.LeftBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => sheet.DismissViewController(true, Closed));
        page.NavigationItem.RightBarButtonItem = new UIBarButtonItem(Translator.Buttons_Copy, UIBarButtonItemStyle.Plain, (_, _) =>
        {
            UIPasteboard.General.String = messageSource ?? string.Empty;
            InfoBarMessage(Translator.MessageSourceDialog_Title, Translator.MessageSourceDialog_Copied, InfoBarMessageType.Success);
        });
        if (sheet.PresentationController is { } presentation) presentation.Delegate = dismissal;
        presenter.PresentViewController(sheet, true, null);
        return completion.Task;
    }, false);

    #endregion

    #region Not built for iOS (no shared caller)

    /// <summary>No shared code calls it: account creation goes through the onboarding provider page (as on the Mac).</summary>
    public async Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
    {
        Serilog.Log.Warning("ShowAccountProviderSelectionDialogAsync was called; iOS uses the onboarding provider page instead.");
        await Task.CompletedTask;
        return null;
    }

    #endregion

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult) => new AccountProgress(dispatcher, host);

    /// <summary>
    /// The "adding account" sheet while sign-in and setup run. Account work never owns the presentation gate, so the
    /// browser sign-in session can still present over it.
    /// </summary>
    private sealed class AccountProgress(IDispatcher dispatcher, IUIKitShellHost host) : IAccountCreationDialog
    {
        private UIViewController? _sheet;
        private CancellationTokenSource? _cancellation;
        public AccountCreationDialogState State { get; set; }

        public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource) => dispatcher.ExecuteOnUIThread(() =>
        {
            _cancellation = cancellationTokenSource;
            var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium);
            spinner.StartAnimating();
            var title = WinoStyle.Label(Translator.MacOS_AccountCreation_Title, WinoStyle.Heading);
            var label = WinoStyle.Label(Translator.MacOS_AccountCreation_BrowserMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
            label.TextAlignment = UITextAlignment.Center;
            var cancel = UIButton.FromType(UIButtonType.System);
            cancel.SetTitle(Translator.Buttons_Cancel, UIControlState.Normal);
            cancel.PrimaryActionTriggered += (_, _) => cancellationTokenSource.Cancel();
            var stack = WinoLayout.VStack(WinoStyle.Space4, spinner, title, label, cancel);
            stack.Alignment = UIStackViewAlignment.Center;
            var root = new UIView { BackgroundColor = UIColor.SystemBackground };
            root.AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints(
            [
                stack.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
                stack.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
                stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(root.LeadingAnchor, 24),
                stack.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -24)
            ]);
            var page = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.FormSheet, ModalInPresentation = true, View = root };
            if (page.SheetPresentationController is { } sheet)
                sheet.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent()];
            _sheet = page;
            host.TopViewController?.PresentViewController(page, true, null);
        });

        public async void Complete(bool cancel)
        {
            try
            {
                if (cancel) _cancellation?.Cancel();
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    _sheet?.DismissViewController(true, null);
                    _sheet = null;
                });
            }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Could not close the account progress sheet."); }
        }
    }
}

using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
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
using Wino.Core.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Native dialogs: NSAlert sheets for prompts, window sheets for pickers and small forms, attached
/// to the key window (main window or Settings window). Specialized editors that are not ported yet
/// say so with a message instead of failing silently (docs/macos-design-decisions.md, A13/A14).
/// </summary>
public sealed partial class AppKitDialogService(IDispatcher dispatcher, Func<NSWindow?> owner, Action<Exception> error, IServiceProvider? services = null) : IMailDialogService
{
    private readonly SemaphoreSlim _presentations = new(1, 1);

    private NSWindow ResolveOwner()
    {
        var key = NSApplication.SharedApplication.KeyWindow;
        if (key is { IsVisible: true, IsSheet: false } && key.AttachedSheet is null) return key;
        return owner() ?? NSApplication.SharedApplication.MainWindow ?? throw new InvalidOperationException("No owning window is available.");
    }

    private async Task<T> PresentAsync<T>(Func<NSWindow, Task<T>> present)
    {
        await _presentations.WaitAsync();
        try
        {
            Task<T>? operation = null;
            await dispatcher.ExecuteOnUIThread(() => operation = present(ResolveOwner()));
            return await operation!;
        }
        finally { _presentations.Release(); }
    }

    private Task<int> AlertAsync(string title, string description, string[] buttons, NSView? accessory = null,
        NSAlertStyle style = NSAlertStyle.Informational, Action<NSAlert>? configure = null, Action<NSAlert>? completed = null) => PresentAsync(window =>
    {
        var alert = new NSAlert { MessageText = title ?? string.Empty, InformativeText = description ?? string.Empty, AlertStyle = style, AccessoryView = accessory };
        foreach (string button in buttons) alert.AddButton(button);
        configure?.Invoke(alert);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => window.EndSheet(alert.Window, NSModalResponse.Cancel), window);
        alert.BeginSheet(window, response =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); }
            completed?.Invoke(alert);
            completion.TrySetResult(response == (nint)(long)NSModalResponse.Cancel ? -1 : (int)response - 1000);
            alert.Dispose();
        });
        return completion.Task;
    });

    private static NSAlertStyle StyleFor(WinoCustomMessageDialogIcon? icon) => icon switch
    {
        WinoCustomMessageDialogIcon.Warning or WinoCustomMessageDialogIcon.Error => NSAlertStyle.Warning,
        _ => NSAlertStyle.Informational
    };

    public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle) =>
        await AlertAsync(title, question, [confirmationButtonTitle, Translator.Buttons_Cancel]) == 0;

    public async Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon) =>
        await AlertAsync(title, message, [Translator.Buttons_OK], style: StyleFor(icon));

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null) =>
        await AlertAsync(title, description, [primaryButtonText, secondaryButtonText, cancelButtonText], style: StyleFor(icon)) switch
        {
            0 => ThreeButtonDialogResult.Primary,
            1 => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel
        };

    public async Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
    {
        var configuration = services?.GetService<IConfigurationService>();
        bool dontAsk = !string.IsNullOrEmpty(dontAskAgainConfigurationKey) && configuration is not null;
        // Same contract as Windows: a key already marked "don't ask again" answers without showing.
        if (dontAsk && configuration!.Get(dontAskAgainConfigurationKey, false)) return false;

        bool suppressed = false;
        var buttons = string.IsNullOrEmpty(cancelButtonText) ? new[] { approveButtonText } : new[] { approveButtonText, cancelButtonText };
        var result = await AlertAsync(title, description, buttons, style: StyleFor(icon),
            configure: alert =>
            {
                if (!dontAsk) return;
                alert.ShowsSuppressionButton = true;
                alert.SuppressionButton.Title = Translator.Dialog_DontAskAgain;
            },
            completed: alert => suppressed = dontAsk && alert.SuppressionButton?.State == NSCellStateValue.On);
        if (suppressed) configuration!.Set(dontAskAgainConfigurationKey, true);
        return result == 0;
    }

    public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
    {
        NSTextField? field = null;
        await dispatcher.ExecuteOnUIThread(() => field = new NSTextField(new CGRect(0, 0, 320, 24)) { StringValue = currentInput ?? string.Empty });
        try
        {
            if (await AlertAsync(dialogTitle, dialogDescription, [primaryButtonText, Translator.Buttons_Cancel], field,
                    configure: alert => alert.Window.InitialFirstResponder = field) != 0) return null!;
            string? input = null;
            await dispatcher.ExecuteOnUIThread(() => input = field!.StringValue);
            return input!;
        }
        finally { await dispatcher.ExecuteOnUIThread(() => field?.Dispose()); }
    }

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) => ShowNotice(title, message, messageType);
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action) => ShowActionNotice(title, message, actionButtonText, action);

    /// <summary>
    /// Info-bar messages show inline at the top of the issuing window (Windows ShellInfoBar), with an
    /// alert only when no titled window can host the bar.
    /// </summary>
    private async void ShowNotice(string title, string message, InfoBarMessageType type)
    {
        try
        {
            bool shown = false;
            await dispatcher.ExecuteOnUIThread(() => shown = InlineInfoBarPresenter.TryShow(InfoBarWindow(), title, message, type));
            if (!shown) await AlertAsync(title, message, [Translator.Buttons_OK], style: type is InfoBarMessageType.Error or InfoBarMessageType.Warning ? NSAlertStyle.Warning : NSAlertStyle.Informational);
        }
        catch (Exception exception) { error(exception); }
    }

    private async void ShowActionNotice(string title, string message, string button, Action action)
    {
        try
        {
            bool shown = false;
            await dispatcher.ExecuteOnUIThread(() => shown = InlineInfoBarPresenter.TryShow(InfoBarWindow(), title, message, InfoBarMessageType.Information, button, action));
            if (!shown && await ShowConfirmationDialogAsync(message, title, button)) action();
        }
        catch (Exception exception) { error(exception); }
    }

    /// <summary>The key window, or the window a sheet hangs from, else the main window.</summary>
    private NSWindow? InfoBarWindow()
    {
        var key = NSApplication.SharedApplication.KeyWindow;
        if (key is { IsSheet: true }) key = key.SheetParent;
        if (key is { IsVisible: true } && key.StyleMask.HasFlag(NSWindowStyle.Titled)) return key;
        return owner() ?? NSApplication.SharedApplication.MainWindow;
    }

    private static string NotYetOnMac => Translator.MacOS_FeatureNotAvailable;

    public void ShowNotSupportedMessage() => ShowNotice("Wino Mail", NotYetOnMac, InfoBarMessageType.Information);
    public void ShowReadOnlyCalendarMessage() => ShowNotice(Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message, InfoBarMessageType.Warning);

    public Task<bool> ShowHardDeleteConfirmationAsync()
        => ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage, Translator.DialogMessage_HardDeleteConfirmationTitle,
            Translator.Buttons_Yes, WinoCustomMessageDialogIcon.Warning, Translator.Buttons_No);

    #region File panels

    private Task<string[]> PickPathsAsync(bool directory, bool multiple, params object[] typeFilters) => PresentAsync(window =>
    {
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = directory; panel.CanChooseFiles = !directory; panel.AllowsMultipleSelection = multiple;
        var extensions = typeFilters.OfType<string>().Select(value => value.TrimStart('.')).Where(value => value.Length > 0 && value != "*").ToArray();
#pragma warning disable CA1422 // AllowedFileTypes keeps extension filters simple; UTType mapping is not needed here.
        if (extensions.Length > 0) panel.AllowedFileTypes = extensions;
#pragma warning restore CA1422
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

    public async Task<string> PickWindowsFolderAsync() => (await PickPathsAsync(true, false)).FirstOrDefault()!;

    public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
    {
        // Like Windows, a cancelled pick is an empty array; shared callers test Length, not null.
        string? path = (await PickPathsAsync(false, false, typeFilters)).FirstOrDefault();
        return path is null ? [] : await File.ReadAllBytesAsync(path);
    }

    public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
    {
        var result = new List<SharedFile>();
        foreach (string path in await PickPathsAsync(false, true, typeFilters)) result.Add(new(path, await File.ReadAllBytesAsync(path)));
        return result;
    }

    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters) =>
        (await PickPathsAsync(false, true, typeFilters)).Select(path => new PickedFileMetadata(path, new FileInfo(path).Length)).ToList();

    /// <summary>
    /// Returns the exact file URL the user confirmed. The sandbox grants write access to that path
    /// only, so callers must write there and not to another name in the same folder.
    /// </summary>
    public Task<string> PickFilePathAsync(string saveFileName) => PresentAsync(window =>
    {
        var panel = NSSavePanel.SavePanel; panel.NameFieldStringValue = saveFileName;
        panel.CanCreateDirectories = true;
        // Keep the suggested extension when the user renames the file, so the matching open panel
        // (for example the .winosnap filter on restore) still finds it. Other types stay allowed.
        var extension = Path.GetExtension(saveFileName).TrimStart('.');
        if (extension.Length > 0)
        {
#pragma warning disable CA1422 // AllowedFileTypes keeps extension filters simple; UTType mapping is not needed here.
            panel.AllowedFileTypes = [extension];
#pragma warning restore CA1422
            panel.AllowsOtherFileTypes = true;
        }
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => panel.Cancel(null), window);
        panel.BeginSheet(window, response => { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); completion.TrySetResult((long)response == 1 ? panel.Url?.Path! : null!); panel.Dispose(); });
        return completion.Task;
    });

    #endregion

    #region Pickers

    public async Task<IMailItemFolder> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
    {
        var picked = await PickAsync(Translator.MoveMailDialog_Title, null, availableFolders,
            folder => folder.ChildFolders ?? (IEnumerable<IMailItemFolder>)Array.Empty<IMailItemFolder>(),
            folder => folder.FolderName ?? string.Empty,
            folder => WinoIcons.Image(Views.Shell.ShellPaneRows.FolderGlyph(folder.SpecialFolderType), 14),
            folder => WinoStyle.FromHexString(folder.TextColorHex),
            folder => folder.IsMoveTarget,
            Translator.MailOperation_Move,
            folder => folder.IsMoveTarget ? null : string.Format(Translator.MoveMailDialog_InvalidFolderMessage, folder.FolderName));
        return picked!;
    }

    public async Task<IMailItemFolder> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
    {
        var structure = await folderService.GetFolderStructureForAccountAsync(accountId, true);
        return await ShowMoveMailFolderDialogAsync(structure.Folders);
    }

    public async Task<MailAccount> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
    {
        var picked = await PickAsync(Translator.AccountPickerDialog_Title, null, availableAccounts,
            _ => Array.Empty<MailAccount>(),
            account => string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} — {account.Address}",
            account => WinoIcons.Image(Wino.Mail.Controls.AppKit.Common.WinoAccountIconView.ProviderGlyph(Wino.Mail.ViewModels.Data.MailAccountIconInfoFactory.CreateProviderFallback(account.ProviderType, account.SpecialImapProvider).Provider, false), 16),
            account => WinoStyle.FromHexString(account.AccountColorHex) ?? WinoStyle.AvatarColor(account.Address),
            _ => true,
            Translator.Buttons_Continue);
        return picked!;
    }

    /// <summary>
    /// A titled sheet with a filter field and a native outline of items. Return confirms the
    /// selection, Escape cancels, double-click confirms. Non-selectable items explain why.
    /// </summary>
    private Task<T?> PickAsync<T>(string title, string? description, IReadOnlyList<T> roots, Func<T, IEnumerable<T>> children,
        Func<T, string> text, Func<T, NSImage?> symbol, Func<T, NSColor?> tint, Func<T, bool> selectable, string confirmTitle,
        Func<T, string?>? invalid = null) where T : class => PresentAsync(window =>
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new NSWindow(new CGRect(0, 0, 380, 460), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);

        var source = new PickerSource<T>(roots, children, text, symbol, tint, selectable);
        var outline = new NSOutlineView { HeaderView = null, RowHeight = 26, Style = NSTableViewStyle.SourceList, IndentationPerLevel = 14 };
        var column = new NSTableColumn("item") { ResizingMask = NSTableColumnResizing.Autoresizing };
        outline.AddColumn(column);
        outline.OutlineTableColumn = column;
        outline.DataSource = source;
        outline.Delegate = source;
        var scroll = new NSScrollView { DocumentView = outline, HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };

        var heading = WinoStyle.Label(title, WinoStyle.Heading);
        var filter = new NSSearchField { PlaceholderString = Translator.SearchBarPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        var message = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        message.Hidden = string.IsNullOrEmpty(description);
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var confirm = new NSButton { Title = confirmTitle, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", Enabled = false };

        void Finish(T? result)
        {
            if (completion.Task.IsCompleted) return;
            completion.TrySetResult(result);
            window.EndSheet(sheet);
        }

        void UpdateState()
        {
            var item = source.ItemAt(outline, outline.SelectedRow);
            var reason = item is null ? null : invalid?.Invoke(item);
            confirm.Enabled = item is not null && selectable(item);
            message.StringValue = reason ?? description ?? string.Empty;
            message.TextColor = reason is null ? WinoStyle.SecondaryText : WinoStyle.Critical;
            message.Hidden = string.IsNullOrEmpty(message.StringValue);
        }

        source.SelectionChanged = UpdateState;
        filter.Changed += (_, _) => { source.Filter = filter.StringValue; outline.ReloadData(); if (string.IsNullOrEmpty(source.Filter)) outline.ExpandItem(null, true); UpdateState(); };
        outline.DoubleClick += (_, _) => { var item = source.ItemAt(outline, outline.ClickedRow); if (item is not null && selectable(item)) Finish(item); };
        cancel.Activated += (_, _) => Finish(null);
        confirm.Activated += (_, _) => { var item = source.ItemAt(outline, outline.SelectedRow); if (item is not null && selectable(item)) Finish(item); };

        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, confirm);
        var stack = WinoLayout.VStack(WinoStyle.Space3, heading, filter, scroll, message, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { filter, scroll, buttons, message }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, sheet.ContentView!);
        sheet.InitialFirstResponder = filter;

        outline.ReloadData();
        outline.ExpandItem(null, true);
        window.BeginSheet(sheet, _ =>
        {
            completion.TrySetResult(null);
            outline.DataSource = null;
            outline.Delegate = null;
            source.Dispose();
            sheet.Dispose();
        });
        return completion.Task;
    });

    private sealed class PickerSource<T>(IReadOnlyList<T> roots, Func<T, IEnumerable<T>> children, Func<T, string> text,
        Func<T, NSImage?> symbol, Func<T, NSColor?> tint, Func<T, bool> selectable) : NSOutlineViewDataSource, INSOutlineViewDelegate where T : class
    {
        private readonly Dictionary<T, Box> _boxes = new(ReferenceEqualityComparer.Instance);
        public string Filter { get; set; } = string.Empty;
        public Action? SelectionChanged { get; set; }

        private sealed class Box(T item) : NSObject { public T Item { get; } = item; }

        private Box BoxFor(T item)
        {
            if (!_boxes.TryGetValue(item, out var box)) _boxes[item] = box = new Box(item);
            return box;
        }

        private IEnumerable<T> Flatten(IEnumerable<T> items) => items.SelectMany(item => new[] { item }.Concat(Flatten(children(item))));

        private IList<T> Items(NSObject? parent)
        {
            if (!string.IsNullOrWhiteSpace(Filter))
                return parent is null
                    ? Flatten(roots).Where(item => text(item).Contains(Filter, StringComparison.CurrentCultureIgnoreCase)).ToList()
                    : Array.Empty<T>();
            return parent is Box box ? children(box.Item).ToList() : roots.ToList();
        }

        public T? ItemAt(NSOutlineView outline, nint row) => row >= 0 && outline.ItemAtRow(row) is Box box ? box.Item : null;

        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => Items(item).Count;
        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item) => BoxFor(Items(item)[(int)childIndex]);
        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => Items(item).Count > 0;

        [Export("outlineView:viewForTableColumn:item:")]
        public NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var value = ((Box)item).Item;
            var cell = outlineView.MakeView("picker", this) as NSTableCellView;
            if (cell is null)
            {
                cell = new NSTableCellView { Identifier = "picker" };
                var image = new NSImageView { TranslatesAutoresizingMaskIntoConstraints = false };
                var label = WinoStyle.Label(string.Empty);
                cell.ImageView = image;
                cell.TextField = label;
                WinoLayout.Size(image, 16, 16);
                WinoLayout.Fill(WinoLayout.HStack(6, image, label), cell, 0, 2, 0, 4);
            }
            cell.TextField!.StringValue = text(value);
            cell.TextField.TextColor = selectable(value) ? WinoStyle.PrimaryText : WinoStyle.TertiaryText;
            cell.ImageView!.Image = symbol(value);
            cell.ImageView.ContentTintColor = tint(value) ?? WinoStyle.Accent;
            return cell;
        }

        [Export("outlineViewSelectionDidChange:")]
        public void SelectionDidChange(NSNotification notification) => SelectionChanged?.Invoke();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (var box in _boxes.Values) box.Dispose(); _boxes.Clear(); SelectionChanged = null; }
            base.Dispose(disposing);
        }
    }

    #endregion

    #region Keyboard shortcut and account order

    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut existingShortcut = null!) => PresentAsync(window =>
    {
        var completion = new TaskCompletionSource<KeyboardShortcutDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortcuts = services?.GetService<IKeyboardShortcutService>();
        var sheet = new NSWindow(new CGRect(0, 0, 420, 250), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);

        var modes = new[] { WinoApplicationMode.Mail, WinoApplicationMode.Calendar, WinoApplicationMode.Contacts, WinoApplicationMode.Tasks };
        var modeNames = new[] { Translator.KeyboardShortcuts_ModeMail, Translator.KeyboardShortcuts_ModeCalendar, Translator.ContactsPage_Title, Translator.ToDoPage_Title };
        var modePicker = new NSPopUpButton();
        modePicker.AddItems(modeNames);
        var actionPicker = new NSPopUpButton();
        var recorder = new ShortcutRecorder();
        var errorLabel = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);
        errorLabel.Hidden = true;
        List<KeyboardShortcutActionViewModel> actions = [];

        void RefreshActions(KeyboardShortcutAction selected)
        {
            var mode = modes[Math.Max(0, (int)modePicker.IndexOfSelectedItem)];
            actions = ActionsFor(mode).Select(action => new KeyboardShortcutActionViewModel(mode, action)).ToList();
            actionPicker.RemoveAllItems();
            actionPicker.AddItems(actions.Select(action => action.DisplayName).ToArray());
            var index = actions.FindIndex(action => action.Action == selected);
            actionPicker.SelectItem(Math.Max(0, index));
        }

        if (existingShortcut is not null)
        {
            modePicker.SelectItem(Math.Max(0, Array.IndexOf(modes, existingShortcut.Mode)));
            recorder.SetShortcut(existingShortcut.Key, existingShortcut.ModifierKeys);
        }
        RefreshActions(existingShortcut?.Action ?? KeyboardShortcutAction.None);
        modePicker.Activated += (_, _) => RefreshActions(KeyboardShortcutAction.None);

        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded };
        void Finish(KeyboardShortcutDialogResult result) { if (completion.TrySetResult(result)) window.EndSheet(sheet); }
        void ShowError(string text) { errorLabel.StringValue = text; errorLabel.Hidden = false; }

        cancel.Activated += (_, _) => Finish(KeyboardShortcutDialogResult.Canceled());
        save.Activated += async (_, _) =>
        {
            try
            {
                errorLabel.Hidden = true;
                if (string.IsNullOrWhiteSpace(recorder.Key)) { ShowError(Translator.KeyboardShortcuts_EnterKey); return; }
                var mode = modes[Math.Max(0, (int)modePicker.IndexOfSelectedItem)];
                var index = (int)actionPicker.IndexOfSelectedItem;
                if (index < 0 || index >= actions.Count || actions[index].Action == KeyboardShortcutAction.None) { ShowError(Translator.KeyboardShortcuts_SelectOperation); return; }
                var candidate = new KeyboardShortcut { Mode = mode, Key = recorder.Key, ModifierKeys = recorder.Modifiers, Action = actions[index].Action };
                if (shortcuts is not null)
                {
                    if (!shortcuts.IsShortcutAllowed(candidate)) { ShowError(Translator.KeyboardShortcuts_InvalidShortcut); return; }
                    if (await shortcuts.IsKeyCombinationInUseAsync(mode, recorder.Key, recorder.Modifiers, existingShortcut?.Id)) { ShowError(Translator.KeyboardShortcuts_ShortcutInUse); return; }
                }
                Finish(KeyboardShortcutDialogResult.Success(mode, recorder.Key, recorder.Modifiers, actions[index].Action));
            }
            catch (Exception exception)
            {
                error(exception);
                ShowError(Translator.KeyboardShortcuts_FailedToSave);
            }
        };

        var grid = new NSGridView { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 10, ColumnSpacing = 12 };
        grid.AddRow([RightLabel("Mode"), modePicker]);
        grid.AddRow([RightLabel(Translator.KeyboardShortcuts_Action), actionPicker]);
        grid.AddRow([RightLabel(Translator.KeyboardShortcuts_KeyCombination), recorder]);
        var title = WinoStyle.Label(existingShortcut is null ? Translator.KeyboardShortcuts_Add : Translator.KeyboardShortcuts_EditTitle, WinoStyle.Heading);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, save);
        var stack = WinoLayout.VStack(WinoStyle.Space3, title, grid, errorLabel, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        buttons.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        errorLabel.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        WinoLayout.Size(recorder, 220, 26);
        WinoLayout.Fill(stack, sheet.ContentView!);
        sheet.InitialFirstResponder = recorder;

        window.BeginSheet(sheet, _ => { completion.TrySetResult(KeyboardShortcutDialogResult.Canceled()); sheet.Dispose(); });
        return completion.Task;
    });

    private static NSTextField RightLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body, WinoStyle.SecondaryText);
        label.Alignment = NSTextAlignment.Right;
        return label;
    }

    private static KeyboardShortcutAction[] ActionsFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail =>
        [
            KeyboardShortcutAction.NewMail, KeyboardShortcutAction.ToggleReadUnread, KeyboardShortcutAction.ToggleFlag,
            KeyboardShortcutAction.ToggleArchive, KeyboardShortcutAction.Delete, KeyboardShortcutAction.Move,
            KeyboardShortcutAction.Reply, KeyboardShortcutAction.ReplyAll, KeyboardShortcutAction.Send
        ],
        WinoApplicationMode.Calendar => [KeyboardShortcutAction.NewEvent, KeyboardShortcutAction.Delete],
        _ => Enum.GetValues<KeyboardShortcutAction>().Where(action => action != KeyboardShortcutAction.None).ToArray()
    };

    /// <summary>Focusable field that records one key combination using Mac modifier names.</summary>
    private sealed class ShortcutRecorder : WinoSurfaceView
    {
        private readonly NSTextField _label;
        public string Key { get; private set; } = string.Empty;
        public ModifierKeys Modifiers { get; private set; }

        public ShortcutRecorder()
        {
            Fill = NSColor.TextBackground;
            Stroke = WinoStyle.GroupStroke;
            CornerRadius = WinoStyle.ControlRadius;
            _label = WinoStyle.Label(Translator.KeyboardShortcuts_PressKeysHere, WinoStyle.Body, WinoStyle.TertiaryText);
            _label.Alignment = NSTextAlignment.Center;
            WinoLayout.Fill(_label, this, 4, 8, 4, 8);
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.TextFieldRole;
            AccessibilityLabel = Translator.KeyboardShortcuts_KeyCombination;
        }

        public override bool AcceptsFirstResponder() => true;
        public override bool BecomeFirstResponder() { Stroke = WinoStyle.Accent; StrokeWidth = 2; return true; }
        public override bool ResignFirstResponder() { Stroke = WinoStyle.GroupStroke; StrokeWidth = 1; return true; }
        public override void MouseDown(NSEvent theEvent) => Window?.MakeFirstResponder(this);

        public override bool PerformKeyEquivalent(NSEvent theEvent)
        {
            // Capture Command combinations here instead of letting menus consume them.
            if (Window?.FirstResponder != this) return false;
            KeyDown(theEvent);
            return true;
        }

        public override void KeyDown(NSEvent theEvent)
        {
            var key = KeyName(theEvent);
            if (key is null) return;
            var flags = theEvent.ModifierFlags;
            var modifiers = ModifierKeys.None;
            if (flags.HasFlag(NSEventModifierMask.CommandKeyMask)) modifiers |= ModifierKeys.Command;
            if (flags.HasFlag(NSEventModifierMask.ControlKeyMask)) modifiers |= ModifierKeys.Control;
            if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask)) modifiers |= ModifierKeys.Alt;
            if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask)) modifiers |= ModifierKeys.Shift;
            SetShortcut(key, modifiers);
        }

        public void SetShortcut(string? key, ModifierKeys modifiers)
        {
            Key = key ?? string.Empty;
            Modifiers = modifiers;
            if (string.IsNullOrEmpty(Key))
            {
                _label.StringValue = Translator.KeyboardShortcuts_PressKeysHere;
                _label.TextColor = WinoStyle.TertiaryText;
                return;
            }
            var symbols = (modifiers.HasFlag(ModifierKeys.Control) ? "⌃" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "⌥" : "")
                + (modifiers.HasFlag(ModifierKeys.Shift) ? "⇧" : "") + (modifiers.HasFlag(ModifierKeys.Command) ? "⌘" : "");
            _label.StringValue = symbols + " " + Key;
            _label.TextColor = WinoStyle.PrimaryText;
            AccessibilityValue = new NSString(_label.StringValue);
        }

        private static string? KeyName(NSEvent theEvent)
        {
            switch (theEvent.KeyCode)
            {
                case 36: return "Enter";
                case 48: return "Tab";
                case 49: return "Space";
                case 51: return "Back";
                case 117: return "Delete";
                case 53: return "Escape";
                case 123: return "Left";
                case 124: return "Right";
                case 125: return "Down";
                case 126: return "Up";
            }
            var characters = theEvent.CharactersIgnoringModifiers;
            if (string.IsNullOrEmpty(characters)) return null;
            var character = char.ToUpperInvariant(characters[0]);
            if (char.IsLetter(character)) return character.ToString();
            if (char.IsDigit(character)) return "Number" + character;
            return null;
        }
    }

    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => PresentAsync<bool>(window =>
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new NSWindow(new CGRect(0, 0, 380, 380), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        var order = availableAccounts.ToList();
        var table = new NSTableView { HeaderView = null, RowHeight = 30, Style = NSTableViewStyle.Inset };
        table.AddColumn(new NSTableColumn("account") { ResizingMask = NSTableColumnResizing.Autoresizing });
        var source = new ReorderSource(order);
        table.DataSource = source;
        table.Delegate = source;
        table.RegisterForDraggedTypes([ReorderSource.DragType]);
        table.DraggingDestinationFeedbackStyle = NSTableViewDraggingDestinationFeedbackStyle.FeedbackStyleGap;
        var scroll = new NSScrollView { DocumentView = table, HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };

        var up = new NSButton { Image = WinoIcons.Image(WinoIconGlyph.ChevronUp, 12, null, Translator.MailFilters_MoveUp), BezelStyle = NSBezelStyle.Rounded };
        var down = new NSButton { Image = WinoIcons.Image(WinoIconGlyph.ChevronDown, 12, null, Translator.MailFilters_MoveDown), BezelStyle = NSBezelStyle.Rounded };
        void Move(int delta)
        {
            var row = (int)table.SelectedRow;
            var target = row + delta;
            if (row < 0 || target < 0 || target >= order.Count) return;
            (order[row], order[target]) = (order[target], order[row]);
            table.ReloadData();
            table.SelectRow(target, false);
        }
        up.Activated += (_, _) => Move(-1);
        down.Activated += (_, _) => Move(1);
        source.Reordered = table.ReloadData;

        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        cancel.Activated += (_, _) => { completion.TrySetResult(false); window.EndSheet(sheet); };
        save.Activated += async (_, _) =>
        {
            try
            {
                if (services?.GetService<IAccountService>() is { } accounts)
                    await accounts.UpdateAccountOrdersAsync(order.ToDictionary(account => account.StartupEntityId, account => order.IndexOf(account)));
                // Mirror the new order in the bound collection so the settings page updates in place.
                for (int index = 0; index < order.Count; index++)
                {
                    var current = availableAccounts.IndexOf(order[index]);
                    if (current >= 0 && current != index) availableAccounts.Move(current, index);
                }
                completion.TrySetResult(true);
                window.EndSheet(sheet);
            }
            catch (Exception exception) { error(exception); }
        };

        var title = WinoStyle.Label(Translator.SettingsReorderAccounts_Title, WinoStyle.Heading);
        var hint = WinoStyle.Label(Translator.MacOS_ReorderAccounts_Hint, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, up, down, WinoLayout.Spacer(), cancel, save);
        var stack = WinoLayout.VStack(WinoStyle.Space3, title, hint, scroll, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { hint, scroll, buttons }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, sheet.ContentView!);

        window.BeginSheet(sheet, _ =>
        {
            completion.TrySetResult(false);
            table.DataSource = null;
            table.Delegate = null;
            source.Dispose();
            sheet.Dispose();
        });
        return completion.Task;
    });

    private sealed class ReorderSource(List<IAccountProviderDetailViewModel> order) : NSTableViewDataSource, INSTableViewDelegate
    {
        public const string DragType = "com.winomail.account-row";
        public Action? Reordered { get; set; }

        public override nint GetRowCount(NSTableView tableView) => order.Count;

        [Export("tableView:viewForTableColumn:row:")]
        public NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var cell = tableView.MakeView("account", this) as NSTableCellView;
            if (cell is null)
            {
                cell = new NSTableCellView { Identifier = "account" };
                var image = new NSImageView { TranslatesAutoresizingMaskIntoConstraints = false };
                var label = WinoStyle.Label(string.Empty);
                cell.ImageView = image;
                cell.TextField = label;
                WinoLayout.Size(image, 12, 12);
                WinoLayout.Fill(WinoLayout.HStack(8, image, label), cell, 0, 4, 0, 4);
            }
            var account = order[(int)row];
            cell.TextField!.StringValue = account.StartupEntityTitle ?? account.Account?.Name ?? string.Empty;
            cell.ImageView!.Image = WinoIcons.Image(WinoIconGlyph.ReOrderDotsVertical, 12);
            cell.ImageView.ContentTintColor = WinoStyle.TertiaryText;
            return cell;
        }

        public override INSPasteboardWriting GetPasteboardWriterForRow(NSTableView tableView, nint row)
        {
            var item = new NSPasteboardItem();
            item.SetStringForType(row.ToString(), DragType);
            return item;
        }

        public override NSDragOperation ValidateDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation dropOperation)
        {
            if (dropOperation == NSTableViewDropOperation.On) tableView.SetDropRowDropOperation(row, NSTableViewDropOperation.Above);
            return NSDragOperation.Move;
        }

        public override bool AcceptDrop(NSTableView tableView, INSDraggingInfo info, nint row, NSTableViewDropOperation dropOperation)
        {
            var value = info.DraggingPasteboard.GetStringForType(DragType);
            if (!int.TryParse(value, out var from) || from < 0 || from >= order.Count) return false;
            var item = order[from];
            order.RemoveAt(from);
            var to = (int)row > from ? (int)row - 1 : (int)row;
            order.Insert(Math.Clamp(to, 0, order.Count), item);
            Reordered?.Invoke();
            return true;
        }
    }

    #endregion

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult result) => new AccountProgress(dispatcher, ResolveOwner, error);

    /// <summary>Specialized dialogs not ported yet: tell the user plainly, then return the cancel result.</summary>
    private async Task<T> NotYetAsync<T>()
    {
        await AlertAsync("Wino Mail", NotYetOnMac, [Translator.Buttons_OK]);
        return default!;
    }

    private Task NotYetAsync() => NotYetAsync<bool>();

    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders) => NotYetAsync<AccountCreationDialogResult>();
    /// <summary>
    /// Windows ImapValidationFailedDialog as a warning alert sheet: the error, the protocol log in a
    /// scrolling monospaced view, Close as the default button and Copy diagnostics.
    /// </summary>
    public async Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
    {
        NSView? accessory = null;
        await dispatcher.ExecuteOnUIThread(() => accessory = Views.Onboarding.ImapValidationLogView.Create(protocolLog));
        var choice = await AlertAsync(Translator.ImapValidationFailedDialog_Title, errorMessage,
            [Translator.Buttons_Close, Translator.ImapValidationFailedDialog_Copy], accessory, NSAlertStyle.Warning);
        if (choice != 1) return;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            var pasteboard = NSPasteboard.GeneralPasteboard;
            pasteboard.ClearContents();
            pasteboard.SetStringForType($"{errorMessage}{Environment.NewLine}{Environment.NewLine}{protocolLog}", NSPasteboard.NSPasteboardTypeString);
        });
    }
    public async Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
    {
        var profile = services!.GetRequiredService<IWinoAccountProfileService>();
        var email = await PresentAsync(window => new Views.Account.WinoAccountRegistrationSheet(profile).PresentAsync(window));
        if (!string.IsNullOrWhiteSpace(email))
            await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationSentDialog_Message, email), Translator.WinoAccount_EmailConfirmationSentDialog_Title, WinoCustomMessageDialogIcon.Information);
        return null;
    }

    /// <summary>Same flow as Windows: sign-in sheet, then the confirmation sheet or the reset-sent alert when the sheet asks for it.</summary>
    public async Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
    {
        var profile = services!.GetRequiredService<IWinoAccountProfileService>();
        var outcome = await PresentAsync(window => new Views.Account.WinoAccountLoginSheet(profile).PresentAsync(window));
        if (outcome is { ConfirmationDetails: { } details, PendingConfirmationEmail: { Length: > 0 } pending })
        {
            if (await PresentAsync(window => new Views.Account.WinoAccountEmailConfirmationSheet(profile, pending, details).PresentAsync(window)))
                await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationResentDialog_Message, pending), Translator.WinoAccount_EmailConfirmationResentDialog_Title, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        if (outcome is { PasswordResetEmail: { Length: > 0 } reset })
        {
            await ShowMessageAsync(string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, reset), Translator.WinoAccount_ForgotPasswordDialog_SuccessTitle, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        return outcome?.Account;
    }
    public Task<bool> ShowWinoAccountExportDialogAsync() => ShowConfirmationDialogAsync(
        $"{Translator.WinoAccount_Management_ExportDialog_Confirmation}\n\n{Translator.WinoAccount_Management_ExportDialog_EncryptionNotice}\n\n{Translator.WinoAccount_Management_ExportDialog_AccountsDisclaimer} {Translator.WinoAccount_Management_ExportDialog_AccountsRelogin_Device}",
        Translator.WinoAccount_Management_ExportDialog_Title, Translator.WinoAccount_Management_LocalDataExportAction);

    // Account work never owns the modal gate: browser authentication must remain presentable.
    private sealed class AccountProgress(IDispatcher dispatcher, Func<NSWindow?> owner, Action<Exception> error) : IAccountCreationDialog
    {
        private NSWindow? _sheet;
        private NSWindow? _parent;
        private CancellationTokenSource? _cancellation;
        public AccountCreationDialogState State { get; set; }

        public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource) => dispatcher.ExecuteOnUIThread(() =>
        {
            _cancellation = cancellationTokenSource;
            _sheet = new NSWindow(new CGRect(0, 0, 400, 150), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
            _sheet.ReleaseWhenClosed(false);
            var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true };
            spinner.StartAnimation(null);
            var title = WinoStyle.Label(Translator.MacOS_AccountCreation_Title, WinoStyle.Heading);
            var label = WinoStyle.Label(Translator.MacOS_AccountCreation_BrowserMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
            label.PreferredMaxLayoutWidth = 340;
            var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
            cancel.Activated += (_, _) => cancellationTokenSource.Cancel();
            var stack = WinoLayout.VStack(WinoStyle.Space3, WinoLayout.HStack(WinoStyle.Space2, spinner, title), label, WinoLayout.HStack(0, WinoLayout.Spacer(), cancel));
            stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
            WinoLayout.Fill(stack, _sheet.ContentView!);
            _parent = owner();
            if (_parent is not null) _parent.BeginSheet(_sheet, _ => { });
            else { _sheet.Center(); _sheet.MakeKeyAndOrderFront(null); }
        });

        public async void Complete(bool cancel)
        {
            try
            {
                if (cancel) _cancellation?.Cancel();
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (_sheet is null) return;
                    if (_parent is not null) _parent.EndSheet(_sheet); else _sheet.Close();
                    _sheet.Dispose();
                    _sheet = null;
                });
            }
            catch (Exception exception) { error(exception); }
        }
    }
}

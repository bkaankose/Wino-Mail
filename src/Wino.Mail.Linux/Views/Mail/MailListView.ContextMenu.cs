using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The mail row context menu (right click, long press, the Menu key and Shift+F10). Categories and the folder tree
/// load before the menu shows, as on Windows; a newer request wins over one still loading. The menu acts on the
/// clicked row without selecting it.
/// </summary>
internal sealed partial class MailListView
{
    private readonly Dictionary<MailRowCell, ContextMenuAttachment> _menuAttachments = new(ReferenceEqualityComparer.Instance);
    private WinoMenuPopover? _contextPopover;
    private int _contextMenuVersion;

    /// <summary>The row whose menu is open, or null. Hover actions stay visible on it.</summary>
    internal MailListRow? ContextMenuRow { get; private set; }

    internal event Action? ContextMenuRowChanged;

    partial void SetUpContextMenu()
    {
        var self = _self;
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => self.Value?.ContextMenuKey(args.Keyval, args.State) ?? false));
        List.AddController(keys);
        Bindings.Own(new ActionDisposable(() =>
        {
            if (self.Value is not { } view) return;
            view._contextMenuVersion++;
            view.CloseContextMenu();
            foreach (var attachment in view._menuAttachments.Values) attachment.Dispose();
            view._menuAttachments.Clear();
        }));
    }

    partial void ContextMenuCellSetup(MailRowCell cell)
    {
        var self = _self;
        _menuAttachments[cell] = ContextMenu.AttachRequest(cell.Root, (x, y) =>
        {
            if (self.Value is not { } view || cell.Item is not { } row) return;
            var (listX, listY) = view.PointInList(cell.Root, x ?? 24, y ?? cell.Root.GetHeight() / 2.0);
            view.Observe(view.OpenContextMenuAsync(row, listX, listY));
        });
    }

    partial void ContextMenuCellTeardown(MailRowCell cell)
    {
        if (_menuAttachments.Remove(cell, out var attachment)) attachment.Dispose();
    }

    /// <summary>Loads what the menu needs, then shows it on the list at the point (list coordinates); null = the row's centre.</summary>
    internal async Task<bool> OpenContextMenuAsync(MailListRow row, double? x, double? y)
    {
        var version = ++_contextMenuVersion;
        if (await BuildContextMenuAsync(row, version) is not { } entries) return false;
        var shown = false;
        await Dispatcher.ExecuteOnUIThread(() => shown = ShowContextMenu(row, entries, x, y, version));
        return shown;
    }

    private bool ShowContextMenu(MailListRow row, MailMenuEntry[] entries, double? x, double? y, int version)
    {
        if (IsReleased || version != _contextMenuVersion) return false;
        var builder = WinoMenus.Create();
        Fill(builder, entries);
        var menu = builder.Build();
        if (menu.IsEmpty)
        {
            menu.Dispose();
            return false;
        }
        if (x is null || y is null)
        {
            if (_byRow.TryGetValue(row, out var entry) && Cells.CellOf(entry) is { } cell) (x, y) = PointInList(cell.Root, 24, cell.Root.GetHeight() / 2.0);
            else (x, y) = (List.GetWidth() / 2.0, List.GetHeight() / 2.0);
        }
        CloseContextMenu();
        WinoMenuPopover? popover = null;
        var self = _self;
        popover = ContextMenu.Show(List, menu, x, y, () =>
        {
            if (self.Value is not { } view || !ReferenceEquals(view._contextPopover, popover)) return;
            view._contextPopover = null;
            view.ContextMenuRow = null;
            view.ContextMenuRowChanged?.Invoke();
        });
        _contextPopover = popover;
        ContextMenuRow = row;
        ContextMenuRowChanged?.Invoke();
        return true;
    }

    internal void CloseContextMenu() => _contextPopover?.Popdown();

    /// <summary>A point of <paramref name="from"/> in list coordinates.</summary>
    private (double X, double Y) PointInList(Gtk.Widget from, double x, double y)
    {
        var point = new Graphene.Point();
        point.Init((float)x, (float)y);
        return from.ComputePoint(List, point, out var inList) ? (inList.X, inList.Y) : (x, y);
    }

    private async Task<MailMenuEntry[]?> BuildContextMenuAsync(MailListRow row, int version)
    {
        var targets = MailListTargets.For(row, ViewModel);
        if (targets.IsEmpty) return null;
        var actions = ViewModel.GetAvailableMailActions(targets.Items).ToArray();
        var categoriesTask = LoadCategoriesAsync(targets);
        var foldersTask = LoadMoveFoldersAsync(targets);
        await Task.WhenAll(categoriesTask, foldersTask).ConfigureAwait(false);
        var (categories, assigned) = await categoriesTask.ConfigureAwait(false);
        var folders = await foldersTask.ConfigureAwait(false);

        MailMenuEntry[]? entries = null;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || version != _contextMenuVersion) return;
            var sources = new HashSet<Guid>();
            foreach (var item in targets.Items)
            {
                if (item.MailCopy.AssignedFolder is { } folder) sources.Add(folder.Id);
            }
            var input = new MailContextMenuInput(row, targets, actions, categories, assigned, folders, sources);
            entries = MailContextMenuModel.Build(input, Commands(row, targets));
        });
        return entries;
    }

    private async Task<(MailCategory[] Categories, HashSet<Guid> Assigned)> LoadCategoriesAsync(MailListTargets targets)
    {
        try
        {
            var (categories, assigned) = await ViewModel.GetAvailableCategoriesAsync(targets.Items).ConfigureAwait(false);
            return ([.. categories], [.. assigned]);
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return ([], []);
        }
    }

    /// <summary>The account's folder tree as the sidebar shapes it; null when the targets span accounts.</summary>
    private async Task<IMailItemFolder[]?> LoadMoveFoldersAsync(MailListTargets targets)
    {
        Guid? accountId = null;
        foreach (var item in targets.Items)
        {
            if (item.MailCopy.AssignedAccount?.Id is not { } id) continue;
            if (accountId is { } existing && existing != id) return null;
            accountId = id;
        }
        if (accountId is not { } account) return null;
        try { return [.. await _folders.GetFolderStructureForDisplayAsync(account).ConfigureAwait(false)]; }
        catch (Exception exception)
        {
            ReportError(exception);
            return null;
        }
    }

    private MailContextMenuCommands Commands(MailListRow row, MailListTargets targets) => new(
        (operation, folder) => RunMenuOperation(operation, folder, targets),
        (category, assignedToAll) => ViewModel.ToggleCategoryAssignmentAsync(category, targets.Items, assignedToAll),
        pinned => ViewModel.ChangePinnedStatusAsync(targets.Items, pinned),
        () =>
        {
            if (!IsReleased && _byRow.TryGetValue(row, out var entry)) ToggleThread(entry);
            return Task.CompletedTask;
        },
        () => ViewModel.CreateTestNotificationsAsync(targets.Items),
        operation => MailShortcutHints.Accelerator(_shortcuts, operation));

    private Task RunMenuOperation(MailOperation operation, IMailItemFolder? folder, MailListTargets targets) => operation switch
    {
        MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward => targets.ComposeTarget is { } mail ? CreateDraftAsync(mail, operation) : Task.CompletedTask,
        MailOperation.RetryDraftUpload => targets.ComposeTarget is { } draft ? ViewModel.RetryDraftUploadAsync(draft) : Task.CompletedTask,
        _ => ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(operation, targets.Mails, moveTargetFolder: folder)),
    };

    private async Task CreateDraftAsync(Wino.Mail.ViewModels.Data.MailItemViewModel mail, MailOperation operation)
    {
        if (!await _composeSources.EnsureAsync(mail.MailCopy) || IsReleased) return;
        await ViewModel.CreateDraftFromMailAsync(mail, operation);
    }

    private void Fill(WinoMenuBuilder builder, MailMenuEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                builder.Separator();
                continue;
            }
            var icon = entry.Glyph == WinoIconGlyph.None ? default : new WinoMenuIcon(WinoIcons.Glyph(entry.Glyph), entry.GlyphTintHex);
            if (entry.Children is { } children) builder.Submenu(entry.Title, child => Fill(child, children), entry.IsEnabled, icon);
            else if (entry.IsChecked is { } isChecked) builder.Check(entry.Title, isChecked, _ => Start(entry), entry.IsEnabled, icon: icon);
            else builder.Item(entry.Title, () => Start(entry), entry.IsEnabled, entry.Accelerator, icon, entry.IsDestructive);
        }
    }

    /// <summary>Reply, Move and Delete open a composer, a picker or a dialog: they start after the popover gave up its grab.</summary>
    private void Start(MailMenuEntry entry)
    {
        var self = _self;
        MainLoop.Post(() =>
        {
            if (self.Value is { IsReleased: false } view && entry.Run is { } run) view.Observe(run());
        });
    }

    private bool ContextMenuKey(uint keyval, Gdk.ModifierType state)
    {
        var menuKey = keyval == Gdk.Constants.KEY_Menu || (keyval == Gdk.Constants.KEY_F10 && (state & Gdk.ModifierType.ShiftMask) != 0);
        if (!menuKey || IsReleased) return false;
        MailListEntry? target = null;
        foreach (var entry in SelectedEntries())
        {
            target ??= entry;
            if (Cells.CellOf(entry) is { } cell && cell.Root.GetParent() is { } item && item.HasFocus)
            {
                target = entry;
                break;
            }
        }
        if (target is null) return false;
        Observe(OpenContextMenuAsync(target.Row, null, null));
        return true;
    }

#if DEBUG
    internal WinoMenuPopover? DebugContextPopover => _contextPopover;

    internal Task<MailMenuEntry[]?> DebugBuildContextMenuAsync(MailListRow row) => BuildContextMenuAsync(row, _contextMenuVersion);

    internal int DebugTargetCount(MailListRow row) => MailListTargets.For(row, ViewModel).Items.Length;
#endif
}

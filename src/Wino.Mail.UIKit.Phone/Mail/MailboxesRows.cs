using System.Collections.Specialized;
using System.ComponentModel;
using CoreFoundation;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The Mail shell menu as the iPhone Mailboxes rows (<see cref="WinoShellRowCell"/>): accounts with their unread pill and
/// sync bar, linked (merged) accounts, section headers and folders by depth, with "More" and parent folders expanding in
/// place. Unlike the read-only <c>ShellMenuRows</c> it follows every shown item's property changes (unread counts,
/// expansion, synchronization, selection) and coalesces them into one reload per main-loop turn. The New mail entry is
/// left out: Mailboxes has a compose button instead. Visibility and child rules come from <see cref="ShellMenuItemRules"/>.
/// </summary>
internal sealed class MailboxesRows : UITableViewSource
{
    private readonly IPictureStorageService? _pictures;
    private readonly List<(IMenuItem Item, int Depth)> _rows = [];
    private readonly List<INotifyPropertyChanged> _observedItems = [];
    private readonly List<INotifyCollectionChanged> _observedCollections = [];
    private ShellMenu? _menu;
    private object? _selected;
    private int _rebuildScheduled;
    private bool _disposed;

    public MailboxesRows(IPictureStorageService? pictures)
    {
        _pictures = pictures;
    }

    /// <summary>Raised on the main thread after the rows were rebuilt.</summary>
    public event EventHandler? Changed;

    /// <summary>A row was tapped.</summary>
    public event EventHandler<IMenuItem>? Invoked;

    /// <summary>A row was long-pressed; the owner returns its menu (null for none).</summary>
    public Func<IMenuItem, UIMenu?>? MenuProvider { get; set; }

    public IReadOnlyList<(IMenuItem Item, int Depth)> Rows => _rows;

    public void SetMenu(ShellMenu? menu)
    {
        _menu = menu;
        Rebuild();
    }

    /// <summary>The shell's selected item, drawn with the Windows selection fill and pipe.</summary>
    public void SetSelected(object? selected)
    {
        if (ReferenceEquals(_selected, selected)) return;
        _selected = selected;
        ScheduleRebuild();
    }

    /// <summary>The row of the shell's selected item, for the owner to select after a reload.</summary>
    public NSIndexPath? SelectedIndexPath
    {
        get
        {
            if (_selected is null) return null;
            int index = _rows.FindIndex(row => ReferenceEquals(row.Item, _selected));
            return index < 0 ? null : NSIndexPath.FromRowSection(index, 0);
        }
    }

    public IMenuItem? ItemAt(NSIndexPath indexPath) => indexPath.Row >= 0 && indexPath.Row < _rows.Count ? _rows[indexPath.Row].Item : null;

    /// <summary>True for rows whose children expand in place (More, parent folders, linked-account folder groups).</summary>
    public static bool HasChildren(IMenuItem item) => item is not IAccountMenuItem && ShellMenuItemRules.Children(item).Any();

    /// <summary>Rows that open a folder list (a tap navigates) rather than only toggling their children.</summary>
    public static bool Navigates(IMenuItem item)
        => ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.IsEnabled(item)
           && (item is IAccountMenuItem || ShellMenuItemRules.SelectsOnInvoked(item));

    private void ScheduleRebuild()
    {
        if (_disposed || Interlocked.Exchange(ref _rebuildScheduled, 1) == 1) return;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            Interlocked.Exchange(ref _rebuildScheduled, 0);
            if (!_disposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        Unobserve();
        _rows.Clear();
        if (_menu is { } menu)
        {
            Observe(menu.Items);
            Add(menu.Items, 0);
            if (menu.FooterItems is { } footer)
            {
                Observe(footer);
                Add(footer, 0);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(IEnumerable<IMenuItem> items, int depth)
    {
        foreach (var item in items.ToArray())
        {
            if (item is SeperatorItem or NewMailMenuItem || !ShellMenuItemRules.IsShown(item) || Kind(item) is null) continue;
            _rows.Add((item, depth));
            if (item is INotifyPropertyChanged notifying)
            {
                notifying.PropertyChanged += ItemChanged;
                _observedItems.Add(notifying);
            }
            var children = ShellMenuItemRules.Children(item);
            Observe(children);
            // Accounts always show their folders; other parents only while expanded.
            if (item is IAccountMenuItem || item.IsExpanded) Add(children, depth + 1);
        }
    }

    private void Observe(IEnumerable<IMenuItem> collection)
    {
        if (collection is not INotifyCollectionChanged notifying) return;
        notifying.CollectionChanged += CollectionChanged;
        _observedCollections.Add(notifying);
    }

    private void Unobserve()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        foreach (var collection in _observedCollections) collection.CollectionChanged -= CollectionChanged;
        _observedItems.Clear();
        _observedCollections.Clear();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs args) => ScheduleRebuild();

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRebuild();

    private static WinoShellRowKind? Kind(IMenuItem item) => item switch
    {
        ShellSectionHeaderMenuItem => WinoShellRowKind.Header,
        IAccountMenuItem => WinoShellRowKind.Account,
        IBaseFolderMenuItem or MergedAccountMoreFolderMenuItem => WinoShellRowKind.Folder,
        _ => null
    };

    public override nint RowsInSection(UITableView tableview, nint section) => _rows.Count;

    public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        => (nfloat)WinoShellRowCell.HeightFor(ItemAt(indexPath) is { } item ? Kind(item) ?? WinoShellRowKind.Folder : WinoShellRowKind.Folder);

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
        var (item, depth) = _rows[indexPath.Row];
        cell.Item = item;
        switch (item)
        {
            case ShellSectionHeaderMenuItem header:
                cell.ConfigureHeader(header.Title);
                break;
            case MergedAccountMenuItem merged:
                cell.ConfigureAccount(MailAccountIconInfoFactory.CreateProviderFallback(MailProviderType.IMAP4, SpecialImapProvider.None),
                    merged.MergedAccountName ?? string.Empty, null, merged.UnreadItemCount);
                cell.SetSync(merged.IsSynchronizationInProgress, merged.IsProgressIndeterminate, merged.SynchronizationProgressValue);
                break;
            case AccountMenuItem account:
                var icon = _pictures is null
                    ? MailAccountIconInfoFactory.CreateProviderFallback(account.Account.ProviderType, account.Account.SpecialImapProvider)
                    : MailAccountIconInfoFactory.Create(account.Account, _pictures);
                cell.ConfigureAccount(icon, account.AccountName, account.AccountAddress, account.UnreadItemCount);
                cell.SetSync(account.IsSynchronizationInProgress, account.IsProgressIndeterminate, account.SynchronizationProgressValue);
                break;
            case MergedAccountMoreFolderMenuItem more:
                // A linked account's More group expands in place like a single account's More folder.
                cell.ConfigureFolder(WinoIconGlyph.SpecialFolderMore, Translator.More, 0, ShellMenuItemRules.FolderIndent(more), more.IsExpanded);
                break;
            case IBaseFolderMenuItem folder:
                cell.ConfigureFolder(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), folder.FolderName ?? string.Empty,
                    folder.ShowUnreadCount ? folder.UnreadItemCount : 0, ShellMenuItemRules.FolderIndent(folder),
                    HasChildren(folder) ? folder.IsExpanded : null);
                break;
        }
        // After Configure*, which resets the row: interaction follows the shared rules.
        cell.Highlightable = ShellMenuItemRules.IsHighlightable(item) && item is not ShellSectionHeaderMenuItem;
        cell.ShowsIndicator = ShellMenuItemRules.ShowsIndicator(item);
        if (HasChildren(item))
            cell.AccessibilityValue = string.Join(", ", new[] { cell.AccessibilityValue, item.IsExpanded ? Translator.Accessibility_MailThreadExpanded : Translator.Accessibility_MailThreadCollapsed }
                .Where(static part => !string.IsNullOrEmpty(part)));
        return cell;
    }

    public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        => ItemAt(indexPath) is { } item && ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.IsEnabled(item) ? indexPath : null;

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, false);
        if (ItemAt(indexPath) is { } item) Invoked?.Invoke(this, item);
    }

    public override UIContextMenuConfiguration? GetContextMenuConfiguration(UITableView tableView, NSIndexPath indexPath, CoreGraphics.CGPoint point)
    {
        if (ItemAt(indexPath) is not { } item || MenuProvider?.Invoke(item) is not { } menu) return null;
        return UIContextMenuConfiguration.Create(null, null, _ => menu);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            Unobserve();
            Changed = null;
            Invoked = null;
            MenuProvider = null;
        }
        base.Dispose(disposing);
    }
}

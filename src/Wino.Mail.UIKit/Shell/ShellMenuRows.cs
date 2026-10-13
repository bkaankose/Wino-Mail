using System.Collections.Specialized;
using Wino.Core.Domain.Enums;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Shell;

/// <summary>
/// The published shell menu as flat table rows for <see cref="WinoShellRowCell"/>: the new-item entry, account rows,
/// headers and folders with their depth (children of expanded items follow their parent). Used by the iPhone Mailboxes
/// screen and the iPad sidebar. The source raises <see cref="Changed"/> when the menu collection changes.
/// </summary>
public sealed class ShellMenuRows : UITableViewSource
{
    private readonly IPictureStorageService? _pictures;
    private readonly List<(IMenuItem Item, int Depth)> _rows = new();
    private ShellMenu? _menu;

    public ShellMenuRows(IPictureStorageService? pictures)
    {
        _pictures = pictures;
    }

    /// <summary>Raised on the main thread after the rows were rebuilt.</summary>
    public event EventHandler? Changed;

    /// <summary>A row was tapped; null keeps the rows read-only.</summary>
    public Action<IMenuItem>? Invoked { get; set; }

    public IReadOnlyList<(IMenuItem Item, int Depth)> Rows => _rows;

    public void SetMenu(ShellMenu? menu)
    {
        if (_menu?.Items is INotifyCollectionChanged previous) previous.CollectionChanged -= MenuChanged;
        _menu = menu;
        if (_menu?.Items is INotifyCollectionChanged next) next.CollectionChanged += MenuChanged;
        Rebuild();
    }

    private void MenuChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (NSThread.IsMain) Rebuild();
        else UIApplication.SharedApplication.BeginInvokeOnMainThread(Rebuild);
    }

    private void Rebuild()
    {
        _rows.Clear();
        if (_menu is { } menu)
        {
            Add(menu.Items, 0);
            if (menu.FooterItems is { } footer) Add(footer, 0);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(IEnumerable<IMenuItem> items, int depth)
    {
        foreach (var item in items.ToArray())
        {
            if (item is SeperatorItem || !ShellMenuItemRules.IsShown(item) || Kind(item) is null) continue;
            _rows.Add((item, depth));
            // Accounts always show their folders; other parents only while expanded.
            if (item is IAccountMenuItem || item.IsExpanded) Add(ShellMenuItemRules.Children(item), depth + 1);
        }
    }

    private static WinoShellRowKind? Kind(IMenuItem item) => item switch
    {
        NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem => WinoShellRowKind.NewItem,
        ShellSectionHeaderMenuItem => WinoShellRowKind.Header,
        IAccountMenuItem => WinoShellRowKind.Account,
        IBaseFolderMenuItem => WinoShellRowKind.Folder,
        _ => null
    };

    public override nint RowsInSection(UITableView tableview, nint section) => _rows.Count;

    public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        => (nfloat)WinoShellRowCell.HeightFor(Kind(_rows[indexPath.Row].Item) ?? WinoShellRowKind.Folder);

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
        var (item, depth) = _rows[indexPath.Row];
        cell.Item = item;
        cell.Highlightable = Invoked is not null && ShellMenuItemRules.IsHighlightable(item);
        cell.ShowsIndicator = ShellMenuItemRules.ShowsIndicator(item);
        switch (item)
        {
            case NewMailMenuItem:
                cell.ConfigureNewItem(Translator.MenuNewMail);
                break;
            case NewContactMenuItem or NewTaskListMenuItem:
                cell.ConfigureNewItem(Translator.Buttons_Create, WinoIconGlyph.Add);
                break;
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
            case IBaseFolderMenuItem folder:
                var glyph = SpecialFolderGlyphs.GetGlyph(folder.SpecialFolderType);
                cell.ConfigureFolder(glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph, folder.FolderName ?? string.Empty,
                    folder.ShowUnreadCount ? folder.UnreadItemCount : 0, depth > 0 ? depth - 1 : 0,
                    folder.SubMenuItems is { Count: > 0 } ? folder.IsExpanded : null);
                break;
        }
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (Invoked is { } invoked) invoked(_rows[indexPath.Row].Item);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _menu?.Items is INotifyCollectionChanged items) items.CollectionChanged -= MenuChanged;
        base.Dispose(disposing);
    }
}

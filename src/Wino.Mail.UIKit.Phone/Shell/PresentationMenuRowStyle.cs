using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// Drawer rows drawn from the shared <see cref="ShellMenuRowPresentation"/> (the Windows pane templates, as the iPad
/// sidebar draws them): used for the Contacts drawer (new contact and list, accounts, address books and lists). The
/// current row is the shell's selected item.
/// </summary>
internal sealed class PresentationMenuRowStyle(string title, IPictureStorageService? pictures) : IModeMenuRowStyle
{
    public string Title => title;

    public WinoShellRowKind? Kind(IMenuItem item) => ShellMenuRowPresentation.KindOf(item) switch
    {
        ShellMenuRowKind.NewItem => WinoShellRowKind.NewItem,
        ShellMenuRowKind.Header => WinoShellRowKind.Header,
        ShellMenuRowKind.Account => WinoShellRowKind.Account,
        _ => WinoShellRowKind.Folder
    };

    public void Configure(WinoShellRowCell cell, IMenuItem item, int depth)
    {
        cell.AccessoryView = null;
        var row = ShellMenuRowPresentation.Describe(item, depth, pictures);
        switch (row.Kind)
        {
            case ShellMenuRowKind.NewItem:
                cell.ConfigureNewItem(row.Title, row.Glyph);
                break;
            case ShellMenuRowKind.Header:
                cell.ConfigureHeader(row.Title);
                break;
            case ShellMenuRowKind.Account when row.AccountIcon is { } icon:
                cell.ConfigureAccount(icon, row.Title, row.Subtitle, row.UnreadCount);
                cell.SetSync(row.ShowsSync, row.IsSyncIndeterminate, row.SyncValue);
                break;
            default:
                cell.ConfigureFolder(row.Glyph, row.Title, row.UnreadCount, row.Indent, row.IsExpanded);
                break;
        }
    }

    public bool IsCurrent(IMenuItem item, object? selectedMenuItem, object? contentViewModel) => ReferenceEquals(item, selectedMenuItem);
}

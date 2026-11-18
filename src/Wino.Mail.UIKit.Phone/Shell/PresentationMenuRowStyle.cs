using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// Drawer rows drawn from the shared <see cref="ShellMenuRowPresentation"/> (the Windows pane templates, as the iPad
/// sidebar draws them): used for the Contacts drawer (new contact and list, accounts, address books and lists). The
/// current row is the shell's selected item.
/// </summary>
internal sealed class PresentationMenuRowStyle(string title, IPictureStorageService? pictures) : IModeMenuRowStyle
{
    public string Title => title;

    public WinoShellRowKind? Kind(IMenuItem item) => item is NewAddressBookMenuItem ? WinoShellRowKind.NewItem : ShellMenuRowPresentation.KindOf(item) switch
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
            case ShellMenuRowKind.Folder when item is NewAddressBookMenuItem:
                cell.ConfigureNewItem(row.Title, row.Glyph);
                break;
            case ShellMenuRowKind.Header:
                cell.ConfigureHeader(row.Title);
                break;
            case ShellMenuRowKind.Folder when item is AccountGroupMenuItem accountGroup:
                cell.ConfigureGroup(row.Glyph, row.Title, row.UnreadCount, row.IsExpanded, WinoStyle.AccountGroupColor(row.ColorKey), accountGroup.AutomationName);
                break;
            case ShellMenuRowKind.Account when item is MergedAccountMenuItem:
                cell.ConfigureGroup(row.Glyph, row.Title, row.UnreadCount, row.IsExpanded, WinoStyle.AccountGroupColor(row.ColorKey));
                cell.SetSync(row.ShowsSync, row.IsSyncIndeterminate, row.SyncValue);
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

    /// <summary>Rename and Delete of a contact list, category or editable address book (Windows ContactListContextRequested).</summary>
    public UIMenu? ContextMenu(IMenuItem item)
    {
        if (item is not ContactFilterViewModel { HasAccountIcon: false, CanRenameOrDelete: true } filter) return null;
        var rename = UIAction.Create(filter.IsCategory ? Translator.Buttons_Edit : Translator.ContactList_Rename,
            WinoIcons.Image(WinoIconGlyph.Rename, 18), null, _ => filter.RenameListCommand.Execute(null));
        if (!filter.RenameListCommand.CanExecute(null)) rename.Attributes |= UIMenuElementAttributes.Disabled;
        var delete = UIAction.Create(Translator.ContactsPage_Delete, WinoIcons.Image(WinoIconGlyph.Delete, 18), null, _ => filter.DeleteListCommand.Execute(null));
        delete.Attributes |= UIMenuElementAttributes.Destructive;
        if (!filter.DeleteListCommand.CanExecute(null)) delete.Attributes |= UIMenuElementAttributes.Disabled;
        return UIMenu.Create([rename, delete]);
    }
}

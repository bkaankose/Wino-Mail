using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// Sidebar menu entries of the Contacts rows (macOS ShellSidebarContextMenus ContactListItems): New category on the
/// categories row; rename or edit and delete on lists, categories and manageable address books.
/// </summary>
internal static class ContactsSidebarMenus
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        ShellSidebarContextMenus.AddExtension(Build);
    }

    internal static SidebarMenuEntry[] Build(IMenuItem item, ShellSidebarMenuContext context)
    {
        switch (item)
        {
            case ContactCategoriesExpanderMenuItem { NewCategoryRequested: { } newCategory }:
                return [new(Translator.ContactsPage_NewCategory + "…", newCategory, Glyph: WinoIconGlyph.New)];
            case ContactFilterViewModel { HasAccountIcon: false, CanRenameOrDelete: true } list:
                var entries = new List<SidebarMenuEntry>();
                if (list.IsCategory)
                    entries.Add(new(Translator.ContactsPage_NewCategory + "…", () => list.NewCategoryCommand.ExecuteAsync(null), Glyph: WinoIconGlyph.New));
                entries.Add(new((list.IsCategory ? Translator.Buttons_Edit : Translator.ContactList_Rename) + "…", () =>
                {
                    list.RenameListCommand.Execute(null);
                    return Task.CompletedTask;
                }, list.RenameListCommand.CanExecute(null), Glyph: WinoIconGlyph.Rename));
                entries.Add(new(Translator.ContactsPage_Delete, () =>
                {
                    list.DeleteListCommand.Execute(null);
                    return Task.CompletedTask;
                }, list.DeleteListCommand.CanExecute(null), Glyph: WinoIconGlyph.Delete, IsDestructive: true));
                return [.. entries];
            default:
                return [];
        }
    }
}

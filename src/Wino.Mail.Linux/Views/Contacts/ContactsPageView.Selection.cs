using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// The contact row menu: right click, long press, the Menu key and Shift+F10. A row outside the selection is selected
/// first (as in Finder and Files), so the menu acts on what is selected. The menu opens once the assignable lists are
/// loaded, so "Add to list" is complete when it appears; it is shown on the list, not on the recycled cell.
/// </summary>
internal sealed partial class ContactsPageView
{
    private readonly Dictionary<ContactCell, IDisposable> _menuAttachments = new(ReferenceEqualityComparer.Instance);
    private int _menuVersion;
    private bool _menuKeysAttached;

    partial void ContextCellSetup(ContactCell cell)
    {
        AttachMenuKeys();
        var self = _self;
        _menuAttachments[cell] = ContextMenu.AttachRequest(cell.Root, (x, y) =>
        {
            if (self.Value is not { } view || cell.Item is not { } contact) return;
            var (listX, listY) = view.PointInList(cell.Root, x ?? 24, y ?? cell.Root.GetHeight() / 2.0);
            view.Observe(view.OpenContextMenuAsync(contact, listX, listY));
        });
    }

    partial void ContextCellTeardown(ContactCell cell)
    {
        if (_menuAttachments.Remove(cell, out var attachment)) attachment.Dispose();
    }

    private void AttachMenuKeys()
    {
        if (_menuKeysAttached) return;
        _menuKeysAttached = true;
        var self = _self;
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => self.Value?.MenuKey(args.Keyval, args.State) ?? false));
        var list = _list;
        list.AddController(keys);
        Bindings.Own(new ActionDisposable(() =>
        {
            list.RemoveController(keys);
            if (self.Value is not { } view) return;
            view._menuVersion++;
            foreach (var attachment in view._menuAttachments.Values) attachment.Dispose();
            view._menuAttachments.Clear();
        }));
    }

    private bool MenuKey(uint keyval, Gdk.ModifierType state)
    {
        var menuKey = keyval == Gdk.Constants.KEY_Menu || (keyval == Gdk.Constants.KEY_F10 && (state & Gdk.ModifierType.ShiftMask) != 0);
        if (!menuKey || IsReleased) return false;
        AccountContactViewModel? target = ViewModel.SelectedContact;
        foreach (var contact in ListViews.SelectedValues<AccountContactViewModel>(_selection))
        {
            if (_cells.CellOf(contact) is { } cell && cell.Root.GetParent() is { } item && item.HasFocus)
            {
                target = contact;
                break;
            }
        }
        if (target is null) return false;
        var point = _cells.CellOf(target) is { } shown ? PointInList(shown.Root, 24, shown.Root.GetHeight() / 2.0) : (24, 24);
        Observe(OpenContextMenuAsync(target, point.X, point.Y));
        return true;
    }

    internal async Task OpenContextMenuAsync(AccountContactViewModel contact, double x, double y)
    {
        var version = ++_menuVersion;
        var entries = await BuildContextMenuAsync(contact);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || version != _menuVersion || entries.Length == 0) return;
            var builder = WinoMenus.Create();
            ContactContextMenu.Populate(builder, entries);
            var menu = builder.Build();
            if (menu.IsEmpty)
            {
                menu.Dispose();
                return;
            }
            ContextMenu.Show(_list, menu, x, y);
        });
    }

    /// <summary>The menu entries for a right click on <paramref name="contact"/>, after selecting it when it was not.</summary>
    internal async Task<ContactMenuEntry[]> BuildContextMenuAsync(AccountContactViewModel contact)
    {
        AccountContactViewModel[] targets = [];
        var hasCandidates = false;
        MailCategory[] categories = [];
        var isListFilter = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            if (PositionOf(contact) is { } position && !_selection.IsSelected(position)) ListViews.Select(_selection, [position]);
            targets = [.. ViewModel.ResolveContactContextTargets(contact)];
            hasCandidates = targets.Length > 0 && ViewModel.ContactLists.Any(list => targets.All(t => t.SourceContact?.AddressBookId == list.AddressBookId));
            categories = [.. ViewModel.GetAvailableCategories(targets).Where(static category => category is not null)];
            isListFilter = ViewModel.SelectedFilter?.IsList == true;
        });
        if (targets.Length == 0 || IsReleased) return [];
        ContactList[] lists = hasCandidates ? [.. await ViewModel.GetAssignableListsAsync(targets)] : [];
        var self = _self;
        return ContactContextMenu.Build(contact, targets, lists, hasCandidates, categories, isListFilter, new ContactContextMenu.Actions(
            SendMail: items => self.Value?.ComposeTo(items),
            Edit: item => self.Value?.ViewModel.EditContactCommand.Execute(item),
            ToggleFavorite: items => self.Value?.Observe(self.Value.ViewModel.FavoriteContactsAsync(items)),
            AddToList: (list, items) => self.Value?.Observe(self.Value.ViewModel.AssignContactsToListAsync(list, [.. items.Select(static i => i.Id)])),
            ToggleCategory: (category, items) => self.Value?.Observe(self.Value.ViewModel.ToggleContactCategoryAsync(category, items)),
            RemoveFromList: items => self.Value?.Observe(self.Value.RemoveFromCurrentListAsync(items)),
            Delete: items => self.Value?.Observe(self.Value.ViewModel.DeleteContactsAsync(items))));
    }

    private async Task RemoveFromCurrentListAsync(AccountContactViewModel[] targets)
    {
        foreach (var contact in targets)
        {
            if (IsReleased) return;
            await ViewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact);
        }
    }

    private (double X, double Y) PointInList(Gtk.Widget from, double x, double y)
    {
        var point = new Graphene.Point();
        point.Init((float)x, (float)y);
        return from.ComputePoint(_list, point, out var inList) ? (inList.X, inList.Y) : (x, y);
    }
}

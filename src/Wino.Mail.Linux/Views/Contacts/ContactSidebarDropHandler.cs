using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// Contacts dropped on a contact list row of the sidebar (ContactFilterViewModel as IMenuItemDropTarget). Other rows
/// get no verdict, so the plain card shows over them (macOS); the list on screen accepts a drop too.
/// </summary>
internal sealed class ContactSidebarDropHandler(Action<Exception> error) : ISidebarDropHandler
{
    public bool IsDragActive => ContactDragPayload.Current.Count > 0;

    public SidebarDropVerdict Validate(IMenuItem item, bool isSelected)
    {
        var properties = ContactDragPayload.DataProperties(ContactDragPayload.Current);
        return item is ContactFilterViewModel filter && ShellMenuItemRules.IsEnabled(filter) && filter.CanAccept(properties)
            ? new SidebarDropVerdict(Gdk.DragAction.Copy, filter.GetDropCaption(properties), false)
            : SidebarDropVerdict.None;
    }

    public void ShowCaption(SidebarDropVerdict verdict) => ContactDragPayload.SetCaption(verdict.Caption, verdict.Refused);

    public bool Drop(IMenuItem item, bool isSelected, string? text)
    {
        if (ContactDragPayload.Resolve(text) is not { } ids || item is not ContactFilterViewModel filter) return false;
        var properties = ContactDragPayload.DataProperties(ids);
        if (!filter.CanAccept(properties)) return false;
        Serilog.Log.Information("Contact drop: adding {Count} contacts to a list.", ids.Length);
        _ = AddAsync(filter, properties);
        ContactDragPayload.End();
        return true;
    }

    private async Task AddAsync(ContactFilterViewModel filter, Dictionary<string, object> properties)
    {
        try { await filter.HandleDropAsync(properties); }
        catch (Exception exception) { error(exception); }
    }
}

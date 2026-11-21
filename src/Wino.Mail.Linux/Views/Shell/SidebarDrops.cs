using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.Shell;

internal readonly record struct SidebarDropVerdict(Gdk.DragAction Action, string? Caption, bool Refused)
{
    public static SidebarDropVerdict None { get; } = new(default, null, false);
}

/// <summary>
/// One kind of in-app drag that can end on a sidebar row (mail; later contacts and task lists). The sidebar has one
/// string drop target per row and asks the handler whose drag is active.
/// </summary>
internal interface ISidebarDropHandler
{
    bool IsDragActive { get; }

    SidebarDropVerdict Validate(IMenuItem item, bool isSelected);

    /// <summary>Shows the verdict's caption on the handler's own drag icon.</summary>
    void ShowCaption(SidebarDropVerdict verdict);

    /// <summary>True when the drop was taken; <paramref name="text"/> is the dropped string.</summary>
    bool Drop(IMenuItem item, bool isSelected, string? text);
}

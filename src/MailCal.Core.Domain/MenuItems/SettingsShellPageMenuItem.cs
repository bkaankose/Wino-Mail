using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Domain.MenuItems;

/// <summary>
/// A settings section in the pane. Entries inside a collapsible group carry that group as
/// their <see cref="IMenuItem.ParentMenuItem"/> so a shell can expand and realize the group
/// before it selects the entry.
/// </summary>
public partial class SettingsShellPageMenuItem(
    MailCalPage pageType,
    string title,
    string description,
    MailCalIconGlyph icon,
    IMenuItem parentMenuItem = null) : MenuItemBase(entityId: null, parentMenuItem)
{
    public MailCalPage PageType { get; } = pageType;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    // MailCalIconGlyph is source-generated in this assembly, so [ObservableProperty] cannot resolve it.
    public MailCalIconGlyph Icon { get; } = icon;
}

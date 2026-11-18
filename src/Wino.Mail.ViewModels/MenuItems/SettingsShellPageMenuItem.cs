using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A settings section in the pane. Entries inside a collapsible group carry that group as
/// their <see cref="IMenuItem.ParentMenuItem"/> so a shell can expand and realize the group
/// before it selects the entry.
/// </summary>
public partial class SettingsShellPageMenuItem(
    WinoPage pageType,
    string title,
    string description,
    WinoIconGlyph icon,
    IMenuItem parentMenuItem = null) : MenuItemBase(entityId: null, parentMenuItem)
{
    public WinoPage PageType { get; } = pageType;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    // WinoIconGlyph is source-generated in this assembly, so [ObservableProperty] cannot resolve it.
    public WinoIconGlyph Icon { get; } = icon;
}

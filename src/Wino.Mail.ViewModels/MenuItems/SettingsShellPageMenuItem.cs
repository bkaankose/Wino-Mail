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
    private string _title = title;
    private string _description = description;

    public WinoPage PageType { get; } = pageType;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public WinoIconGlyph Icon { get; } = icon;
}

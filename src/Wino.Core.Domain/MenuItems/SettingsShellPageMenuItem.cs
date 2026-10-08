using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

public partial class SettingsShellPageMenuItem(
    WinoPage pageType,
    string title,
    string description,
    WinoIconGlyph icon) : MenuItemBase
{
    public WinoPage PageType { get; } = pageType;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    // WinoIconGlyph is source-generated in this assembly, so [ObservableProperty] cannot resolve it.
    public WinoIconGlyph Icon { get; } = icon;
}

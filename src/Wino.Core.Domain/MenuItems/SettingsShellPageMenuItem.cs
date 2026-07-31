using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

public partial class SettingsShellPageMenuItem(
    WinoPage pageType,
    string title,
    string description,
    string glyph) : MenuItemBase
{
    public WinoPage PageType { get; } = pageType;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    [ObservableProperty]
    public partial string Glyph { get; set; } = glyph;
}

using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

public partial class SettingsShellSectionMenuItem(string title, WinoIconGlyph icon) : MenuItemBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    public WinoIconGlyph Icon { get; } = icon;
}

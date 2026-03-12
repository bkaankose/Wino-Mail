using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Core.Domain.MenuItems;

public partial class SettingsShellSectionMenuItem(string title, string glyph) : MenuItemBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Glyph { get; set; } = glyph;
}

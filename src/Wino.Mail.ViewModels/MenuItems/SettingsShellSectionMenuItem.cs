using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

public partial class SettingsShellSectionMenuItem(string title, WinoIconGlyph icon) : MenuItemBase
{
    private string _title = title;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public WinoIconGlyph Icon { get; } = icon;
}

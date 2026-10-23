using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.MenuItems;

public partial class SettingsShellSectionMenuItem(string title, MailCalIconGlyph icon) : MenuItemBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    public MailCalIconGlyph Icon { get; } = icon;
}

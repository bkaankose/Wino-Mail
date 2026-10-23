using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.MenuItems;

/// <summary>
/// A collapsible mode group in the settings pane. Groups are pane affordances only: they are not
/// navigation targets, so invoking one expands it rather than navigating.
/// </summary>
public partial class SettingsShellGroupMenuItem(string title, MailCalIconGlyph icon)
    : MenuItemBase<string, SettingsShellPageMenuItem>(title, entityId: null)
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    public MailCalIconGlyph Icon { get; } = icon;
}

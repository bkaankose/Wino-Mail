using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A collapsible mode group in the settings pane. Groups are pane affordances only: they are not
/// navigation targets, so invoking one expands it rather than navigating.
/// </summary>
public partial class SettingsShellGroupMenuItem(string title, WinoIconGlyph icon)
    : MenuItemBase<string, SettingsShellPageMenuItem>(title, entityId: null)
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    public WinoIconGlyph Icon { get; } = icon;
}

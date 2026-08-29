using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A collapsible mode group in the settings pane. Groups are pane affordances only: they are not
/// navigation targets, so invoking one expands it rather than navigating.
/// </summary>
public partial class SettingsShellGroupMenuItem(string title, string glyph)
    : MenuItemBase<string, SettingsShellPageMenuItem>(title, entityId: null)
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Glyph { get; set; } = glyph;
}

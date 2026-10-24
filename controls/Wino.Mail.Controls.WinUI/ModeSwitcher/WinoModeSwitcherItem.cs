using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;

namespace Wino.Mail.Controls.ModeSwitcher;

/// <summary>
/// One mode in a <see cref="WinoModeSwitcher"/>. The host owns the instances and flips
/// <see cref="IsVisible"/> as accounts come and go; the switcher animates the change.
/// </summary>
public sealed partial class WinoModeSwitcherItem : DependencyObject
{
    /// <summary>
    /// WinoIcons glyph shown while the mode is not selected.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Glyph { get; set; }

    /// <summary>
    /// WinoIcons glyph shown while the mode is selected, typically the Filled variant.
    /// Falls back to <see cref="Glyph"/> when empty.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SelectedGlyph { get; set; }

    /// <summary>
    /// Tooltip and screen-reader name of the mode.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Label { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AutomationId { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsVisible { get; set; }

    /// <summary>
    /// A pinned mode keeps a fixed size at the far end, behind a divider, no matter how
    /// many other modes are visible. Settings is pinned so it always has a way back.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsPinned { get; set; }

    /// <summary>
    /// Host-defined identity of the mode, for mapping the invoked item back to whatever
    /// the host navigates with.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial object? Tag { get; set; }
}

using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// One mode in a <see cref="WinoAppModeSwitcher"/>. The control never knows what the mode
/// is; the host supplies the icon and the wording and interprets the index it gets back.
/// </summary>
public sealed partial class WinoAppModeSwitcherItem : DependencyObject
{
    /// <summary>
    /// The glyph shown in the strip. It is reparented into the item container, so an
    /// instance belongs to exactly one switcher.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IconElement? Icon { get; set; }

    /// <summary>
    /// Optional icon shown while the switcher uses the light theme. When omitted,
    /// <see cref="Icon"/> is used for both themes.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IconElement? LightThemeIcon { get; set; }

    /// <summary>
    /// Tooltip and automation name for the item, e.g. "Mail".
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Label { get; set; }
}

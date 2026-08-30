using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// One mode in a <see cref="WinoAppModeSwitcher"/>. The control never knows what the mode
/// is; the host supplies the artwork and the wording and interprets the index it gets back.
///
/// An element rather than a plain <see cref="DependencyObject"/>, and it draws nothing: the
/// switcher parents it at zero size so that it sits in the tree. A theme resource is only
/// re-resolved on objects the framework can reach from the tree, and <see cref="Glyph"/> is
/// normally a theme resource - held outside it, every glyph would keep the artwork it was
/// parsed with for as long as the app ran.
/// </summary>
public sealed partial class WinoAppModeSwitcherItem : FrameworkElement
{
    /// <summary>
    /// The mode's artwork, normally one of the app icons from Themes/WinoAppIcons.xaml:
    ///
    ///     Glyph="{ThemeResource AppIconMail}"
    ///
    /// A <see cref="SvgImageSource"/> with a <c>UriSource</c> - which is what those resources
    /// are - is not shown as it arrives. The switcher takes the Uri back off it and recolours
    /// the markup so the blues follow the app accent, which a ready-made image cannot do.
    /// Any other <see cref="ImageSource"/> is shown as it is. Preferred over
    /// <see cref="Icon"/>, which follows neither the theme nor the accent.
    ///
    /// Theme is not this control's business: the theme resource names a different asset per
    /// theme, and XAML swaps it.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial ImageSource? Glyph { get; set; }

    /// <summary>
    /// The same mode drawn in a single ink, used while the switcher is monochrome. It is a
    /// separate asset rather than a recolouring of <see cref="Glyph"/>: the colour
    /// artwork reads by setting accent regions against paper ones, and collapsing that to one
    /// ink turns a card into an outline and a fill into a hole. Left unset, the item keeps its
    /// colour artwork whatever the switcher is doing.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Uri? MonochromeGlyphSource { get; set; }

    /// <summary>
    /// A ready-made glyph, for hosts that want something other than recoloured artwork. It
    /// is reparented into the item container, so an instance belongs to exactly one
    /// switcher. Ignored when <see cref="Glyph"/> is set.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IconElement? Icon { get; set; }

    /// <summary>
    /// Tooltip and automation name for the item, e.g. "Mail".
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Label { get; set; }
}

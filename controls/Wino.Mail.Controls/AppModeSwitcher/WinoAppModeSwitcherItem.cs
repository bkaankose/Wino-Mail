using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// One mode in a <see cref="WinoAppModeSwitcher"/>. The control never knows what the mode
/// is; the host supplies the artwork and the wording and interprets the index it gets back.
/// </summary>
public sealed partial class WinoAppModeSwitcherItem : DependencyObject
{
    /// <summary>
    /// An SVG asset the switcher recolours itself, so the glyph follows the app accent and
    /// takes the paper colour for the surface it is resting on. Preferred over
    /// <see cref="Icon"/>: a ready-made icon cannot do either.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Uri? GlyphSource { get; set; }

    /// <summary>
    /// The same mode drawn in a single ink, used while the switcher is monochrome. It is a
    /// separate asset rather than a recolouring of <see cref="GlyphSource"/>: the colour
    /// artwork reads by setting accent regions against paper ones, and collapsing that to one
    /// ink turns a card into an outline and a fill into a hole. Left unset, the item keeps its
    /// colour artwork whatever the switcher is doing.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Uri? MonochromeGlyphSource { get; set; }

    /// <summary>
    /// A ready-made glyph, for hosts that want something other than recoloured artwork. It
    /// is reparented into the item container, so an instance belongs to exactly one
    /// switcher. Ignored when <see cref="GlyphSource"/> is set.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IconElement? Icon { get; set; }

    /// <summary>
    /// Tooltip and automation name for the item, e.g. "Mail".
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Label { get; set; }
}

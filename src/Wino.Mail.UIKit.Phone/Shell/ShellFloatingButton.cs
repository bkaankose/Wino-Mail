using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>A tab's content page that handles the shell's floating action button itself (New event, New contact, New task).</summary>
internal interface IPhoneFloatingActionPage
{
    void FloatingActionInvoked();
}

/// <summary>
/// The floating action button of every iPhone tab (owner review of 18 Oct 2026, Android-style): a 56pt accent circle at
/// the bottom right above the tab bar with the mode's create glyph (New message, New event, New contact, New task). It
/// replaces the bars' "+" buttons and shows whenever the tab's root page is on screen.
/// </summary>
internal sealed class ShellFloatingButton : UIButton
{
    public const double Diameter = 56;

    public ShellFloatingButton()
    {
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        configuration.BaseBackgroundColor = WinoStyle.Accent;
        configuration.ContentInsets = NSDirectionalEdgeInsets.Zero;
        Configuration = configuration;
        Layer.ShadowColor = UIColor.Black.CGColor;
        Layer.ShadowOpacity = 0.22f;
        Layer.ShadowRadius = 10;
        Layer.ShadowOffset = new CGSize(0, 4);
        PointerInteractionEnabled = true;
    }

    /// <summary>Shows <paramref name="glyph"/> and announces <paramref name="label"/>.</summary>
    public void Set(WinoIconGlyph glyph, string label)
    {
        if (Configuration is { } configuration)
        {
            configuration.Image = WinoIcons.Image(glyph, 24, UIColor.White);
            configuration.BaseBackgroundColor = WinoStyle.Accent;
            Configuration = configuration;
        }
        AccessibilityLabel = label;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        Layer.ShadowPath = UIBezierPath.FromOval(Bounds).CGPath;
    }
}

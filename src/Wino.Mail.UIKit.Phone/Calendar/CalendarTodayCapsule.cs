using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The floating Today capsule of the calendar (calendar refresh of 14 Nov 2026): a 44pt material capsule at the bottom
/// leading edge, level with the shell's floating button, shown only while today is off screen. A chevron points to where
/// today is (left or right; up or down in Month). It fades and scales in and out.
/// </summary>
internal sealed class CalendarTodayCapsule : UIView
{
    public const double Height = 44;

    private readonly UIButton _button;
    private int _direction;
    private bool _vertical;
    private bool _shown;

    public CalendarTodayCapsule(Action tapped)
    {
        Hidden = true;
        Alpha = 0;
        Layer.ShadowColor = UIColor.Black.CGColor;
        Layer.ShadowOpacity = 0.14f;
        Layer.ShadowRadius = 10;
        Layer.ShadowOffset = new CGSize(0, 3);
        var material = new UIVisualEffectView(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemChromeMaterial))
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ClipsToBounds = true,
            UserInteractionEnabled = false
        };
        material.Layer.CornerRadius = (nfloat)(Height / 2);
        WinoLayout.Fill(material, this);
        _button = new UIButton { TranslatesAutoresizingMaskIntoConstraints = false };
        _button.PrimaryActionTriggered += (_, _) => tapped();
        _button.AccessibilityLabel = Translator.IOS_Calendar_BackToToday;
        WinoLayout.Fill(_button, this);
        WinoStyle.AccentWatchers.Add(this, static view => view.Apply());
        Apply();
    }

    public bool IsShown => _shown;

    public int Direction => _direction;

    /// <summary>
    /// Shows the capsule when <paramref name="direction"/> is not 0: -1 when today is before the visible range, +1 after
    /// it. <paramref name="vertical"/> picks the up and down chevrons of Month.
    /// </summary>
    public void Show(int direction, bool vertical)
    {
        bool show = direction != 0;
        if (show && (direction != _direction || vertical != _vertical))
        {
            _direction = direction;
            _vertical = vertical;
            Apply();
        }
        if (show == _shown) return;
        _shown = show;
        if (show) Hidden = false;
        bool reduce = UIAccessibility.IsReduceMotionEnabled;
        if (show && !reduce) Transform = CGAffineTransform.MakeScale(0.9f, 0.9f);
        UIView.Animate(0.2, 0, UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.AllowUserInteraction, () =>
        {
            Alpha = show ? 1 : 0;
            Transform = show || reduce ? CGAffineTransform.MakeIdentity() : CGAffineTransform.MakeScale(0.9f, 0.9f);
        }, () =>
        {
            if (!_shown) Hidden = true;
        });
    }

    public override CGSize IntrinsicContentSize
    {
        get
        {
            var size = _button.IntrinsicContentSize;
            return new CGSize(size.Width, Height);
        }
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        Layer.ShadowPath = UIBezierPath.FromRoundedRect(Bounds, (nfloat)(Height / 2)).CGPath;
    }

    private void Apply()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.AttributedTitle = new Foundation.NSAttributedString(Translator.Today, new UIStringAttributes { Font = WinoStyle.Fixed(15, UIFontWeight.Semibold), ForegroundColor = WinoStyle.Accent });
        var glyph = _vertical
            ? (_direction < 0 ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown)
            : (_direction < 0 ? WinoIconGlyph.ChevronLeft : WinoIconGlyph.ChevronRight);
        configuration.Image = WinoIcons.Image(glyph, 12, WinoStyle.Accent);
        configuration.ImagePadding = 6;
        configuration.ImagePlacement = _direction > 0 ? NSDirectionalRectEdge.Trailing : NSDirectionalRectEdge.Leading;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 16, 0, 16);
        _button.Configuration = configuration;
        InvalidateIntrinsicContentSize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}

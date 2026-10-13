using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Pad.Mail;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>
/// The iPad calendar content (Windows CalendarPage layout): the period surface in a Wino zone that fills the content
/// column, with a host view over it for the event composer shown in place. In a wide column the range title, the period
/// control and New event sit in the toolbar row; in a narrow one (the pad shell's narrow toolbar rule) the range and the
/// period control move into <see cref="Header"/>, a row at the top of the zone.
/// </summary>
public sealed class CalendarPage : UIView
{
    public static readonly CalendarDisplayType[] DisplayTypes =
        [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];

    private readonly UIStackView _headerRow;

    /// <summary>Segment titles, in <see cref="DisplayTypes"/> order.</summary>
    public static string[] Titles =>
        [Translator.CalendarTypeSelector_Day, Translator.CalendarTypeSelector_Week, Translator.CalendarTypeSelector_WorkWeek, Translator.CalendarTypeSelector_Month];

    public CalendarPage()
    {
        BackgroundColor = UIColor.Clear;
        CalendarZone = new WinoZoneView { CornerRadius = PadRenderingFrameHost.ZoneRadius };
        AddSubview(CalendarZone);

        Period = new UISegmentedControl(Titles);
        RangeLabel = WinoStyle.Label(string.Empty, WinoStyle.Scaled(17, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        RangeLabel.AccessibilityTraits = UIAccessibilityTrait.Header;
        HeaderRange = WinoStyle.Label(string.Empty, WinoStyle.Scaled(15, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        HeaderRange.AccessibilityTraits = UIAccessibilityTrait.Header;
        HeaderRange.LineBreakMode = UILineBreakMode.TailTruncation;
        HeaderPeriod = new UISegmentedControl(Titles);

        _headerRow = new UIStackView([HeaderRange, HeaderPeriod])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Leading,
            Spacing = 8,
            LayoutMarginsRelativeArrangement = true,
            DirectionalLayoutMargins = new NSDirectionalEdgeInsets(8, 12, 6, 12)
        };
        Header = _headerRow;
        Header.Hidden = true;
        SurfaceHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear };
        var column = new UIStackView([_headerRow, SurfaceHost]) { Axis = UILayoutConstraintAxis.Vertical };
        WinoLayout.Fill(column, CalendarZone.ContentView);

        ComposerHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true, ClipsToBounds = true, BackgroundColor = UIColor.Clear };
        WinoLayout.Fill(ComposerHost, CalendarZone.ContentView);
        var guide = SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            CalendarZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
            CalendarZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            CalendarZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, 0),
            CalendarZone.BottomAnchor.ConstraintEqualTo(BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
    }

    public WinoZoneView CalendarZone { get; }

    /// <summary>Where the surface and the readiness state go, below <see cref="Header"/>.</summary>
    public UIView SurfaceHost { get; }

    /// <summary>Covers the zone while the composer is open in place.</summary>
    public UIView ComposerHost { get; }

    /// <summary>The toolbar's period control (wide column).</summary>
    public UISegmentedControl Period { get; }

    /// <summary>The toolbar's range title (wide column).</summary>
    public UILabel RangeLabel { get; }

    /// <summary>The header at the top of the zone (narrow column): the range over the period control.</summary>
    public UIView Header { get; }

    public UILabel HeaderRange { get; }

    public UISegmentedControl HeaderPeriod { get; }

    /// <summary>Brings the composer host to the front once the surface is placed.</summary>
    public void RaiseComposerHost() => CalendarZone.ContentView.BringSubviewToFront(ComposerHost);

    /// <summary>Selects <paramref name="index"/> in both period controls.</summary>
    public void SelectPeriod(int index)
    {
        Period.SelectedSegment = index;
        HeaderPeriod.SelectedSegment = index;
    }

    /// <summary>Shows the range in the toolbar title and the header.</summary>
    public void SetRange(string text)
    {
        RangeLabel.Text = text;
        RangeLabel.SizeToFit();
        HeaderRange.Text = text;
    }
}

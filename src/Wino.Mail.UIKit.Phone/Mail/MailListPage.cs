using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// Root view of the phone message list (twin of the Mac MailListPage): the theme backdrop of the shell shows around one
/// rounded Wino zone that holds the list column. <see cref="Column"/> stacks the header bars over the table; floating
/// bars (undo, status) go into <see cref="Overlay"/>, pinned to the bottom of the zone.
/// </summary>
public sealed class MailListPage : UIView
{
    public MailListPage()
    {
        BackgroundColor = UIColor.Clear;
        ListZone = new WinoZoneView();
        AddSubview(ListZone);
        var guide = SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            ListZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, (nfloat)WinoStyle.ZoneGutter),
            ListZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            ListZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
            ListZone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);

        Column = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(Column, ListZone.ContentView);

        Overlay = WinoLayout.VStack(6);
        Overlay.Alignment = UIStackViewAlignment.Fill;
        ListZone.ContentView.AddSubview(Overlay);
        NSLayoutConstraint.ActivateConstraints(
        [
            Overlay.LeadingAnchor.ConstraintEqualTo(ListZone.ContentView.LeadingAnchor, 8),
            Overlay.TrailingAnchor.ConstraintEqualTo(ListZone.ContentView.TrailingAnchor, -8),
            Overlay.BottomAnchor.ConstraintEqualTo(ListZone.ContentView.BottomAnchor, -8)
        ]);
    }

    public WinoZoneView ListZone { get; }

    /// <summary>The header bars and the table, top to bottom.</summary>
    public UIStackView Column { get; }

    /// <summary>Bars floating over the bottom of the list (they never move the rows).</summary>
    public UIStackView Overlay { get; }
}

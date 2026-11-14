using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// Root view of the phone message list: one full-bleed surface (iOS refresh, 14 Nov 2026) under the bar and the floating
/// tab bar. <see cref="Column"/> stacks the header bars over the table from the safe-area top to the screen's bottom edge;
/// floating bars (undo, status) go into <see cref="Overlay"/>, above the tab bar.
/// </summary>
public sealed class MailListPage : UIView
{
    private const double FloatingButtonClearance = 16 + 56 + 12;
    private readonly NSLayoutConstraint _overlayTrailing;

    public MailListPage()
    {
        BackgroundColor = UIColor.Clear;
        Surface = new WinoPageSurfaceView();
        WinoLayout.Fill(Surface, this);

        Column = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(Column);
        var guide = SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            Column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            Column.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            Column.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            Column.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        Overlay = WinoLayout.VStack(6);
        Overlay.Alignment = UIStackViewAlignment.Fill;
        AddSubview(Overlay);
        _overlayTrailing = Overlay.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -12);
        NSLayoutConstraint.ActivateConstraints(
        [
            Overlay.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 12),
            _overlayTrailing,
            Overlay.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -8)
        ]);
    }

    public WinoPageSurfaceView Surface { get; }

    /// <summary>The header bars and the table, top to bottom.</summary>
    public UIStackView Column { get; }

    /// <summary>Bars floating over the bottom of the list (they never move the rows).</summary>
    public UIStackView Overlay { get; }

    /// <summary>Keeps the floating bars clear of the shell's action button while it shows.</summary>
    public bool AvoidsFloatingButton
    {
        get => _overlayTrailing.Constant < -12;
        set => _overlayTrailing.Constant = value ? -(nfloat)FloatingButtonClearance : -12;
    }
}

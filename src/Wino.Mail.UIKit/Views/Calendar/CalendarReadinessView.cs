using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>Blocked state shown instead of the grid (Windows ModeReadinessPanel; Mac CalendarReadinessView): title, message, progress.</summary>
public sealed class CalendarReadinessView : UIView
{
    private readonly UILabel _title;
    private readonly UILabel _message;
    private readonly UIActivityIndicatorView _progress;

    public CalendarReadinessView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(WinoIconGlyph.Calendar, 40, WinoStyle.TertiaryText);
        _title = WinoStyle.Label(string.Empty, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        _title.TextAlignment = UITextAlignment.Center;
        _message = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _message.TextAlignment = UITextAlignment.Center;
        _progress = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false, HidesWhenStopped = true };
        var stack = WinoLayout.VStack(10, icon, _title, _message, _progress);
        stack.Alignment = UIStackViewAlignment.Center;
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(420),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 20),
            stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -20)
        ]);
    }

    public void Update(string? title, string? message, bool progress)
    {
        _title.Text = title ?? string.Empty;
        _message.Text = message ?? string.Empty;
        if (progress) _progress.StartAnimating(); else _progress.StopAnimating();
    }
}

using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

public sealed class WinoSourceChoiceCard : WinoPressableView
{
    private readonly NSTextField _title;
    private readonly NSTextField _description;
    private bool _isSelected;

    public WinoSourceChoiceCard(string title, string? description, WinoIconGlyph glyph)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        CornerRadius = WinoSettingsStyle.CardRadius;
        Fill = WinoSettingsStyle.CardFill;
        Stroke = WinoSettingsStyle.CardStroke;
        StrokeWidth = 1;

        var tile = new WinoSettingsIconTile(glyph);
        _title = WinoStyle.Label(title, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
        _description = WinoStyle.Label(description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        _description.Hidden = string.IsNullOrEmpty(description);

        var stack = WinoLayout.VStack(8, tile, _title, _description);
        stack.Alignment = NSLayoutAttribute.Leading;
        AddSubview(stack);
        _title.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        _description.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;

        var padding = (nfloat)WinoSettingsStyle.CardPadding;
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, padding),
            stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -padding),
            stack.TopAnchor.ConstraintEqualTo(TopAnchor, padding),
            stack.BottomAnchor.ConstraintEqualTo(BottomAnchor, -padding)
        ]);

        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        AccessibilityLabel = title;
        AccessibilityValue = new Foundation.NSNumber(0);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Stroke = value ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            StrokeWidth = value ? 2 : 1;
            AccessibilityValue = new Foundation.NSNumber(value ? 1 : 0);
        }
    }
}

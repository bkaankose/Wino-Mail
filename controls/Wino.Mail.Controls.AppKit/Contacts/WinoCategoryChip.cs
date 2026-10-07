using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Entities.Mail;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Contacts;

/// <summary>
/// Category chip used in contact rows and the contact detail pane (Windows
/// ContactCategoryChipTemplate: radius 4, padding 6×1, caption text). The chip is tinted with the
/// category colour at low alpha and writes its name in that colour so it reads in light and dark.
/// </summary>
public sealed class WinoCategoryChip : WinoSurfaceView
{
    private readonly NSTextField _label;

    public WinoCategoryChip(MailCategory category)
    {
        CornerRadius = 4;
        var color = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.Accent;
        Fill = color.ColorWithAlphaComponent((nfloat)0.16);
        _label = WinoStyle.Label(category.Name, WinoStyle.Caption, color);
        _label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        WinoLayout.Fill(_label, this, 1, 6, 1, 6);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        AccessibilityLabel = category.Name;
    }

    public override CGSize IntrinsicContentSize
    {
        get
        {
            var size = _label.IntrinsicContentSize;
            return new CGSize(size.Width + 12, size.Height + 2);
        }
    }

    /// <summary>A horizontal chip row (4 pt gaps) for the given categories; empty when there are none.</summary>
    public static NSStackView Row(IEnumerable<MailCategory>? categories)
    {
        var row = WinoLayout.HStack(4);
        row.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        if (categories is null) return row;
        foreach (var category in categories) row.AddArrangedSubview(new WinoCategoryChip(category));
        return row;
    }
}

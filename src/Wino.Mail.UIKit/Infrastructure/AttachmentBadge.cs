using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Attachments;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

internal static class AttachmentBadge
{
    public static UIView Create(AttachmentViewModelBase attachment, double size)
    {
        var badge = new WinoSurfaceView { Fill = WinoStyle.Hex(attachment.AccentColor), CornerRadius = Math.Round(size / 6) };
        WinoLayout.Size(badge, size, size);
        badge.UserInteractionEnabled = false;
        badge.AccessibilityElementsHidden = true;
        badge.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        badge.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Vertical);

        UIView content;
        if (string.IsNullOrEmpty(attachment.FileExtensionLabel))
        {
            content = new WinoIconView(WinoIconGlyph.Attachment, Math.Round(size * 0.5), UIColor.White) { Colorful = false };
        }
        else
        {
            content = WinoStyle.Label(attachment.FileExtensionLabel, WinoStyle.Fixed(Math.Round(size * 0.28), UIFontWeight.Bold), UIColor.White);
        }
        badge.AddSubview(content);
        content.CenterXAnchor.ConstraintEqualTo(badge.CenterXAnchor).Active = true;
        content.CenterYAnchor.ConstraintEqualTo(badge.CenterYAnchor).Active = true;
        if (content is UILabel label)
        {
            label.AdjustsFontForContentSizeCategory = false;
            label.AdjustsFontSizeToFitWidth = true;
            label.MinimumScaleFactor = 0.6f;
            label.TextAlignment = UITextAlignment.Center;
            label.WidthAnchor.ConstraintLessThanOrEqualTo(badge.WidthAnchor, 1, -4).Active = true;
        }
        return badge;
    }
}

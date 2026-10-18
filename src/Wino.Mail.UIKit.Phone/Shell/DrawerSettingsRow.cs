using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// The Settings row pinned at the foot of every drawer menu (<see cref="ShellDrawer"/>): gear glyph, title and chevron,
/// 50pt, under a hairline. Settings opens as a full-screen sheet once the drawer has closed.
/// </summary>
internal static class DrawerSettingsRow
{
    public const double Height = 50;

    public static UIView Create(Action open)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.TemplateImage(WinoIconGlyph.Settings, 20);
        configuration.ImagePadding = 12;
        configuration.Title = Translator.MenuSettings;
        configuration.BaseForegroundColor = UIColor.Label;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 25, 0, 44);
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        button.PrimaryActionTriggered += (_, _) => open();

        var chevron = new WinoIconView(WinoIconGlyph.ChevronRight, 14, WinoStyle.SecondaryText)
        {
            UserInteractionEnabled = false,
            IsAccessibilityElement = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var hairline = new UIView { BackgroundColor = WinoStyle.Separator, TranslatesAutoresizingMaskIntoConstraints = false };

        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        row.AddSubview(button);
        row.AddSubview(chevron);
        row.AddSubview(hairline);
        NSLayoutConstraint.ActivateConstraints(
        [
            row.HeightAnchor.ConstraintEqualTo((nfloat)Height),
            hairline.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            hairline.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 16),
            hairline.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -16),
            hairline.HeightAnchor.ConstraintEqualTo(1 / UIScreen.MainScreen.Scale),
            button.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            button.BottomAnchor.ConstraintEqualTo(row.BottomAnchor),
            button.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            button.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            chevron.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor),
            chevron.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -20)
        ]);
        return row;
    }
}

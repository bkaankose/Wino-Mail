using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views;

/// <summary>
/// Welcome screen (design board "Onboarding · Welcome", Mac WelcomePageV2): the Wino Mail logo, title, one-line
/// description, four feature tiles with WinoIcons glyphs, the restore links and the primary "add account" button.
/// The theme backdrop fills the screen; the content sits in one Wino zone, a centred column on iPad.
/// </summary>
public sealed class WelcomePageV2 : UIView
{
    public const string LogoResource = "WinoLogo.png";
    private const double ColumnWidth = 520;

    private readonly List<UILabel> _wrapping = new();

    public WelcomePageV2(UIButton getStarted, UIButton importAccount, UIButton importFile, UILabel status)
    {
        var backdrop = UIKitThemeService.Track(new ThemeBackdropView());
        WinoLayout.Fill(backdrop, this);

        var logo = new UIImageView(LoadLogo())
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            TranslatesAutoresizingMaskIntoConstraints = false,
            IsAccessibilityElement = true,
            AccessibilityLabel = "Wino Mail",
            AccessibilityTraits = UIAccessibilityTrait.Image
        };
        WinoLayout.Size(logo, 88, 88);

        var title = WinoStyle.Label(Translator.WelcomeWindow_Title, WinoStyle.PageTitle, WinoStyle.PrimaryText, 0);
        title.TextAlignment = UITextAlignment.Center;
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var description = WinoStyle.Label(Translator.WelcomeWindow_GetStartedDescription, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        description.TextAlignment = UITextAlignment.Center;
        _wrapping.AddRange([title, description, status]);

        var tiles = WinoLayout.VStack(WinoStyle.Space3,
            Tile(WinoIconGlyph.Mail, WinoStyle.Hex(0x0F6CBD), Translator.MacOS_Welcome_AccountsTitle, Translator.MacOS_Welcome_AccountsDescription),
            Tile(WinoIconGlyph.LockClosed, WinoStyle.Hex(0x2F9E63), Translator.MacOS_Welcome_PrivacyTitle, Translator.MacOS_Welcome_PrivacyDescription),
            Tile(WinoIconGlyph.Sparkle, WinoStyle.Hex(0x6C5CE7), Translator.MacOS_Welcome_IntelligenceTitle, Translator.MacOS_Welcome_IntelligenceDescription),
            Tile(WinoIconGlyph.PaintBrush, WinoStyle.Hex(0xE58E26), Translator.MacOS_Welcome_ThemesTitle, Translator.MacOS_Welcome_ThemesDescription));
        tiles.Alignment = UIStackViewAlignment.Fill;

        status.Font = WinoStyle.Description;
        status.TextColor = WinoStyle.SecondaryText;
        status.TextAlignment = UITextAlignment.Center;
        status.Lines = 0;

        var links = WinoLayout.VStack(0, importAccount, importFile);
        links.Alignment = UIStackViewAlignment.Center;

        // The logo is centred in its own holder so the text rows can fill the column width and wrap.
        var logoHolder = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        logoHolder.AddSubview(logo);
        NSLayoutConstraint.ActivateConstraints(
        [
            logo.CenterXAnchor.ConstraintEqualTo(logoHolder.CenterXAnchor),
            logo.TopAnchor.ConstraintEqualTo(logoHolder.TopAnchor),
            logo.BottomAnchor.ConstraintEqualTo(logoHolder.BottomAnchor)
        ]);
        var heading = WinoLayout.VStack(WinoStyle.Space2, logoHolder, title, description);
        heading.Alignment = UIStackViewAlignment.Fill;
        var column = WinoLayout.VStack(WinoStyle.Space6, heading, tiles, status, getStarted, links);
        column.Alignment = UIStackViewAlignment.Fill;
        column.SetCustomSpacing((nfloat)WinoStyle.Space3, getStarted);
        getStarted.HeightAnchor.ConstraintGreaterThanOrEqualTo(50).Active = true;

        var zone = new WinoZoneView { TranslatesAutoresizingMaskIntoConstraints = false };
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
        WinoLayout.Fill(scroll, zone.ContentView);
        // A container at least as tall as the zone keeps short content in the middle; long content scrolls.
        var container = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        scroll.AddSubview(container);
        container.AddSubview(column);
        var content = scroll.ContentLayoutGuide;
        var frame = scroll.FrameLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            container.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            container.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            container.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor),
            container.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor),
            container.HeightAnchor.ConstraintGreaterThanOrEqualTo(frame.HeightAnchor),
            column.LeadingAnchor.ConstraintEqualTo(container.LeadingAnchor, 24),
            column.TrailingAnchor.ConstraintEqualTo(container.TrailingAnchor, -24),
            column.CenterYAnchor.ConstraintEqualTo(container.CenterYAnchor),
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(container.TopAnchor, 32),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(container.BottomAnchor, -24),
        ]);
        // Without content pressure the container takes the zone's height exactly.
        var snug = container.HeightAnchor.ConstraintEqualTo(frame.HeightAnchor);
        snug.Priority = 250;
        snug.Active = true;

        AddSubview(zone);
        var guide = SafeAreaLayoutGuide;
        var width = zone.WidthAnchor.ConstraintEqualTo((nfloat)ColumnWidth);
        width.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 12),
            zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -12),
            zone.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
            zone.LeadingAnchor.ConstraintGreaterThanOrEqualTo(guide.LeadingAnchor, 12),
            zone.TrailingAnchor.ConstraintLessThanOrEqualTo(guide.TrailingAnchor, -12),
            width
        ]);
    }

    /// <summary>
    /// Multi-line labels inside a scrolling stack measure their height with a stale width on the first pass (the
    /// description lost its second line on iPad); a second pass with the laid-out width fixes the height.
    /// </summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        bool changed = false;
        foreach (var label in _wrapping)
        {
            var width = label.Bounds.Width;
            if (width > 0 && Math.Abs(label.PreferredMaxLayoutWidth - width) > 0.5)
            {
                label.PreferredMaxLayoutWidth = width;
                changed = true;
            }
        }
        if (changed) base.LayoutSubviews();
    }

    /// <summary>The Wino Mail logo bundled as WinoLogo.png (src/Wino.Mail.WinUI/Assets/WinoLogo.png).</summary>
    public static UIImage? LoadLogo()
    {
        var path = Path.Combine(NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory, LogoResource);
        return File.Exists(path) ? UIImage.FromFile(path) : null;
    }

    /// <summary>A feature row: the glyph on a coloured badge, the title and a wrapping description.</summary>
    private static UIView Tile(WinoIconGlyph glyph, UIColor color, string title, string detail)
    {
        var badge = new WinoSurfaceView { Fill = color, CornerRadius = 8 };
        WinoLayout.Size(badge, 34, 34);
        var icon = new WinoIconView(glyph, 18, UIColor.White) { Colorful = false, TranslatesAutoresizingMaskIntoConstraints = false };
        badge.AddSubview(icon);
        icon.CenterXAnchor.ConstraintEqualTo(badge.CenterXAnchor).Active = true;
        icon.CenterYAnchor.ConstraintEqualTo(badge.CenterYAnchor).Active = true;

        var text = WinoLayout.VStack(2, WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0),
            WinoStyle.Label(detail, WinoStyle.Description, WinoStyle.SecondaryText, 0));
        text.Alignment = UIStackViewAlignment.Fill;
        var row = WinoLayout.HStack(WinoStyle.Space3, badge, text);
        row.Alignment = UIStackViewAlignment.Center;

        var surface = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.GroupStroke, CornerRadius = 10 };
        WinoLayout.Fill(row, surface, 12);
        surface.IsAccessibilityElement = true;
        surface.AccessibilityLabel = $"{title}, {detail}";
        return surface;
    }
}

using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views;

/// <summary>
/// Welcome screen (design board "Onboarding · Welcome", Mac WelcomePageV2): the Wino Mail logo, title, one-line
/// description, the restore links and the primary "add account" button.
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
        var column = WinoLayout.VStack(WinoStyle.Space6, heading, status, getStarted, links);
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
}

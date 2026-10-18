using AppKit;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Welcome screen (design board "Onboarding · Welcome"): app icon, title, one-line description
/// and a footer with the restore actions and the primary "add account" button.
/// </summary>
public sealed class WelcomePageV2 : NSView
{
    public WelcomePageV2(NSButton getStarted, NSButton importAccount, NSButton importFile, NSTextField status)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        var icon = new NSImageView
        {
            Image = NSApplication.SharedApplication.ApplicationIconImage,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(icon, 96, 96);

        var title = WinoStyle.Label(Translator.WelcomeWindow_Title, NSFont.SystemFontOfSize(28, NSFontWeight.Bold));
        title.Alignment = NSTextAlignment.Center;
        var description = WinoStyle.Label(Translator.WelcomeWindow_GetStartedDescription, NSFont.SystemFontOfSize(14), WinoStyle.SecondaryText, 0);
        description.Alignment = NSTextAlignment.Center;
        description.PreferredMaxLayoutWidth = (nfloat)HeroTextWidth;
        // A fixed width keeps the wrapped lines centred under the title instead of hugging the longest line.
        description.WidthAnchor.ConstraintEqualTo((nfloat)HeroTextWidth).Active = true;

        status.Font = WinoStyle.Description;
        status.TextColor = WinoStyle.SecondaryText;
        status.Alignment = NSTextAlignment.Center;
        status.MaximumNumberOfLines = 0;
        status.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        status.PreferredMaxLayoutWidth = (nfloat)HeroTextWidth;

        // Every hero row is centred on the same axis: the inner title stack must centre too,
        // because WinoLayout.VStack defaults to leading alignment.
        var heading = WinoLayout.VStack(6, title, description);
        heading.Alignment = NSLayoutAttribute.CenterX;
        var hero = WinoLayout.VStack(WinoStyle.Space4, icon, heading, status);
        hero.Alignment = NSLayoutAttribute.CenterX;
        hero.SetCustomSpacing((nfloat)WinoStyle.Space6, heading);

        foreach (var link in new[] { importAccount, importFile }) StyleAsLink(link);
        getStarted.KeyEquivalent = "\r";
        getStarted.ControlSize = NSControlSize.Large;

        // Native footer: no fill of its own (the window background shows through in light and dark),
        // a hairline separator on top, links leading and the primary button trailing.
        var footer = WinoLayout.HStack(WinoStyle.Space4, importAccount, importFile, WinoLayout.Spacer(), getStarted);
        footer.EdgeInsets = new NSEdgeInsets(16, 20, 16, 20);
        var separator = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };

        var center = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        center.AddSubview(hero);
        var centerY = hero.CenterYAnchor.ConstraintEqualTo(center.CenterYAnchor, 10);
        centerY.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            hero.CenterXAnchor.ConstraintEqualTo(center.CenterXAnchor),
            centerY,
            hero.LeadingAnchor.ConstraintGreaterThanOrEqualTo(center.LeadingAnchor, 48),
            hero.TopAnchor.ConstraintGreaterThanOrEqualTo(center.TopAnchor, 40),
            hero.BottomAnchor.ConstraintLessThanOrEqualTo(center.BottomAnchor, -24)
        ]);

        AddSubview(center);
        AddSubview(separator);
        AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(
        [
            center.TopAnchor.ConstraintEqualTo(TopAnchor),
            center.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            center.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            center.BottomAnchor.ConstraintEqualTo(separator.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(footer.TopAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    private const double HeroTextWidth = 460;

    private static void StyleAsLink(NSButton button)
    {
        button.Bordered = false;
        button.BezelStyle = NSBezelStyle.Inline;
        button.ContentTintColor = NSColor.Link;
        button.AttributedTitle = new Foundation.NSAttributedString(button.Title, new AppKit.NSStringAttributes
        {
            ForegroundColor = NSColor.Link,
            Font = WinoStyle.Body
        });
    }
}

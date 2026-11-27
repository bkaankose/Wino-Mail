using AppKit;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Welcome screen (design board "Onboarding · Welcome"): app icon, title and subtitle, the primary "Get started" button
/// directly under them, then the restore and import options as links under a caption. The import status sits under
/// the links. The window keeps at least 640 pt, so the links always fit on one row.
/// </summary>
public sealed class WelcomePageV2 : NSView
{
    public WelcomePageV2(NSButton getStarted, IReadOnlyList<NSButton> restoreLinks, NSTextField status)
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
        var subtitle = WinoStyle.Label(Translator.Android_Welcome_Tagline, NSFont.SystemFontOfSize(14), WinoStyle.SecondaryText, 0);
        subtitle.Alignment = NSTextAlignment.Center;

        var caption = WinoStyle.Label(Translator.WelcomeWindow_RestoreOrImportCaption, NSFont.SystemFontOfSize(13), WinoStyle.SecondaryText);
        caption.Alignment = NSTextAlignment.Center;

        status.Font = WinoStyle.Description;
        status.TextColor = WinoStyle.SecondaryText;
        status.Alignment = NSTextAlignment.Center;
        status.MaximumNumberOfLines = 0;
        status.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        status.PreferredMaxLayoutWidth = (nfloat)HeroTextWidth;

        foreach (var link in restoreLinks) StyleAsLink(link);
        getStarted.KeyEquivalent = "\r";
        getStarted.ControlSize = NSControlSize.Large;

        // Every row is centred on the same axis: the inner title stack must centre too, because
        // WinoLayout.VStack defaults to leading alignment.
        var heading = WinoLayout.VStack(6, title, subtitle);
        heading.Alignment = NSLayoutAttribute.CenterX;
        var links = WinoLayout.HStack(WinoStyle.Space4, restoreLinks.ToArray());
        var column = WinoLayout.VStack(WinoStyle.Space4, icon, heading, getStarted, caption, links, status);
        column.Alignment = NSLayoutAttribute.CenterX;
        column.SetCustomSpacing((nfloat)WinoStyle.Space6, heading);
        column.SetCustomSpacing((nfloat)WinoStyle.Space6, getStarted);
        column.SetCustomSpacing((nfloat)WinoStyle.Space2, caption);

        var center = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        center.AddSubview(column);
        var centerY = column.CenterYAnchor.ConstraintEqualTo(center.CenterYAnchor, 10);
        centerY.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.CenterXAnchor.ConstraintEqualTo(center.CenterXAnchor),
            centerY,
            column.LeadingAnchor.ConstraintGreaterThanOrEqualTo(center.LeadingAnchor, 48),
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(center.TopAnchor, 40),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(center.BottomAnchor, -24)
        ]);

        AddSubview(center);
        NSLayoutConstraint.ActivateConstraints(
        [
            center.TopAnchor.ConstraintEqualTo(TopAnchor),
            center.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            center.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            center.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    private const double HeroTextWidth = 520;

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

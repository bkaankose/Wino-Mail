using AppKit;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Welcome screen (design board "Onboarding · Welcome"): app icon, title, one-line description,
/// four feature tiles and a footer with the restore actions and the primary "add account" button.
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

        // Feature tiles. No shared strings exist for these, so they are English literals.
        var tiles = NSGridView.Create(
        [
            [Tile("envelope.fill", WinoStyle.Hex(0x0F6CBD), "All your accounts", "Outlook, Gmail, iCloud, Yahoo and IMAP"),
             Tile("lock.fill", WinoStyle.Hex(0x2F9E63), "Private by design", "Credentials live in your Keychain")],
            [Tile("sparkles", WinoStyle.Hex(0x6C5CE7), "On-device intelligence", "Summaries and a daily briefing"),
             Tile("paintbrush.fill", WinoStyle.Hex(0xE58E26), "Make it yours", "Wino themes, native light and dark")]
        ]);
        tiles.TranslatesAutoresizingMaskIntoConstraints = false;
        tiles.RowSpacing = 10;
        tiles.ColumnSpacing = 10;
        // Fill both axes so every card in a row shares the tallest card's height.
        tiles.X = NSGridCellPlacement.Fill;
        tiles.Y = NSGridCellPlacement.Fill;

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
        var hero = WinoLayout.VStack(WinoStyle.Space4, icon, heading, tiles, status);
        hero.Alignment = NSLayoutAttribute.CenterX;
        hero.SetCustomSpacing((nfloat)WinoStyle.Space6, heading);
        hero.SetCustomSpacing((nfloat)WinoStyle.Space6, tiles);

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
    private const double TileWidth = 255;
    private const double TileBadge = 30;
    private const double TilePadding = 12;

    private static NSView Tile(string symbol, NSColor color, string title, string detail)
    {
        var badge = new WinoSurfaceView { Fill = color, CornerRadius = 8 };
        WinoLayout.Size(badge, TileBadge, TileBadge);
        var glyph = WinoStyle.SymbolView(symbol, null, 14, NSColor.White);
        badge.AddSubview(glyph);
        glyph.CenterXAnchor.ConstraintEqualTo(badge.CenterXAnchor).Active = true;
        glyph.CenterYAnchor.ConstraintEqualTo(badge.CenterYAnchor).Active = true;

        // The detail wraps to two lines inside the fixed card width rather than truncating.
        var textWidth = TileWidth - TilePadding * 2 - TileBadge - WinoStyle.Space3;
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong);
        var detailLabel = WinoStyle.Label(detail, WinoStyle.Description, WinoStyle.SecondaryText, 2);
        detailLabel.PreferredMaxLayoutWidth = (nfloat)textWidth;
        var text = WinoLayout.VStack(2, titleLabel, detailLabel);
        text.WidthAnchor.ConstraintEqualTo((nfloat)textWidth).Active = true;

        // Top-aligned so the badge lines up with the title line however many detail lines follow.
        var row = WinoLayout.HStack(WinoStyle.Space3, badge, text);
        row.Alignment = NSLayoutAttribute.Top;
        row.EdgeInsets = new NSEdgeInsets(10, (nfloat)TilePadding, 10, (nfloat)TilePadding);

        var surface = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.GroupStroke, CornerRadius = 9 };
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        surface.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            row.LeadingAnchor.ConstraintEqualTo(surface.LeadingAnchor),
            row.TrailingAnchor.ConstraintEqualTo(surface.TrailingAnchor),
            row.TopAnchor.ConstraintEqualTo(surface.TopAnchor),
            row.BottomAnchor.ConstraintLessThanOrEqualTo(surface.BottomAnchor)
        ]);
        // Hug the content, but let the grid stretch a shorter card to its row's height.
        var hug = row.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor);
        hug.Priority = 250;
        hug.Active = true;
        surface.WidthAnchor.ConstraintEqualTo((nfloat)TileWidth).Active = true;
        return surface;
    }

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

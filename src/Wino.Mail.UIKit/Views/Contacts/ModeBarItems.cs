using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// Bar and menu helpers of the Contacts and To Do pages on both idioms. Bar glyphs are drawn straight from the WinoIcons
/// font into a template image of the requested size (the K report's workaround for the oversized
/// <c>WinoIcons.MonoImage</c> template images in bars).
/// </summary>
internal static class ModeBarItems
{
    public static UIImage Glyph(WinoIconGlyph glyph, double size = 22)
    {
        var format = UIGraphicsImageRendererFormat.DefaultFormat;
        format.Opaque = false;
        var renderer = new UIGraphicsImageRenderer(new CGSize(size, size), format);
        var image = renderer.CreateImage(_ => WinoIcons.Draw(WinoIcons.Glyph(glyph), new CGRect(0, 0, size, size), UIColor.Black, null, false));
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
    }

    /// <summary>A glyph in a fixed colour at its real size (state glyphs such as the favourite star).</summary>
    public static UIImage Tinted(WinoIconGlyph glyph, double size, UIColor color)
        => Glyph(glyph, size).ApplyTintColor(color, UIImageRenderingMode.AlwaysOriginal);

    /// <summary>The favourite star: filled in the caution colour, otherwise a tertiary outline.</summary>
    public static UIImage Star(bool on, double size)
        => Tinted(on ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, size, on ? WinoStyle.Caution : WinoStyle.TertiaryText);

    public static UIBarButtonItem Button(WinoIconGlyph glyph, string label, Action action)
    {
        var item = new UIBarButtonItem(Glyph(glyph), UIBarButtonItemStyle.Plain, (_, _) => action());
        item.AccessibilityLabel = label;
        return item;
    }

    public static UIBarButtonItem MenuButton(WinoIconGlyph glyph, string label, UIMenu menu)
    {
        var item = new UIBarButtonItem(Glyph(glyph), menu);
        item.AccessibilityLabel = label;
        return item;
    }

    public static UIAction Action(string title, WinoIconGlyph glyph, Action run, bool enabled = true, bool destructive = false, bool on = false)
    {
        var action = UIAction.Create(title, glyph == WinoIconGlyph.None ? null : Glyph(glyph, 18), null, _ => run());
        if (!enabled) action.Attributes |= UIMenuElementAttributes.Disabled;
        if (destructive) action.Attributes |= UIMenuElementAttributes.Destructive;
        if (on) action.State = UIMenuElementState.On;
        return action;
    }

    public static UIMenu Inline(params UIMenuElement[] children)
        => UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, children);

    public static UIMenu Submenu(string title, WinoIconGlyph glyph, params UIMenuElement[] children)
        => UIMenu.Create(title, glyph == WinoIconGlyph.None ? null : Glyph(glyph, 18), UIMenuIdentifier.None, 0, children);

    public static UIContextualAction Swipe(string title, WinoIconGlyph glyph, UIColor color, Action run, UIContextualActionStyle style = UIContextualActionStyle.Normal)
    {
        var action = UIContextualAction.FromContextualActionStyle(style, title, (_, _, done) =>
        {
            run();
            done(true);
        });
        action.BackgroundColor = color;
        action.Image = Glyph(glyph, 20);
        return action;
    }

    /// <summary>
    /// Appends <paramref name="items"/> after the bar items the shell already placed (the phone shell's avatar), so a
    /// page never removes a shell item.
    /// </summary>
    public static void AppendRight(UINavigationItem navigationItem, params UIBarButtonItem[] items)
    {
        var existing = navigationItem.RightBarButtonItems ?? [];
        navigationItem.RightBarButtonItems = existing.Concat(items.Where(item => Array.IndexOf(existing, item) < 0)).ToArray();
    }

    /// <summary>The shell the page lives in, through the router (null while the shell is not up).</summary>
    public static UIKitShellViewController? Shell(UIKitNavigationService navigation) => navigation.Shell;
}

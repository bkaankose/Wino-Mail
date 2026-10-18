using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>Bar button items and menu images of the phone Mail flow, drawn with WinoIcons (never SF Symbols).</summary>
internal static class PhoneBarItems
{
    /// <summary>Glyph size of the command row buttons.</summary>
    public const double RowGlyphSize = 26;

    /// <summary>A command button for <see cref="Row"/>: a 44pt touch target with a WinoIcons glyph.</summary>
    public static UIButton RowButton(WinoIconGlyph glyph, string label, Action? action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.TemplateImage(glyph, RowGlyphSize);
        configuration.ContentInsets = new NSDirectionalEdgeInsets(8, 8, 8, 8);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.WidthAnchor.ConstraintEqualTo(48).Active = true;
        button.HeightAnchor.ConstraintEqualTo(44).Active = true;
        button.AccessibilityLabel = label;
        // A null action makes a menu button; the owner sets its Menu.
        if (action is null) button.ShowsMenuAsPrimaryAction = true;
        else button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Toolbar items holding <paramref name="buttons"/> as one evenly spaced row. Separate image items overflow a compact
    /// iPhone toolbar (iOS 26 gives each its own wide slot and moves the rest into an overflow menu).
    /// </summary>
    public static UIBarButtonItem[] Row(UIButton[] buttons)
    {
        var row = new UIStackView(buttons)
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.EqualSpacing,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 4
        };
        row.Frame = new CoreGraphics.CGRect(0, 0, buttons.Length * 48 + Math.Max(0, buttons.Length - 1) * 4, 44);
        return [UIBarButtonItem.FlexibleSpaceItem, new UIBarButtonItem(row), UIBarButtonItem.FlexibleSpaceItem];
    }

    /// <summary>
    /// Puts <paramref name="search"/> behind a search glyph in the bar of <paramref name="item"/> on every tab (approach A
    /// of 18 Oct 2026): the field expands in place over the bar when tapped and costs no row at rest. iOS 26 does this
    /// natively (integrated button, kept out of the bottom toolbar); earlier versions hide the field until pulled down.
    /// </summary>
    public static void BarSearch(UINavigationItem item, UISearchController search)
    {
        item.SearchController = search;
        if (OperatingSystem.IsIOSVersionAtLeast(26))
        {
            item.PreferredSearchBarPlacement = UINavigationItemSearchBarPlacement.IntegratedButton;
            item.SearchBarPlacementAllowsToolbarIntegration = false;
        }
        else item.HidesSearchBarWhenScrolling = true;
    }

    /// <summary>The New Message button of Mailboxes and the message list.</summary>
    public static UIBarButtonItem Compose(Action compose) => Button(WinoIconGlyph.NewMail, Translator.MenuNewMail, compose);

    /// <summary>A glyph button for a bar.</summary>
    public static UIBarButtonItem Button(WinoIconGlyph glyph, string label, Action action)
    {
        var item = new UIBarButtonItem(WinoIcons.TemplateImage(glyph, 22), UIBarButtonItemStyle.Plain, (_, _) => action());
        item.AccessibilityLabel = label;
        return item;
    }

    /// <summary>A glyph button that opens <paramref name="menu"/> on tap.</summary>
    public static UIBarButtonItem MenuButton(WinoIconGlyph glyph, string label, UIMenu menu)
    {
        var item = new UIBarButtonItem(WinoIcons.TemplateImage(glyph, 22), menu);
        item.AccessibilityLabel = label;
        return item;
    }

    /// <summary>A menu action with a WinoIcons image.</summary>
    public static UIAction Action(string title, WinoIconGlyph glyph, Action run, bool enabled = true, bool destructive = false, bool on = false)
    {
        var action = UIAction.Create(title, glyph == WinoIconGlyph.None ? null : WinoIcons.TemplateImage(glyph, 18), null, _ => run());
        if (!enabled) action.Attributes |= UIMenuElementAttributes.Disabled;
        if (destructive) action.Attributes |= UIMenuElementAttributes.Destructive;
        if (on) action.State = UIMenuElementState.On;
        return action;
    }

    /// <summary>An untitled inline group (the separators of a UIMenu).</summary>
    public static UIMenu Inline(params UIMenuElement[] children)
        => UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, children);
}

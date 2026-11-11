using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Small views shared by the Wino Intelligence home page and its sub-pages.</summary>
internal static class IntelligenceSettingsViews
{
    public const string LearnMoreUrl = "https://www.winomail.app/intelligence";

    /// <summary>A rounded status pill (Active, Limit reached, New).</summary>
    public static NSView Pill(string text, NSColor textColor, NSColor fill, NSColor? stroke = null)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, textColor);
        var pill = new WinoSurfaceView { Fill = fill, Stroke = stroke, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, pill, 2, 10, 2, 10);
        pill.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);
        pill.AccessibilityElement = true;
        pill.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        pill.AccessibilityLabel = text;
        label.AccessibilityElement = false;
        return pill;
    }

    public static NSView SuccessPill(string text)
        => Pill(text, WinoStyle.Success, WinoStyle.Dynamic(WinoStyle.Hex(0xDFF6DD), WinoStyle.Hex(0x393D1B)));

    public static NSView CriticalPill(string text)
        => Pill(text, WinoStyle.Critical, WinoStyle.Dynamic(WinoStyle.Hex(0xFDE7E9), WinoStyle.Hex(0x442726)));

    public static NSView AccentPill(string text)
        => Pill(text, NSColor.White, WinoStyle.Accent);

    /// <summary>The app icon at <paramref name="size"/> points (Windows StoreLogo).</summary>
    public static NSImageView AppLogo(double size)
    {
        var image = new NSImageView
        {
            Image = NSImage.ImageNamed("AppIcon") ?? NSApplication.SharedApplication.ApplicationIconImage,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(image, size, size);
        image.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        return image;
    }

    /// <summary>Restyles a push button as an accent text link (Windows HyperlinkButton).</summary>
    public static T Link<T>(T button, string title) where T : NSButton
    {
        button.Bordered = false;
        button.SetButtonType(NSButtonType.MomentaryChange);
        button.AttributedTitle = new NSAttributedString(title, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = NSFont.SystemFontOfSize(12) });
        WinoAccessibility.Label(button, title);
        return button;
    }

    public static NSButton UrlLink(string title, string url)
    {
        var button = Link(new NSButton { TranslatesAutoresizingMaskIntoConstraints = false }, title);
        button.Activated += (_, _) => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url));
        return button;
    }

    /// <summary>A locked feature row on the offer page: disabled card with a lock glyph.</summary>
    public static WinoSettingsCard LockedCard(string header, string description, WinoIconGlyph icon)
    {
        var card = new WinoSettingsCard(header, description, icon, new WinoIconView(WinoIconGlyph.LockClosed, 14, WinoStyle.TertiaryText));
        card.IsEnabled = false;
        return card;
    }

    /// <summary>The account picture for a mailbox row, or the provider logo when the account is not on this device.</summary>
    public static WinoAccountIconView MailboxIcon(WinoIntelligenceMailboxItemViewModel item, IPictureStorageService pictures, double size = 28)
        => new(size)
        {
            Account = item.Account is not null
                ? MailAccountIconInfoFactory.Create(item.Account, pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };

    public static void Clear(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews) { stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
    }
}

using AppKit;
using CoreAnimation;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// Settings surface tokens: the raised card (fill, stroke, shadow), the tinted icon tile palette, the
/// expander row surface and the sidebar selection fill. All resolve per appearance.
/// </summary>
public static class WinoSettingsStyle
{
    /// <summary>Card height, padding and radius: a raised card led by a tinted icon tile.</summary>
    public const double CardMinHeight = 64;
    public const double NestedRowMinHeight = 46;
    public const double CardRadius = 10;
    public const double CardPadding = 16;
    public const double CardVerticalPadding = 12;
    /// <summary>Width of the leading icon column: the 30pt tile.</summary>
    public const double IconSize = 30;
    public const double IconGlyphSize = 16;
    public const double IconTileRadius = 8;
    public const double IconGap = 12;
    /// <summary>Nested expander rows start at the header text column: padding + icon + gap.</summary>
    public const double NestedIndent = CardPadding + IconSize + IconGap;
    public const double CardSpacing = 8;
    public const double PopUpMinWidth = 180;

    public static NSColor CardFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF), WinoStyle.Hex(0x2C2C2E));
    public static NSColor CardStroke { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static NSColor CardStrokeHover { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.11), WinoStyle.Hex(0xFFFFFF, 0.13));
    public static NSColor CardPressedFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0xF2F2F4), WinoStyle.Hex(0x333336));
    public static NSColor ExpanderItemsFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F9), WinoStyle.Hex(0x272729));
    public static NSColor SubtleFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.05), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static NSColor SelectedFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    public static NSColor PaneVeil { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.45), WinoStyle.Hex(0x14141A, 0.45));

    public static NSFont SectionTitle => NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
    public static NSFont PageTitle => NSFont.SystemFontOfSize(24, NSFontWeight.Semibold);
    public static NSFont CardTitle => NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
    public static NSFont CardDescription => NSFont.SystemFontOfSize(12);

    private static readonly NSColor[] Tints =
    [
        WinoStyle.Dynamic(WinoStyle.Hex(0x0F6CBD), WinoStyle.Hex(0x479EF5)),
        WinoStyle.Dynamic(WinoStyle.Hex(0x038387), WinoStyle.Hex(0x4FC3C7)),
        WinoStyle.Dynamic(WinoStyle.Hex(0x107C10), WinoStyle.Hex(0x54B054)),
        WinoStyle.Dynamic(WinoStyle.Hex(0xC98A00), WinoStyle.Hex(0xFFC83D)),
        WinoStyle.Dynamic(WinoStyle.Hex(0xDA3B01), WinoStyle.Hex(0xF7894A)),
        WinoStyle.Dynamic(WinoStyle.Hex(0xD13438), WinoStyle.Hex(0xF1707B)),
        WinoStyle.Dynamic(WinoStyle.Hex(0x8764B8), WinoStyle.Hex(0xB696FF)),
        WinoStyle.Dynamic(WinoStyle.Hex(0x737373), WinoStyle.Hex(0x8E8E93))
    ];

    public static NSColor TintColor(WinoSettingsTint tint) => Tints[(int)tint];

    /// <summary>
    /// The tile colour a card gets when its page does not choose one: related settings share a colour
    /// (warnings red, mail and sync blue, people and intelligence purple), and other glyphs get a stable
    /// colour of their own so neighbouring rows rarely match.
    /// </summary>
    public static WinoSettingsTint TintFor(WinoIconGlyph glyph) => glyph switch
    {
        WinoIconGlyph.Delete or WinoIconGlyph.Warning or WinoIconGlyph.Alert or WinoIconGlyph.AlertBadge or WinoIconGlyph.AlertOff
            or WinoIconGlyph.ShieldError or WinoIconGlyph.Bug or WinoIconGlyph.Stop or WinoIconGlyph.Dismiss or WinoIconGlyph.SubtractCircle => WinoSettingsTint.Red,
        WinoIconGlyph.Mail or WinoIconGlyph.NewMail or WinoIconGlyph.Sync or WinoIconGlyph.FolderSync or WinoIconGlyph.Cloud or WinoIconGlyph.Send
            or WinoIconGlyph.MailArrowUp or WinoIconGlyph.MailInboxCheckmark or WinoIconGlyph.MarkRead or WinoIconGlyph.Reply or WinoIconGlyph.Globe
            or WinoIconGlyph.GlobeDesktop or WinoIconGlyph.Link or WinoIconGlyph.Info or WinoIconGlyph.Devices or WinoIconGlyph.Desktop or WinoIconGlyph.Window => WinoSettingsTint.Blue,
        WinoIconGlyph.WinoIntelligence or WinoIconGlyph.Sparkle or WinoIconGlyph.DailyBriefing or WinoIconGlyph.Translate or WinoIconGlyph.People
            or WinoIconGlyph.PeopleLink or WinoIconGlyph.Person or WinoIconGlyph.PersonTag or WinoIconGlyph.PersonSwap or WinoIconGlyph.PersonSquare
            or WinoIconGlyph.ManageAccounts or WinoIconGlyph.TextProofingTools => WinoSettingsTint.Purple,
        WinoIconGlyph.TaskList or WinoIconGlyph.Checkmark or WinoIconGlyph.Calendar or WinoIconGlyph.CalendarSettings or WinoIconGlyph.CalendarWeek
            or WinoIconGlyph.CalendarWorkWeek or WinoIconGlyph.Clock or WinoIconGlyph.History or WinoIconGlyph.Power or WinoIconGlyph.Play
            or WinoIconGlyph.ArrowDownload or WinoIconGlyph.ArrowUpload or WinoIconGlyph.ArrowClockwise => WinoSettingsTint.Green,
        WinoIconGlyph.Color or WinoIconGlyph.PaintBrush or WinoIconGlyph.DarkTheme or WinoIconGlyph.IconStyle or WinoIconGlyph.Image or WinoIconGlyph.Camera
            or WinoIconGlyph.Star or WinoIconGlyph.StarFilled or WinoIconGlyph.Flash or WinoIconGlyph.Pin or WinoIconGlyph.Announcement => WinoSettingsTint.Orange,
        WinoIconGlyph.Folder or WinoIconGlyph.Tag or WinoIconGlyph.Note or WinoIconGlyph.NoteEdit or WinoIconGlyph.Signature or WinoIconGlyph.Storage
            or WinoIconGlyph.Library or WinoIconGlyph.List or WinoIconGlyph.Filter or WinoIconGlyph.ArrowSort or WinoIconGlyph.Grid or WinoIconGlyph.Board
            or WinoIconGlyph.GroupList or WinoIconGlyph.TextFont or WinoIconGlyph.TextLineSpacing or WinoIconGlyph.TextDescription
            or WinoIconGlyph.TextBulletListTree => WinoSettingsTint.Teal,
        WinoIconGlyph.LockClosed or WinoIconGlyph.Certificate or WinoIconGlyph.Keyboard or WinoIconGlyph.Speaker or WinoIconGlyph.Eye
            or WinoIconGlyph.EyeOff or WinoIconGlyph.Find => WinoSettingsTint.Amber,
        WinoIconGlyph.Settings or WinoIconGlyph.More => WinoSettingsTint.Gray,
        _ => (WinoSettingsTint)((int)glyph % (int)WinoSettingsTint.Gray)
    };

    /// <summary>The raised card shadow; the path follows the rounded card so the shadow never re-renders from the alpha mask.</summary>
    public static void ApplyCardShadow(CALayer layer, CGRect bounds, bool dark, bool hovered)
    {
        layer.MasksToBounds = false;
        layer.ShadowColor = NSColor.Black.CGColor;
        layer.ShadowOpacity = hovered ? (dark ? 0.45f : 0.10f) : (dark ? 0.35f : 0.06f);
        layer.ShadowRadius = hovered ? 4 : 1.5f;
        layer.ShadowOffset = new CGSize(0, hovered ? -2 : -1);
        layer.ShadowPath = CGPath.FromRoundedRect(bounds, (nfloat)CardRadius, (nfloat)CardRadius);
    }

    public static void ClearCardShadow(CALayer layer)
    {
        layer.ShadowOpacity = 0;
        layer.ShadowPath = null;
    }
}

/// <summary>Icon tile colours of the settings cards.</summary>
public enum WinoSettingsTint
{
    Blue,
    Teal,
    Green,
    Amber,
    Orange,
    Red,
    Purple,
    Gray
}

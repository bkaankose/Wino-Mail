using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The Windows AppModeFooterSwitcherControl as a card at the bottom of the shell pane: five
/// icon-only segments (Mail, Calendar, Contacts, To Do, Settings). The selected segment shows the
/// Filled glyph in the accent colour on a raised chip; the others rest in secondary ink.
/// </summary>
public sealed class WinoModeSwitcher : NSView
{
    private sealed record Segment(WinoApplicationMode Mode, WinoIconGlyph Regular, WinoIconGlyph Filled, string Title, string Shortcut);

    private static readonly Segment[] Segments =
    [
        new(WinoApplicationMode.Mail, WinoIconGlyph.Mail, WinoIconGlyph.MailFilled, Translator.KeyboardShortcuts_ModeMail, "⌘1"),
        new(WinoApplicationMode.Calendar, WinoIconGlyph.Calendar, WinoIconGlyph.CalendarFilled, Translator.KeyboardShortcuts_ModeCalendar, "⌘2"),
        new(WinoApplicationMode.Contacts, WinoIconGlyph.People, WinoIconGlyph.PeopleFilled, Translator.ContactsPage_Title, "⌘3"),
        new(WinoApplicationMode.Tasks, WinoIconGlyph.CheckmarkCircle, WinoIconGlyph.CheckmarkCircleFilled, Translator.ToDoPage_Title, "⌘4"),
        new(WinoApplicationMode.Settings, WinoIconGlyph.Settings, WinoIconGlyph.SettingsFilled, Translator.MenuSettings, "⌘,")
    ];

    private readonly List<(Segment Segment, WinoSurfaceView Chip, NSButton Button)> _items = new();
    private WinoApplicationMode _selected = WinoApplicationMode.Mail;

    /// <summary>Raised when the user picks a segment. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var card = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0xFFFFFF, 0.07)),
            Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07))
        };
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Distribution = NSStackViewDistribution.FillEqually,
            Spacing = 4,
            EdgeInsets = new NSEdgeInsets(6, 6, 6, 6),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var segment in Segments)
        {
            var chip = new WinoSurfaceView
            {
                CornerRadius = WinoStyle.ControlRadius,
                Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0xFFFFFF, 0.12)),
                Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10))
            };
            var button = new NSButton
            {
                Bordered = false,
                ImagePosition = NSCellImagePosition.ImageOnly,
                ToolTip = $"{segment.Title} ({segment.Shortcut})",
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            button.SetButtonType(NSButtonType.MomentaryChange);
            WinoAccessibility.Label(button, segment.Title);
            var captured = segment.Mode;
            button.Activated += (_, _) => ModeSelected?.Invoke(this, captured);
            WinoLayout.Fill(button, chip);
            chip.HeightAnchor.ConstraintEqualTo(34).Active = true;
            row.AddArrangedSubview(chip);
            _items.Add((segment, chip, button));
        }
        WinoLayout.Fill(row, card);
        WinoLayout.Fill(card, this, 8, 8, 12, 8);
        WinoAccessibility.Label(this, "Mode");
        Refresh();
        WinoStyle.AccentChanged += StyleChanged;
        WinoIcons.StyleChanged += StyleChanged;
    }

    /// <summary>The mode whose segment is drawn selected.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Refresh(); }
    }

    private void StyleChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        foreach (var (segment, chip, button) in _items)
        {
            bool selected = segment.Mode == _selected;
            // The selected glyph is drawn in the accent itself: a template tint is lost in the colorful
            // icon style (the image is not a template there) and in borderless MomentaryChange buttons.
            button.Image = WinoIcons.Image(selected ? segment.Filled : segment.Regular, 18, selected ? WinoStyle.Accent : null, segment.Title);
            button.ContentTintColor = selected ? WinoStyle.Accent : WinoStyle.SecondaryText;
            chip.Fill = selected ? WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0xFFFFFF, 0.12)) : null;
            chip.Stroke = selected ? WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10)) : null;
        }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Refresh();
    }

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, 34 + 12 + 8 + 12);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= StyleChanged;
            WinoIcons.StyleChanged -= StyleChanged;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The pane surface behind the shell menu. With a Wino theme the wallpaper shows through,
/// blurred within the window over <c>ThemeBackdropView</c> and softened by a light or dark veil;
/// with the Default theme it is the native behind-window sidebar material.
/// </summary>
public sealed class WinoPaneBackdropView : NSView
{
    private readonly NSVisualEffectView _effect = new()
    {
        State = NSVisualEffectState.FollowsWindowActiveState,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly WinoSurfaceView _veil = new()
    {
        Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.22), WinoStyle.Hex(0x14141A, 0.28))
    };

    public WinoPaneBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_effect, this);
        WinoLayout.Fill(_veil, this);
        Apply();
        WinoStyle.BackdropChanged += BackdropChanged;
    }

    private void BackdropChanged(object? sender, EventArgs args) => Apply();

    private void Apply()
    {
        bool themed = WinoStyle.HasBackdrop;
        _effect.BlendingMode = themed ? NSVisualEffectBlendingMode.WithinWindow : NSVisualEffectBlendingMode.BehindWindow;
        _effect.Material = themed ? NSVisualEffectMaterial.HudWindow : NSVisualEffectMaterial.Sidebar;
        _effect.Hidden = false;
        _veil.Hidden = !themed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.BackdropChanged -= BackdropChanged;
        base.Dispose(disposing);
    }
}

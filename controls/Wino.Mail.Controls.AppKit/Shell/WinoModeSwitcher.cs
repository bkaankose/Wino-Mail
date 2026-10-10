using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The Windows AppModeFooterSwitcherControl at the bottom of the shell pane: a native select-one
/// segmented control with five icon segments (Mail, Calendar, Contacts, To Do, Settings). AppKit draws
/// the selection, handles keyboard navigation and exposes the segments as a radio group; the selected
/// segment shows the Filled glyph in the accent colour.
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

    private readonly NSSegmentedControl _control = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        TrackingMode = NSSegmentSwitchTracking.SelectOne,
        SegmentStyle = NSSegmentStyle.Automatic,
        SegmentDistribution = NSSegmentDistribution.FillEqually,
        ControlSize = NSControlSize.Large,
        SegmentCount = Segments.Length
    };

    private WinoApplicationMode _selected = WinoApplicationMode.Mail;

    /// <summary>Raised when the user picks a segment. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        for (int index = 0; index < Segments.Length; index++)
            _control.SetToolTip($"{Segments[index].Title} ({Segments[index].Shortcut})", index);
        _control.Activated += SegmentActivated;
        WinoLayout.Fill(_control, this, 8, 8, 12, 8);
        WinoAccessibility.Label(_control, "Mode");
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

    private void SegmentActivated(object? sender, EventArgs args)
    {
        var index = (int)_control.SelectedSegment;
        // The host confirms the switch through Selected; until then the old segment stays selected.
        Refresh();
        if (index >= 0 && index < Segments.Length) ModeSelected?.Invoke(this, Segments[index].Mode);
    }

    private void StyleChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        for (int index = 0; index < Segments.Length; index++)
        {
            var segment = Segments[index];
            bool selected = segment.Mode == _selected;
            // The selected glyph is drawn in the accent itself: a template tint is lost in the colorful icon style.
            _control.SetImage(WinoIcons.Image(selected ? segment.Filled : segment.Regular, 18, selected ? WinoStyle.Accent : null, segment.Title), index);
            if (selected) _control.SelectedSegment = index;
        }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control.Activated -= SegmentActivated;
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
        // The translucent window material alone keeps the native sidebar material; a theme wallpaper blurs within the window.
        bool themed = WinoStyle.HasBackdrop && !WinoStyle.IsWindowMaterialOnly;
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

using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;
using Wino.Presentation.Controls;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The Windows AppModeFooterSwitcherControl at the bottom of the shell pane: a native select-one
/// segmented control with five icon segments (Mail, Calendar, Contacts, To Do, Settings). AppKit draws
/// the selection, handles keyboard navigation and exposes the segments as a radio group. The selected
/// segment's pill takes the theme accent (not the system accent) and shows the Filled glyph in a colour
/// readable on it.
/// </summary>
public sealed class WinoModeSwitcher : NSView
{
    /// <summary>The shared Windows segment order and glyphs (<see cref="WinoModeSegments.All"/>), less the modes hidden by <see cref="SetVisibleModes"/>.</summary>
    private IReadOnlyList<WinoModeSegment> Segments = WinoModeSegments.All;

    private readonly NSSegmentedControl _control = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        TrackingMode = NSSegmentSwitchTracking.SelectOne,
        SegmentStyle = NSSegmentStyle.Automatic,
        SegmentDistribution = NSSegmentDistribution.FillEqually,
        ControlSize = NSControlSize.Large,
        SegmentCount = WinoModeSegments.All.Count
    };

    private WinoApplicationMode _selected = WinoApplicationMode.Mail;

    /// <summary>Raised when the user picks a segment. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        ApplyToolTips();
        _control.Activated += SegmentActivated;
        WinoLayout.Fill(_control, this, 8, 8, 12, 8);
        WinoAccessibility.Label(_control, "Mode");
        Refresh();
        WinoIcons.StyleWatchers.Add(this, static switcher => switcher.Refresh());
    }

    /// <summary>The mode whose segment is drawn selected.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Refresh(); }
    }

    /// <summary>
    /// Shows only <paramref name="visible"/> (the shell's <c>AppModeVisibility.VisibleModes</c>) plus Settings, which is
    /// always reachable (Windows AppModeFooterSwitcherControl); null shows every segment.
    /// </summary>
    public void SetVisibleModes(IReadOnlyCollection<WinoApplicationMode>? visible)
    {
        var segments = WinoModeSegments.All
            .Where(segment => visible is null || segment.Mode == WinoApplicationMode.Settings || visible.Contains(segment.Mode))
            .ToArray();
        if (segments.Select(segment => segment.Mode).SequenceEqual(Segments.Select(segment => segment.Mode))) return;
        Segments = segments;
        _control.SegmentCount = segments.Length;
        ApplyToolTips();
        Refresh();
    }

    private void ApplyToolTips()
    {
        for (int index = 0; index < Segments.Count; index++)
            _control.SetToolTip($"{Segments[index].Title} ({Segments[index].Shortcut})", index);
    }

    private void SegmentActivated(object? sender, EventArgs args)
    {
        var index = (int)_control.SelectedSegment;
        // The host confirms the switch through Selected; until then the old segment stays selected.
        Refresh();
        if (index >= 0 && index < Segments.Count) ModeSelected?.Invoke(this, Segments[index].Mode);
    }

    private void Refresh()
    {
        // The system draws the selected segment (a plain segmented control, as in Finder); the filled
        // glyph alone marks the active mode.
        for (int index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            bool selected = segment.Mode == _selected;
            _control.SetImage(WinoIcons.Image(selected ? segment.Filled : segment.Regular, 18, null, segment.Title), index);
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
            WinoIcons.StyleWatchers.Remove(this);
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
        WinoStyle.BackdropWatchers.Add(this, static view => view.BackdropChanged(null, EventArgs.Empty));
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
        if (disposing) WinoStyle.BackdropWatchers.Remove(this);
        base.Dispose(disposing);
    }
}

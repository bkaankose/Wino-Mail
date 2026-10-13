using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Shell;

/// <summary>
/// The mode switcher card at the foot of the iPad sidebar (Windows AppModeFooterSwitcherControl; UIKit twin of the
/// AppKit <c>WinoModeSwitcher</c>): a native segmented control with one icon segment per mode in the desktop order, on
/// a rounded card. The selected segment shows the Filled glyph in the accent colour. By default it lists the four app
/// modes, because Settings is never a mode on iOS and iPadOS (it opens from the avatar); pass
/// <c>includeSettings: true</c> for the five desktop segments.
/// </summary>
public sealed class WinoModeSwitcher : WinoSurfaceView
{
    private readonly IReadOnlyList<WinoModeSegment> _segments;
    private readonly UISegmentedControl _control = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private WinoApplicationMode _selected = WinoApplicationMode.Mail;

    /// <summary>Raised when the user picks a segment. The host decides whether it becomes <see cref="Selected"/>.</summary>
    public event EventHandler<WinoApplicationMode>? ModeSelected;

    public WinoModeSwitcher(bool includeSettings = false)
    {
        _segments = includeSettings ? WinoModeSegments.All : WinoModeSegments.AppModes;
        CornerRadius = WinoStyle.GroupRadius + 2;
        Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0xFFFFFF, 0.06));
        Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
        for (int index = 0; index < _segments.Count; index++)
            _control.InsertSegment(Image(_segments[index], false), index, false);
        _control.ValueChanged += SegmentChanged;
        WinoLayout.Fill(_control, this, 8, 8, 8, 8);
        _control.HeightAnchor.ConstraintGreaterThanOrEqualTo(36).Active = true;
        WinoAccessibility.Label(_control, "Mode");
        Refresh();
        WinoStyle.AccentChanged += StyleChanged;
        WinoIcons.StyleChanged += StyleChanged;
    }

    /// <summary>The modes in segment order.</summary>
    public IReadOnlyList<WinoModeSegment> Segments => _segments;

    /// <summary>The mode whose segment is drawn selected.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Refresh(); }
    }

    private void SegmentChanged(object? sender, EventArgs args)
    {
        var index = (int)_control.SelectedSegment;
        // The host confirms the switch through Selected; until then the old segment stays selected.
        Refresh();
        if (index >= 0 && index < _segments.Count) ModeSelected?.Invoke(this, _segments[index].Mode);
    }

    private void StyleChanged(object? sender, EventArgs args) => Refresh();

    private static UIImage Image(WinoModeSegment segment, bool selected)
        // The selected glyph is drawn in the accent itself: a template tint is lost in the colorful icon style.
        => WinoIcons.Image(selected ? segment.Filled : segment.Regular, 20, selected ? WinoStyle.Accent : null, segment.Title);

    private void Refresh()
    {
        for (int index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            bool selected = segment.Mode == _selected;
            _control.SetImage(Image(segment, selected), index);
            if (selected) _control.SelectedSegment = index;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control.ValueChanged -= SegmentChanged;
            WinoStyle.AccentChanged -= StyleChanged;
            WinoIcons.StyleChanged -= StyleChanged;
            ModeSelected = null;
        }
        base.Dispose(disposing);
    }
}

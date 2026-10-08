using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// The filter row chip from the design board (Windows DropDownButton / ToggleButton): 30pt
/// high, radius 6, card fill and stroke, a 13pt glyph and a label. With <see cref="Menu"/> set
/// a click pops the menu under the chip and a chevron is shown; otherwise <see cref="Clicked"/>
/// fires. <see cref="Checked"/> tints the chip in the accent (the Important toggle).
/// </summary>
public sealed class WinoChipButton : WinoPressableView
{
    private readonly WinoSurfaceView _surface = WinoToDoStyle.Card();
    private readonly WinoIconView _icon;
    private readonly NSTextField _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12.5f));
    private readonly WinoIconView _chevron = new(WinoIconGlyph.ChevronDown, 9, WinoStyle.SecondaryText) { Hidden = true };
    private NSTrackingArea? _tracking;
    private bool _hovered;
    private bool _checked;
    private NSMenu? _menu;

    public WinoChipButton(WinoIconGlyph glyph, string? title, string? accessibilityLabel = null)
    {
        WinoLayout.Fill(_surface, this);
        _icon = new WinoIconView(glyph, 13);
        _label.StringValue = title ?? string.Empty;
        _label.Hidden = string.IsNullOrEmpty(title);
        var stack = WinoLayout.HStack(6, _icon, _label, _chevron);
        stack.EdgeInsets = new NSEdgeInsets(0, 10, 0, 10);
        WinoLayout.Fill(stack, _surface);
        HeightAnchor.ConstraintEqualTo((nfloat)WinoToDoStyle.ChipHeight).Active = true;
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityLabel = accessibilityLabel ?? title;
        ToolTip = accessibilityLabel;
        WinoStyle.AccentChanged += AccentChanged;
    }

    public string Title
    {
        get => _label.StringValue;
        set { _label.StringValue = value ?? string.Empty; _label.Hidden = string.IsNullOrEmpty(value); }
    }

    public WinoIconGlyph Glyph { set => _icon.Icon = value; }

    /// <summary>Pops under the chip on click. Set <see cref="NSMenu.AutoEnablesItems"/> as needed before showing.</summary>
    public NSMenu? Menu
    {
        get => _menu;
        set { _menu = value; _chevron.Hidden = value is null; }
    }

    public bool Checked
    {
        get => _checked;
        set { _checked = value; Refresh(); }
    }

    protected override void OnEnabledChanged() { AlphaValue = Enabled ? 1 : (nfloat)0.5; Refresh(); }

    protected override double FocusRingCornerRadius => _surface.CornerRadius;

    protected override bool ActivatesOnMouseDown => _menu is not null;

    private void AccentChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var accent = WinoStyle.Accent;
        _surface.Fill = _checked ? accent.ColorWithAlphaComponent((nfloat)0.14) : _hovered && Enabled ? WinoToDoStyle.HoverFill : WinoToDoStyle.CardFill;
        _surface.Stroke = _checked ? accent.ColorWithAlphaComponent((nfloat)0.6) : WinoToDoStyle.CardStroke;
        _icon.Tint = _checked ? accent : null;
        _label.TextColor = _checked ? accent : WinoStyle.PrimaryText;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) { _hovered = true; Refresh(); }
    public override void MouseExited(NSEvent theEvent) { _hovered = false; Refresh(); }

    protected override void OnActivated()
    {
        if (_menu is { } menu)
        {
            menu.PopUpMenu(null, new CGPoint(0, IsFlipped ? Bounds.Height + 4 : -4), this);
            return;
        }
        base.OnActivated();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

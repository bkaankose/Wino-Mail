using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// A detail drawer action row (Windows: a stretched TransparentActionButton under a section
/// label; design board "rowb"): 36pt, radius 5, card fill, a 15pt glyph and text, optional
/// trailing caption. With <see cref="Menu"/> the click pops the menu; otherwise <see cref="Clicked"/>.
/// </summary>
public sealed class WinoDrawerActionRow : WinoPressableView
{
    private readonly WinoSurfaceView _surface = WinoToDoStyle.Card(5, stroked: false);
    private readonly WinoIconView _icon;
    private readonly NSTextField _text = WinoStyle.Label(string.Empty, WinoStyle.Body);
    private readonly NSTextField _trailing = WinoToDoStyle.Caption();
    private NSTrackingArea? _tracking;
    private NSColor? _tint;
    private NSColor? _textColor;
    private bool _hovered;
    private bool _plain;

    public WinoDrawerActionRow(WinoIconGlyph glyph, string text, NSColor? tint = null, NSColor? textColor = null, string? trailing = null)
    {
        _tint = tint;
        _textColor = textColor;
        WinoLayout.Fill(_surface, this);
        _icon = new WinoIconView(glyph, 15, tint);
        _text.StringValue = text;
        _text.TextColor = textColor ?? WinoStyle.PrimaryText;
        _text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _trailing.StringValue = trailing ?? string.Empty;
        _trailing.Hidden = string.IsNullOrEmpty(trailing);
        var stack = WinoLayout.HStack(10, _icon, _text, _trailing);
        stack.EdgeInsets = new NSEdgeInsets(0, 10, 0, 10);
        WinoLayout.Fill(stack, _surface);
        HeightAnchor.ConstraintEqualTo(36).Active = true;
        AccessibilityLabel = text;
        WinoStyle.AccentChanged += AccentChanged;
    }

    public NSMenu? Menu { get; set; }

    /// <summary>No card fill at rest (the drawer's "Add step" row); hover still tints.</summary>
    public bool Plain
    {
        get => _plain;
        set { _plain = value; Refresh(); }
    }

    public string Text
    {
        get => _text.StringValue;
        set { _text.StringValue = value ?? string.Empty; AccessibilityLabel = value; }
    }

    public string Trailing
    {
        set { _trailing.StringValue = value ?? string.Empty; _trailing.Hidden = string.IsNullOrEmpty(value); }
    }

    /// <summary>Glyph tint; null follows the label colour. Pass <c>followAccent</c> to re-tint on theme changes.</summary>
    public bool FollowsAccent { get; init; }

    public NSColor? TextColor
    {
        set { _textColor = value; _text.TextColor = value ?? WinoStyle.PrimaryText; }
    }

    public NSColor? Tint
    {
        set { _tint = value; _icon.Tint = value; }
    }

    protected override void OnEnabledChanged()
    {
        _icon.Tint = Enabled ? _tint : WinoToDoStyle.Disabled;
        _text.TextColor = Enabled ? (_textColor ?? WinoStyle.PrimaryText) : WinoToDoStyle.Disabled;
        _trailing.TextColor = Enabled ? WinoStyle.SecondaryText : WinoToDoStyle.Disabled;
        Refresh();
    }

    protected override double FocusRingCornerRadius => _surface.CornerRadius;

    protected override bool ActivatesOnMouseDown => Menu is not null;

    private void AccentChanged(object? sender, EventArgs e)
    {
        if (!FollowsAccent) return;
        _tint = WinoStyle.Accent;
        if (Enabled) _icon.Tint = _tint;
    }

    private void Refresh() => _surface.Fill = _hovered && Enabled ? WinoToDoStyle.HoverFill : _plain ? null : WinoToDoStyle.CardFill;

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
        if (Menu is { } menu) { menu.PopUpMenu(null, new CGPoint(0, IsFlipped ? Bounds.Height + 4 : -4), this); return; }
        base.OnActivated();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// One checklist step in the drawer (Windows TaskStepTemplate): round checkbox, an editable
/// borderless title and a Dismiss button. Title edits commit when the field ends editing.
/// </summary>
public sealed class WinoStepRowView : NSView
{
    private readonly WinoSurfaceView _surface = WinoToDoStyle.Card(5, stroked: false);
    private readonly WinoRoundCheckbox _check = new(16);
    private readonly NSTextField _title = new();
    private readonly NSButton _delete;
    private bool _completed;

    public WinoStepRowView(string deleteLabel)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_surface, this);
        _check.Toggled += (_, _) => Toggled?.Invoke(this, EventArgs.Empty);

        _title.Bezeled = false;
        _title.Bordered = false;
        _title.DrawsBackground = false;
        _title.FocusRingType = NSFocusRingType.None;
        _title.Font = WinoStyle.Body;
        _title.UsesSingleLineMode = true;
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _title.TranslatesAutoresizingMaskIntoConstraints = false;
        _title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _title.EditingEnded += (_, _) => TitleCommitted?.Invoke(this, EventArgs.Empty);

        _delete = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, deleteLabel, 12, 24, 24, WinoStyle.SecondaryText);
        _delete.Activated += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var stack = WinoLayout.HStack(10, _check, _title, _delete);
        stack.EdgeInsets = new NSEdgeInsets(0, 10, 0, 4);
        WinoLayout.Fill(stack, _surface);
        HeightAnchor.ConstraintEqualTo(32).Active = true;
    }

    public event EventHandler? Toggled;
    public event EventHandler? TitleCommitted;
    public event EventHandler? DeleteRequested;

    /// <summary>The step this row shows; the owner keeps it to route events.</summary>
    public object? Item { get; set; }

    public string Title
    {
        get => _title.StringValue;
        set { if (_title.StringValue != value) _title.StringValue = value ?? string.Empty; ApplyCompletion(); }
    }

    public bool Completed
    {
        get => _completed;
        set { _completed = value; _check.Checked = value; ApplyCompletion(); }
    }

    public bool Editable
    {
        set { _check.Enabled = value; _title.Editable = value; _delete.Enabled = value; }
    }

    private void ApplyCompletion()
    {
        _title.TextColor = _completed ? WinoStyle.TertiaryText : WinoStyle.PrimaryText;
        if (_title.CurrentEditor is not null) return;
        _title.AttributedStringValue = new NSAttributedString(_title.StringValue, new NSStringAttributes
        {
            Font = WinoStyle.Body,
            ForegroundColor = _completed ? WinoStyle.TertiaryText : WinoStyle.PrimaryText,
            StrikethroughStyle = (int)(_completed ? NSUnderlineStyle.Single : NSUnderlineStyle.None)
        });
    }
}

/// <summary>Drawer section label (Windows ToDoDetailSectionLabelStyle): caption, semibold, secondary, margin 12/0/12/4.</summary>
public static class WinoDrawerSection
{
    public static NSTextField Label(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        return label;
    }
}

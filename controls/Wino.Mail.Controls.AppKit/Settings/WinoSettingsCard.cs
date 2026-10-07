using AppKit;
using CoreAnimation;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// macOS counterpart of the Windows SettingsCard: its own rounded surface (6pt, card fill and
/// stroke), a 20pt Wino glyph, a title and description column that fills and wraps, and a trailing
/// control column pinned to the right edge (16pt inset) that hugs its content. Nested rows inside a
/// <see cref="WinoSettingsExpander"/> drop the surface and indent to the header text column.
/// </summary>
public class WinoSettingsCard : NSView
{
    private readonly WinoSurfaceView _surface = new();
    private readonly NSStackView _text;
    private readonly NSTextField _header;
    private readonly NSTextField _description;
    private readonly WinoIconView _icon;
    private readonly WinoIconView _chevron;
    private readonly NSLayoutConstraint _iconLeading, _textLeadingToIcon, _textLeadingToEdge, _minHeight;
    private readonly NSLayoutConstraint _textTop, _textBottom;
    private NSLayoutConstraint[] _trailingConstraints = [];
    private NSView? _content;
    private NSView? _bottomContent;
    private NSView? _leadingView;
    private bool _isClickable;
    private bool? _showsChevron;
    private bool _isEnabled = true;
    private bool _isNested;
    private bool _pressed;
    private bool _isExpanderHeader;

    public WinoSettingsCard(string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, NSView? content = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _surface.CornerRadius = WinoSettingsStyle.CardRadius;
        WinoLayout.Fill(_surface, this);

        _header = WinoStyle.Label(header, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
        _description = WinoStyle.Label(description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        _description.Hidden = string.IsNullOrEmpty(description);
        _text = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _text.AddArrangedSubview(_header);
        _text.AddArrangedSubview(_description);
        // NSStackView sizes itself by its own hugging priority, not the inherited content hugging.
        _text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _header.WidthAnchor.ConstraintEqualTo(_text.WidthAnchor).Active = true;
        _description.WidthAnchor.ConstraintEqualTo(_text.WidthAnchor).Active = true;
        foreach (var label in new[] { _header, _description })
        {
            label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        }

        _icon = new WinoIconView(icon, WinoSettingsStyle.IconSize);
        WinoLayout.Size(_icon, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);
        _icon.Hidden = icon == WinoIconGlyph.None;

        _chevron = new WinoIconView(WinoIconGlyph.ChevronRight, 12, WinoStyle.SecondaryText);
        WinoLayout.Size(_chevron, 12, 12);
        _chevron.Hidden = true;

        _surface.AddSubview(_icon);
        _surface.AddSubview(_text);
        _surface.AddSubview(_chevron);

        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        var vpad = (nfloat)WinoSettingsStyle.CardVerticalPadding;
        _iconLeading = _icon.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, pad);
        _textLeadingToIcon = _text.LeadingAnchor.ConstraintEqualTo(_icon.TrailingAnchor, (nfloat)WinoSettingsStyle.IconGap);
        _textLeadingToEdge = _text.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, pad);
        _textTop = _text.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, vpad);
        _textBottom = _text.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -vpad);
        _minHeight = HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight);
        var textCenter = _text.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor);
        textCenter.Priority = 700;
        NSLayoutConstraint.ActivateConstraints(
        [
            _iconLeading,
            _icon.CenterYAnchor.ConstraintEqualTo(_text.CenterYAnchor),
            _textTop, _textBottom, textCenter,
            _chevron.TrailingAnchor.ConstraintEqualTo(_surface.TrailingAnchor, -pad),
            _chevron.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor),
            _minHeight
        ]);
        UpdateLeading();

        Content = content;
        AccessibilityElement = true;
        AccessibilityLabel = header;
        UpdateAccessibilityRole();
        UpdateSurface();
    }

    public string Header
    {
        get => _header.StringValue;
        set { _header.StringValue = value ?? string.Empty; AccessibilityLabel = value; }
    }

    public string? Description
    {
        get => _description.StringValue;
        set { _description.StringValue = value ?? string.Empty; _description.Hidden = string.IsNullOrEmpty(value); AccessibilityHelp = value; }
    }

    /// <summary>The Windows HeaderIcon glyph (20pt). <see cref="WinoIconGlyph.None"/> removes the icon column.</summary>
    public WinoIconGlyph HeaderIcon
    {
        set
        {
            _icon.Icon = value;
            _icon.Hidden = value == WinoIconGlyph.None;
            UpdateLeading();
        }
    }

    /// <summary>A custom leading view (an account picture) shown instead of the 20pt header icon.</summary>
    public NSView? LeadingView
    {
        get => _leadingView;
        set
        {
            _leadingView?.RemoveFromSuperview();
            _leadingView = value;
            _icon.Hidden = true;
            if (value is null) { UpdateLeading(); return; }
            value.TranslatesAutoresizingMaskIntoConstraints = false;
            value.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
            _surface.AddSubview(value);
            NSLayoutConstraint.ActivateConstraints(
            [
                value.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, (nfloat)WinoSettingsStyle.CardPadding),
                value.CenterYAnchor.ConstraintEqualTo(_text.CenterYAnchor),
                value.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, (nfloat)WinoSettingsStyle.CardVerticalPadding),
                value.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -(nfloat)WinoSettingsStyle.CardVerticalPadding)
            ]);
            UpdateLeading();
        }
    }

    /// <summary>The trailing control (switch, pop-up, stepper, buttons). Pinned to the trailing edge before the chevron.</summary>
    public NSView? Content
    {
        get => _content;
        set
        {
            _content?.RemoveFromSuperview();
            _content = value;
            if (value is not null)
            {
                value.TranslatesAutoresizingMaskIntoConstraints = false;
                // The control column hugs its content; NSStackView rows size by their own hugging priority.
                value.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);
                value.SetContentCompressionResistancePriority(751, NSLayoutConstraintOrientation.Horizontal);
                if (value is NSStackView stack) stack.SetHuggingPriority(751, NSLayoutConstraintOrientation.Horizontal);
                _surface.AddSubview(value);
            }
            RebuildTrailingConstraints();
            ApplyEnabled();
        }
    }

    /// <summary>
    /// Content placed under the text column and stretched to the trailing edge (the Windows
    /// ContentAlignment=Vertical card): sliders, previews, tables.
    /// </summary>
    public NSView? BottomContent
    {
        get => _bottomContent;
        set
        {
            _bottomContent?.RemoveFromSuperview();
            _bottomContent = value;
            _textBottom.Active = value is null;
            if (value is null) return;
            value.TranslatesAutoresizingMaskIntoConstraints = false;
            _surface.AddSubview(value);
            NSLayoutConstraint.ActivateConstraints(
            [
                value.TopAnchor.ConstraintEqualTo(_text.BottomAnchor, 10),
                value.LeadingAnchor.ConstraintEqualTo(_text.LeadingAnchor),
                value.TrailingAnchor.ConstraintEqualTo(_surface.TrailingAnchor, -(nfloat)WinoSettingsStyle.CardPadding),
                value.BottomAnchor.ConstraintEqualTo(_surface.BottomAnchor, -(nfloat)WinoSettingsStyle.CardVerticalPadding),
                _text.TopAnchor.ConstraintEqualTo(_surface.TopAnchor, (nfloat)WinoSettingsStyle.CardVerticalPadding)
            ]);
        }
    }

    /// <summary>Navigation card: shows a chevron and raises <see cref="Activated"/> on click, Return or Space.</summary>
    public bool IsClickable
    {
        get => _isClickable;
        set { _isClickable = value; UpdateChevron(); UpdateAccessibilityRole(); }
    }

    /// <summary>Overrides the chevron shown for clickable cards.</summary>
    public bool ShowsChevron
    {
        get => _showsChevron ?? _isClickable;
        set { _showsChevron = value; UpdateChevron(); }
    }

    /// <summary>Expander headers show a down chevron that turns when expanded (<see cref="IsExpanded"/>).</summary>
    public bool IsExpanderHeader
    {
        get => _isExpanderHeader;
        set { _isExpanderHeader = value; _showsChevron = true; UpdateChevron(); }
    }

    public bool IsExpanded
    {
        set
        {
            if (!_isExpanderHeader) return;
            _chevron.Icon = value ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown;
            _surface.Corners = value
                ? CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner
                : CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner | CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;
            AccessibilityExpanded = value;
        }
    }

    /// <summary>Trailing glyph shown instead of the chevron, for example the Windows "Open" arrow on link cards.</summary>
    public void SetActionIcon(WinoIconGlyph glyph)
    {
        _chevron.Icon = glyph;
        _chevron.PointSize = 14;
    }

    /// <summary>
    /// Rows inside an expander: no surface of their own, 48pt minimum height, indented to the
    /// header text column and separated by hairlines drawn by the expander.
    /// </summary>
    public bool IsNested
    {
        get => _isNested;
        set
        {
            _isNested = value;
            _minHeight.Constant = (nfloat)(value ? WinoSettingsStyle.NestedRowMinHeight : WinoSettingsStyle.CardMinHeight);
            var vpad = (nfloat)(value ? 8 : WinoSettingsStyle.CardVerticalPadding);
            _textTop.Constant = vpad;
            _textBottom.Constant = -vpad;
            UpdateLeading();
            RebuildTrailingConstraints();
            UpdateSurface();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; ApplyEnabled(); }
    }

    /// <summary>Dims and disables the trailing control only; the card itself stays clickable (expander headers).</summary>
    public void SetContentEnabled(bool enabled)
    {
        if (_content is null) return;
        _content.AlphaValue = enabled ? 1 : 0.55f;
        SetControlsEnabled(_content, enabled);
    }

    public event EventHandler? Activated;

    public override bool AcceptsFirstResponder() => _isClickable && _isEnabled;
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void MouseDown(NSEvent theEvent)
    {
        if (!_isClickable || !_isEnabled) { base.MouseDown(theEvent); return; }
        _pressed = true;
        UpdateSurface();
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (!_pressed) { base.MouseUp(theEvent); return; }
        _pressed = false;
        UpdateSurface();
        var point = ConvertPointFromView(theEvent.LocationInWindow, null);
        if (Bounds.Contains(point)) Activated?.Invoke(this, EventArgs.Empty);
    }

    public override void KeyDown(NSEvent theEvent)
    {
        if (_isClickable && _isEnabled && theEvent.Characters is "\r" or " ") { Activated?.Invoke(this, EventArgs.Empty); return; }
        base.KeyDown(theEvent);
    }

    public override bool AccessibilityPerformPress()
    {
        if (!_isClickable || !_isEnabled) return false;
        Activated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        UpdateSurface();
    }

    private void UpdateLeading()
    {
        var hasLeading = _leadingView is not null || !_icon.Hidden;
        var leading = (nfloat)(_isNested ? WinoSettingsStyle.NestedIndent : WinoSettingsStyle.CardPadding);
        _iconLeading.Constant = leading;
        _textLeadingToEdge.Constant = leading;
        if (_leadingView is not null)
        {
            _textLeadingToIcon.Active = false;
            _textLeadingToEdge.Active = false;
            if (_textLeadingToLeadingView is not null) _textLeadingToLeadingView.Active = false;
            _textLeadingToLeadingView = _text.LeadingAnchor.ConstraintEqualTo(_leadingView.TrailingAnchor, (nfloat)WinoSettingsStyle.IconGap);
            _textLeadingToLeadingView.Active = true;
            return;
        }
        if (_textLeadingToLeadingView is not null) { _textLeadingToLeadingView.Active = false; _textLeadingToLeadingView = null; }
        _textLeadingToIcon.Active = hasLeading;
        _textLeadingToEdge.Active = !hasLeading;
    }

    private NSLayoutConstraint? _textLeadingToLeadingView;

    private void UpdateChevron()
    {
        _chevron.Hidden = !(_showsChevron ?? _isClickable);
        RebuildTrailingConstraints();
    }

    /// <summary>Text column, control and chevron: text fills, control hugs at the trailing edge, chevron after it.</summary>
    private void RebuildTrailingConstraints()
    {
        NSLayoutConstraint.DeactivateConstraints(_trailingConstraints);
        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        var vpad = (nfloat)(_isNested ? 8 : WinoSettingsStyle.CardVerticalPadding);
        var constraints = new List<NSLayoutConstraint>();
        NSLayoutXAxisAnchor trailingEdge = _surface.TrailingAnchor;
        nfloat gap = -pad;
        if (!_chevron.Hidden)
        {
            trailingEdge = _chevron.LeadingAnchor;
            gap = -12;
        }
        if (_content is not null && !_content.Hidden)
        {
            constraints.Add(_content.TrailingAnchor.ConstraintEqualTo(trailingEdge, _chevron.Hidden ? -pad : -12));
            constraints.Add(_content.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, vpad));
            constraints.Add(_content.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -vpad));
            var center = _content.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor);
            center.Priority = 700;
            constraints.Add(center);
            trailingEdge = _content.LeadingAnchor;
            gap = -pad;
        }
        else if (_chevron.Hidden) gap = -pad;
        constraints.Add(_text.TrailingAnchor.ConstraintEqualTo(trailingEdge, gap));
        _trailingConstraints = constraints.ToArray();
        NSLayoutConstraint.ActivateConstraints(_trailingConstraints);
    }

    private void UpdateSurface()
    {
        if (_isNested)
        {
            _surface.Fill = _pressed ? WinoSettingsStyle.SelectedFill : null;
            _surface.Stroke = null;
            _surface.CornerRadius = 0;
            return;
        }
        _surface.CornerRadius = WinoSettingsStyle.CardRadius;
        _surface.Fill = _pressed ? WinoSettingsStyle.SubtleFill : WinoSettingsStyle.CardFill;
        _surface.Stroke = WinoSettingsStyle.CardStroke;
    }

    private void ApplyEnabled()
    {
        AlphaValue = _isEnabled ? 1 : 0.55f;
        if (_content is not null) SetControlsEnabled(_content, _isEnabled);
        if (_bottomContent is not null) SetControlsEnabled(_bottomContent, _isEnabled);
        AccessibilityEnabled = _isEnabled;
    }

    private static void SetControlsEnabled(NSView view, bool enabled)
    {
        switch (view)
        {
            case WinoLabeledSwitch toggle: toggle.IsEnabled = enabled; return;
            case NSControl control: control.Enabled = enabled; return;
        }
        foreach (var child in view.Subviews) SetControlsEnabled(child, enabled);
    }

    private void UpdateAccessibilityRole()
        => AccessibilityRole = _isClickable ? NSAccessibilityRoles.ButtonRole : NSAccessibilityRoles.GroupRole;
}

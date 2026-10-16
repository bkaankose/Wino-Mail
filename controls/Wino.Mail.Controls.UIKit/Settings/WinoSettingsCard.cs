using CoreAnimation;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>How a card places its <see cref="WinoSettingsCard.Content"/>.</summary>
public enum WinoSettingsContentPlacement
{
    /// <summary>Switches, value labels, pickers and icon buttons stay trailing; wider controls get their own row.</summary>
    Automatic,
    /// <summary>Always at the trailing edge, beside the text (the desktop card).</summary>
    Trailing,
    /// <summary>Always in its own row under the title and description (the phone card).</summary>
    Below
}

/// <summary>
/// The Windows SettingsCard on UIKit (twin of the AppKit <c>WinoSettingsCard</c>): its own rounded surface (card fill
/// and stroke), a 22pt Wino glyph, a title and description column that fills and wraps, and the card's control. On iOS
/// the control sits under the text in its own row (pop-ups, buttons, text fields, sliders, segmented controls, colour
/// pickers); switches, short values and icon buttons stay at the trailing edge (<see cref="ContentPlacement"/>).
/// Nested rows inside a <see cref="WinoSettingsExpander"/> drop the surface and use the full width without an indent.
/// Rows of an inset-grouped <see cref="WinoSettingsGroup"/> drop the surface too and sit on the group's surface,
/// separated by a hairline that starts at the text column.
/// </summary>
public partial class WinoSettingsCard : UIView
{
    private readonly WinoSurfaceView _surface = new();
    private readonly UIStackView _text;
    private readonly UILabel _header;
    private readonly UILabel _description;
    private readonly WinoIconView _icon;
    private readonly WinoIconView _chevron;
    private readonly WinoSeparator _separator = new() { Hidden = true };
    private readonly NSLayoutConstraint _iconLeading, _textLeadingToIcon, _textLeadingToEdge, _minHeight;
    private readonly NSLayoutConstraint _textTop, _textBottom;
    private NSLayoutConstraint? _textLeadingToLeadingView;
    private NSLayoutConstraint[] _trailingConstraints = [];
    private UIView? _content;
    private UIView? _bottomContent;
    private readonly UIStackView _below;
    private NSLayoutConstraint[] _belowConstraints = [];
    private bool _contentBelow;
    private WinoSettingsContentPlacement _placement;
    private UIView? _leadingView;
    private bool _isClickable;
    private bool? _showsChevron;
    private bool _isEnabled = true;
    private bool _isNested;
    private bool _pressed;
    private bool _isExpanderHeader;
    private bool _isGrouped;
    private WinoIconGlyph _actionIcon = WinoIconGlyph.None;

    private const double NestedVerticalPadding = 6;

    public WinoSettingsCard(string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, UIView? content = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _surface.CornerRadius = WinoSettingsStyle.CardRadius;
        WinoLayout.Fill(_surface, this);

        _header = WinoStyle.Label(header, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
        _description = WinoStyle.Label(description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        _description.Hidden = string.IsNullOrEmpty(description);
        _text = WinoLayout.VStack(1, _header, _description);
        _text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(_text, UILayoutConstraintAxis.Horizontal, 1, 250);
        foreach (var label in new[] { _header, _description }) WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 1);

        _icon = new WinoIconView(icon, WinoSettingsStyle.IconSize);
        WinoLayout.Size(_icon, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);
        _icon.Hidden = icon == WinoIconGlyph.None;

        _chevron = new WinoIconView(WinoIconGlyph.ChevronRight, 12, WinoStyle.SecondaryText);
        WinoLayout.Size(_chevron, 12, 12);
        _chevron.Hidden = true;

        _below = WinoLayout.VStack(WinoStyle.Space2);
        _below.Alignment = UIStackViewAlignment.Fill;
        _below.Hidden = true;

        _surface.AddSubview(_icon);
        _surface.AddSubview(_text);
        _surface.AddSubview(_below);
        _surface.AddSubview(_chevron);
        _surface.AddSubview(_separator);

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
            _separator.TopAnchor.ConstraintEqualTo(_surface.TopAnchor),
            _separator.LeadingAnchor.ConstraintEqualTo(_text.LeadingAnchor),
            _separator.TrailingAnchor.ConstraintEqualTo(_surface.TrailingAnchor),
            _minHeight
        ]);
        UpdateLeading();

        Content = content;
        IsAccessibilityElement = false;
        _header.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        UpdateSurface();
    }

    public string Header
    {
        get => _header.Text ?? string.Empty;
        set { _header.Text = value ?? string.Empty; UpdateBelow(); if (_listMode) ApplyListContent(); }
    }

    public new string? Description
    {
        get => _description.Text;
        set { _description.Text = value ?? string.Empty; _description.Hidden = string.IsNullOrEmpty(value); UpdateBelow(); if (_listMode) ApplyListContent(); }
    }

    /// <summary>The Windows HeaderIcon glyph (20pt). <see cref="WinoIconGlyph.None"/> removes the icon column.</summary>
    public WinoIconGlyph HeaderIcon
    {
        set
        {
            _icon.Icon = value;
            if (_listMode) return;
            _icon.Hidden = value == WinoIconGlyph.None || _leadingView is not null;
            UpdateLeading();
        }
    }

    /// <summary>A custom leading view (an account picture) shown instead of the 20pt header icon.</summary>
    public UIView? LeadingView
    {
        get => _leadingView;
        set
        {
            _leadingView?.RemoveFromSuperview();
            _leadingView = value;
            _icon.Hidden = true;
            if (_listMode) { ListLeadingChanged(); return; }
            if (value is null) { UpdateLeading(); return; }
            value.TranslatesAutoresizingMaskIntoConstraints = false;
            value.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
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

    /// <summary>
    /// The card's control (switch, menu button, stepper, buttons). <see cref="ContentPlacement"/> decides whether it sits
    /// at the trailing edge before the chevron or in its own row under the text.
    /// </summary>
    public UIView? Content
    {
        get => _content;
        set
        {
            RemoveContentView();
            _content = value;
            _contentBelow = false;
            if (_listMode) { ApplyListContent(); return; }
            if (value is not null)
            {
                value.TranslatesAutoresizingMaskIntoConstraints = false;
                _contentBelow = PlacesBelow(value);
                if (_contentBelow) InsertBelow(value, 0, StretchesBelow(value));
                else
                {
                    WinoLayout.Priorities(value, UILayoutConstraintAxis.Horizontal, 751, 751);
                    _surface.AddSubview(value);
                }
            }
            UpdateBelow();
            RebuildTrailingConstraints();
            ApplyEnabled();
        }
    }

    /// <summary>Where <see cref="Content"/> goes. Setting it re-places content that is already set.</summary>
    public WinoSettingsContentPlacement ContentPlacement
    {
        get => _placement;
        set
        {
            if (_placement == value) return;
            _placement = value;
            if (_content is { } content) Content = content;
        }
    }

    /// <summary>Content placed in its own row under the text and stretched to the card's width (the Windows vertical card).</summary>
    public UIView? BottomContent
    {
        get => _bottomContent;
        set
        {
            if (_bottomContent is not null && !_listMode) RemoveFromBelow(_bottomContent);
            _bottomContent = value;
            if (_listMode) { ApplyListContent(); return; }
            if (value is not null)
            {
                value.TranslatesAutoresizingMaskIntoConstraints = false;
                // Rows, grids and fields fill the width; a lone button or pop-up keeps its own width.
                InsertBelow(value, _contentBelow ? 1 : 0, stretch: value is not UIButton);
            }
            UpdateBelow();
        }
    }

    /// <summary>Navigation card: shows a chevron and raises <see cref="Activated"/> on a tap.</summary>
    public bool IsClickable
    {
        get => _isClickable;
        set { _isClickable = value; UpdateChevron(); UpdateAccessibility(); if (_listMode) ApplyListContent(); }
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
        set { _isExpanderHeader = value; _showsChevron = true; _chevron.Icon = WinoIconGlyph.ChevronDown; UpdateChevron(); if (_listMode) ApplyListContent(); }
    }

    public bool IsExpanded
    {
        set
        {
            if (!_isExpanderHeader || _listMode) return;
            _chevron.Icon = value ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown;
            _surface.Corners = value ? CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner : WinoSurfaceView.AllCorners;
            AccessibilityValue = value ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;
        }
    }

    /// <summary>Trailing glyph shown instead of the chevron, for example the Windows "Open" arrow on link cards.</summary>
    public void SetActionIcon(WinoIconGlyph glyph)
    {
        _chevron.Icon = glyph;
        _chevron.PointSize = 14;
        _actionIcon = glyph;
        if (_listMode) ApplyListContent();
    }

    /// <summary>
    /// Rows inside an expander: no surface of their own, a lower minimum height, indented to the header text column and
    /// separated by hairlines drawn by the expander.
    /// </summary>
    public bool IsNested
    {
        get => _isNested;
        set
        {
            _isNested = value;
            if (_listMode) return;
            _minHeight.Constant = (nfloat)(value ? WinoSettingsStyle.NestedRowMinHeight : WinoSettingsStyle.CardMinHeight);
            var vpad = (nfloat)(value ? NestedVerticalPadding : WinoSettingsStyle.CardVerticalPadding);
            _textTop.Constant = vpad;
            _textBottom.Constant = -vpad;
            UpdateLeading();
            UpdateBelow();
            RebuildTrailingConstraints();
            UpdateSurface();
        }
    }

    /// <summary>
    /// A row of a <see cref="WinoSettingsGroup"/>: no surface of its own (the group draws the rounded surface), only the
    /// pressed fill. The group turns <see cref="ShowsTopSeparator"/> on for every visible row after the first.
    /// </summary>
    public bool IsGrouped
    {
        get => _isGrouped;
        set { _isGrouped = value; UpdateSurface(); }
    }

    /// <summary>The hairline above a grouped row, inset to the text column.</summary>
    public bool ShowsTopSeparator
    {
        get => !_separator.Hidden;
        set => _separator.Hidden = !value;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; ApplyEnabled(); }
    }

    /// <summary>Dims and disables the trailing control only; the card itself stays tappable (expander headers).</summary>
    public void SetContentEnabled(bool enabled)
    {
        if (_content is null) return;
        if (_listMode) { SettingsBinder.SetControlsEnabled(_content, enabled); return; }
        _content.Alpha = enabled ? 1 : 0.55f;
        SettingsBinder.SetControlsEnabled(_content, enabled);
    }

    public event EventHandler? Activated;

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        if (!IsTappable) { base.TouchesBegan(touches, evt); return; }
        _pressed = true;
        UpdateSurface();
    }

    public override void TouchesMoved(NSSet touches, UIEvent? evt)
    {
        if (!_pressed) { base.TouchesMoved(touches, evt); return; }
        bool inside = touches.AnyObject is UITouch touch && Bounds.Contains(touch.LocationInView(this));
        if (!inside) { _pressed = false; UpdateSurface(); }
    }

    public override void TouchesEnded(NSSet touches, UIEvent? evt)
    {
        if (!_pressed) { base.TouchesEnded(touches, evt); return; }
        _pressed = false;
        UpdateSurface();
        if (touches.AnyObject is UITouch touch && Bounds.Contains(touch.LocationInView(this))) Activate();
    }

    public override void TouchesCancelled(NSSet touches, UIEvent? evt)
    {
        _pressed = false;
        UpdateSurface();
        base.TouchesCancelled(touches, evt);
    }

    public override bool AccessibilityActivate()
    {
        if (!IsTappable) return false;
        Activate();
        return true;
    }

    private void UpdateLeading()
    {
        if (_listMode) return;
        var hasLeading = _leadingView is not null || !_icon.Hidden;
        var leading = (nfloat)WinoSettingsStyle.CardPadding;
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

    private void UpdateChevron()
    {
        if (_listMode) return;
        _chevron.Hidden = !(_showsChevron ?? _isClickable);
        RebuildTrailingConstraints();
    }

    /// <summary>Text column, control and chevron: text fills, control hugs at the trailing edge, chevron after it.</summary>
    private void RebuildTrailingConstraints()
    {
        if (_listMode) return;
        NSLayoutConstraint.DeactivateConstraints(_trailingConstraints);
        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        var vpad = (nfloat)(_isNested ? NestedVerticalPadding : WinoSettingsStyle.CardVerticalPadding);
        var constraints = new List<NSLayoutConstraint>();
        NSLayoutXAxisAnchor trailingEdge = _surface.TrailingAnchor;
        nfloat gap = -pad;
        if (!_chevron.Hidden)
        {
            trailingEdge = _chevron.LeadingAnchor;
            gap = -12;
        }
        if (_content is not null && !_content.Hidden && !_contentBelow)
        {
            constraints.Add(_content.TrailingAnchor.ConstraintEqualTo(trailingEdge, _chevron.Hidden ? -pad : -12));
            constraints.Add(_content.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, vpad));
            constraints.Add(_content.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -vpad));
            // With a row under the text, a trailing switch lines up with the title block instead of the whole card.
            var center = _content.CenterYAnchor.ConstraintEqualTo(_below.Hidden ? _surface.CenterYAnchor : _text.CenterYAnchor);
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

    private bool PlacesBelow(UIView content) => _placement switch
    {
        WinoSettingsContentPlacement.Trailing => false,
        WinoSettingsContentPlacement.Below => true,
        _ => NeedsOwnRow(content)
    };

    /// <summary>
    /// Controls that need the card's width get their own row: titled buttons and pop-ups, text fields, sliders,
    /// segmented controls, steppers and colour pickers. Switches, labels, pickers and icon-only buttons stay trailing.
    /// </summary>
    private static bool NeedsOwnRow(UIView view)
    {
        switch (view)
        {
            case WinoLabeledSwitch or UISwitch or UIDatePicker or UIActivityIndicatorView or UILabel or WinoIconView:
                return false;
            case UITextField or UITextView or UISegmentedControl or UISlider or UIStepper or WinoColorSwatchPicker:
                return true;
            case UIButton button:
                return button.Configuration?.Indicator == UIButtonConfigurationIndicator.Popup
                    || !string.IsNullOrEmpty(button.Configuration?.Title ?? button.CurrentTitle);
        }
        foreach (var child in view.Subviews)
            if (NeedsOwnRow(child)) return true;
        return false;
    }

    /// <summary>
    /// Text fields, sliders, segmented controls, colour pickers, wrapping text, equal-width rows and custom views fill the
    /// row; buttons, pop-ups, steppers, pickers and stacks of them keep their own width at the leading edge.
    /// </summary>
    private static bool StretchesBelow(UIView view)
    {
        switch (view)
        {
            case UITextField or UITextView or UISegmentedControl or UISlider or WinoColorSwatchPicker:
                return true;
            case UILabel label:
                return label.Lines == 0;
            case UIButton or UIStepper or UIDatePicker or UISwitch or WinoLabeledSwitch or UIActivityIndicatorView or WinoIconView:
                return false;
            case UIStackView stack:
                if (stack.Distribution == UIStackViewDistribution.FillEqually) return true;
                foreach (var child in stack.ArrangedSubviews)
                {
                    // A bare spacer view does not make the row fill.
                    var isSpacer = child.GetType() == typeof(UIView) && child.Subviews.Length == 0;
                    // A caption between controls on one line ("18:30 to 08:00") does not make the line fill either.
                    var isInlineLabel = child is UILabel && stack.Axis == UILayoutConstraintAxis.Horizontal;
                    if (!isSpacer && !isInlineLabel && StretchesBelow(child)) return true;
                }
                return false;
        }
        // Custom views (previews, lists, grids) have no width of their own.
        return true;
    }

    private void InsertBelow(UIView view, int index, bool stretch)
    {
        var host = new BelowHost(view, stretch) { Hidden = view.Hidden };
        host.Observe();
        _below.InsertArrangedSubview(host, (nuint)Math.Min(index, _below.ArrangedSubviews.Length));
    }

    private void RemoveFromBelow(UIView view)
    {
        foreach (var arranged in _below.ArrangedSubviews)
        {
            if (arranged is not BelowHost host || host.Child != view) continue;
            _below.RemoveArrangedSubview(host);
            host.RemoveFromSuperview();
        }
        view.RemoveFromSuperview();
    }

    private void RemoveContentView()
    {
        if (_content is null) return;
        if (_listMode) { RemoveListContent(_content); return; }
        if (_contentBelow) RemoveFromBelow(_content);
        else _content.RemoveFromSuperview();
    }

    /// <summary>Shows the row under the text when it has content, and pins the text to the bottom otherwise.</summary>
    private void UpdateBelow()
    {
        if (_below is null || _listMode) return;
        var hasBelow = _below.ArrangedSubviews.Length > 0;
        NSLayoutConstraint.DeactivateConstraints(_belowConstraints);
        _below.Hidden = !hasBelow;
        _textBottom.Active = !hasBelow;
        if (!hasBelow) { _belowConstraints = []; RebuildTrailingConstraints(); return; }
        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        var vpad = (nfloat)((_isNested ? NestedVerticalPadding : WinoSettingsStyle.CardVerticalPadding) + 4);
        // A card without a title or description is only its control row.
        var textless = string.IsNullOrEmpty(_header.Text) && _description.Hidden;
        _belowConstraints =
        [
            textless ? _below.TopAnchor.ConstraintEqualTo(_surface.TopAnchor, vpad) : _below.TopAnchor.ConstraintEqualTo(_text.BottomAnchor, (nfloat)WinoStyle.Space2),
            _below.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, pad),
            _below.TrailingAnchor.ConstraintEqualTo(_surface.TrailingAnchor, -pad),
            _below.BottomAnchor.ConstraintEqualTo(_surface.BottomAnchor, -vpad),
            _text.TopAnchor.ConstraintEqualTo(_surface.TopAnchor, vpad)
        ];
        NSLayoutConstraint.ActivateConstraints(_belowConstraints);
        RebuildTrailingConstraints();
    }

    /// <summary>Hosts one view of the row under the text: stretched, or leading-aligned at its own width.</summary>
    private sealed class BelowHost : UIView
    {
        public BelowHost(UIView child, bool stretch)
        {
            Child = child;
            TranslatesAutoresizingMaskIntoConstraints = false;
            child.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(child);
            var fill = child.TrailingAnchor.ConstraintEqualTo(TrailingAnchor);
            // The fill is never required: a row of fixed-width buttons must not dictate the card's (and the page's) width.
            // Buttons keep their own width; stretched content fills above its hugging.
            fill.Priority = stretch ? 760 : 1;
            NSLayoutConstraint.ActivateConstraints(
            [
                child.TopAnchor.ConstraintEqualTo(TopAnchor),
                child.BottomAnchor.ConstraintEqualTo(BottomAnchor),
                child.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                child.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor),
                fill
            ]);
        }

        public UIView Child { get; }

        /// <summary>A hidden control hides its row too, so the card keeps no empty gap.</summary>
        public void Observe() => _hidden = Child.AddObserver("hidden", NSKeyValueObservingOptions.New, _ => Hidden = Child.Hidden);

        private IDisposable? _hidden;

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _hidden?.Dispose(); _hidden = null; }
            base.Dispose(disposing);
        }
    }

    private void UpdateSurface()
    {
        if (_listMode) { UpdateListSurface(); return; }
        if (_isNested)
        {
            _surface.Fill = _pressed ? WinoSettingsStyle.SelectedFill : null;
            _surface.Stroke = null;
            _surface.CornerRadius = 0;
            return;
        }
        if (_isGrouped)
        {
            _surface.Fill = _pressed ? WinoSettingsStyle.SubtleFill : null;
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
        if (_listMode) { ApplyListEnabled(); return; }
        Alpha = _isEnabled ? 1 : 0.55f;
        if (_content is not null) SettingsBinder.SetControlsEnabled(_content, _isEnabled);
        if (_bottomContent is not null) SettingsBinder.SetControlsEnabled(_bottomContent, _isEnabled);
        UpdateAccessibility();
    }

    /// <summary>Clickable cards read as one button; others expose their title and control separately.</summary>
    private void UpdateAccessibility()
    {
        if (_listMode) { UpdateListAccessibility(); return; }
        IsAccessibilityElement = _isClickable;
        if (_isClickable)
        {
            AccessibilityLabel = string.IsNullOrEmpty(_description.Text) ? _header.Text : $"{_header.Text}, {_description.Text}";
            AccessibilityTraits = _isEnabled ? UIAccessibilityTrait.Button : UIAccessibilityTrait.Button | UIAccessibilityTrait.NotEnabled;
        }
    }
}

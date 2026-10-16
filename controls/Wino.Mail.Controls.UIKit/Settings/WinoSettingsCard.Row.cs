using System.Runtime.CompilerServices;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The settings-page row presentation of <see cref="WinoSettingsCard"/> (SettingsControls board, 15 Oct 2026): a title
/// and, by the content's kind, a trailing switch, a value and a chevron, a chevron alone, or an accent action title. No
/// header icon and no description on the page: anything that is not a switch pushes the generic
/// <see cref="WinoSettingDetailViewController"/>, which shows the description and the real control. Rows with a leading
/// picture (accounts) and rows without a control keep their subtitle.
/// </summary>
public partial class WinoSettingsCard
{
    private static readonly ConditionalWeakTable<UIView, WeakReference<WinoSettingsCard>> ValueOwners = new();

    private bool _listMode;
    private UIStackView? _rowText;
    private UIStackView? _rowTrailing;
    private UILabel? _rowValue;
    private WinoIconView? _rowChevron;
    private NSLayoutConstraint[] _rowLeadingConstraints = [];
    private WinoSettingsRowKind _rowKind;
    private UIButton? _actionButton;
    private UIButton? _checkButton;
    private IDisposable? _actionEnabledObserver;
    private bool _inlineValueControl;

    /// <summary>
    /// Renders the card as a settings-page row. One way: settings groups, settings pages and the detail page turn it on;
    /// cards elsewhere (onboarding forms) keep the desktop card.
    /// </summary>
    public bool IsListRow
    {
        get => _listMode;
        set { if (value && !_listMode) EnterListMode(); }
    }

    /// <summary>The kind the row currently renders as.</summary>
    public WinoSettingsRowKind RowKind => _rowKind;

    /// <summary>
    /// On a detail page, a numeric stepper or a time picker is drawn on the row itself instead of pushing another page.
    /// </summary>
    public bool ShowsValueControlInline
    {
        get => _inlineValueControl;
        set { _inlineValueControl = value; if (_listMode) ApplyListContent(); }
    }

    /// <summary>Marks the action row red (Windows destructive buttons).</summary>
    public bool IsDestructive { get; set; }

    /// <summary>Raised when <see cref="IsEnabled"/> changes, for detail sections that mirror the card.</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>The bottom content (Windows vertical content) the detail page hosts.</summary>
    internal UIView? DetailBottomContent => _bottomContent;

    internal bool IsExpanderHeaderRow => _isExpanderHeader;

    internal string? DescriptionText => string.IsNullOrEmpty(_description.Text) ? null : _description.Text;

    /// <summary>Opens the detail page for an expander header (set by the expander).</summary>
    internal Action? ExpanderDetail { get; set; }

    /// <summary>Asks the row that shows <paramref name="control"/> to refresh its value text (the binder calls it).</summary>
    public static void NotifyValueChanged(UIView control)
    {
        if (ValueOwners.TryGetValue(control, out var owner) && owner.TryGetTarget(out var card)) card.RefreshValue();
    }

    private bool IsTappable => _listMode
        ? _isEnabled && (_isClickable || _isExpanderHeader || _rowKind is WinoSettingsRowKind.Value or WinoSettingsRowKind.Push
                         || _rowKind == WinoSettingsRowKind.Action && _actionButton is { Enabled: true }
                         || _checkButton is { Enabled: true })
        : _isClickable && _isEnabled;

    private void Activate()
    {
        if (!_listMode) { Activated?.Invoke(this, EventArgs.Empty); return; }
        if (_isExpanderHeader && ExpanderDetail is { } openExpander) { openExpander(); return; }
        if (_isClickable) { Activated?.Invoke(this, EventArgs.Empty); return; }
        switch (_rowKind)
        {
            case WinoSettingsRowKind.Action when _actionButton is { } button:
                var events = button.AllControlEvents.HasFlag(UIControlEvent.PrimaryActionTriggered) ? UIControlEvent.PrimaryActionTriggered : UIControlEvent.TouchUpInside;
                button.SendActionForControlEvents(events);
                break;
            case WinoSettingsRowKind.Value or WinoSettingsRowKind.Push:
                WinoSettingDetailViewController.Push(this, WinoSettingDetailViewController.ForCard(this));
                break;
            case WinoSettingsRowKind.Inline when _checkButton is { } check:
                check.SendActionForControlEvents(UIControlEvent.PrimaryActionTriggered);
                break;
        }
    }

    private void EnterListMode()
    {
        var content = _content;
        if (content is not null) RemoveContentView();
        if (_bottomContent is { } bottom) RemoveFromBelow(bottom);
        NSLayoutConstraint.DeactivateConstraints(_trailingConstraints);
        NSLayoutConstraint.DeactivateConstraints(_belowConstraints);
        _trailingConstraints = [];
        _belowConstraints = [];
        _listMode = true;
        _pressed = false;

        _icon.RemoveFromSuperview();
        _chevron.RemoveFromSuperview();
        _below.RemoveFromSuperview();
        _text.RemoveArrangedSubview(_header);
        _text.RemoveArrangedSubview(_description);
        _header.RemoveFromSuperview();
        _description.RemoveFromSuperview();
        _text.RemoveFromSuperview();

        _header.Font = WinoSettingsStyle.CardTitle;
        _header.Lines = 2;
        _description.Lines = 2;
        _rowText = WinoLayout.VStack(2, _header, _description);
        _rowText.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(_header, UILayoutConstraintAxis.Horizontal, 250, 740);
        WinoLayout.Priorities(_description, UILayoutConstraintAxis.Horizontal, 250, 740);

        _rowValue = WinoStyle.Label(string.Empty, WinoSettingsStyle.RowValue, WinoStyle.SecondaryText);
        _rowValue.TextAlignment = UITextAlignment.Right;
        _rowValue.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(_rowValue, UILayoutConstraintAxis.Horizontal, 251, 760);
        _rowChevron = new WinoIconView(WinoIconGlyph.ChevronRight, 12, WinoSettingsStyle.Chevron);
        WinoLayout.Size(_rowChevron, 12, 12);
        _rowTrailing = WinoLayout.HStack(8, _rowValue, _rowChevron);
        _rowTrailing.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Priorities(_rowTrailing, UILayoutConstraintAxis.Horizontal, 750, 750);

        _surface.AddSubview(_rowText);
        _surface.AddSubview(_rowTrailing);
        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        var vpad = (nfloat)WinoSettingsStyle.RowVerticalPadding;
        var textCenter = _rowText.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor);
        textCenter.Priority = 700;
        NSLayoutConstraint.ActivateConstraints(
        [
            _rowText.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, vpad),
            _rowText.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -vpad),
            textCenter,
            _rowTrailing.TrailingAnchor.ConstraintEqualTo(_surface.TrailingAnchor, -pad),
            _rowTrailing.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor),
            _rowTrailing.TopAnchor.ConstraintGreaterThanOrEqualTo(_surface.TopAnchor, vpad),
            _rowTrailing.BottomAnchor.ConstraintLessThanOrEqualTo(_surface.BottomAnchor, -vpad),
            _rowText.TrailingAnchor.ConstraintLessThanOrEqualTo(_rowTrailing.LeadingAnchor, -12),
            _rowValue.WidthAnchor.ConstraintLessThanOrEqualTo(_surface.WidthAnchor, 0.45f),
            _separator.LeadingAnchor.ConstraintEqualTo(_rowText.LeadingAnchor)
        ]);
        // The title takes the free width so a short value sits at the trailing edge.
        var fill = _rowText.TrailingAnchor.ConstraintEqualTo(_rowTrailing.LeadingAnchor, -12);
        fill.Priority = 600;
        fill.Active = true;
        _minHeight.Constant = (nfloat)WinoSettingsStyle.RowMinHeight;
        _separator.Fill = WinoSettingsStyle.RowSeparator;
        ListLeadingChanged();
        Content = content;
        UpdateSurface();
    }

    private void ListLeadingChanged()
    {
        if (_rowText is null) return;
        NSLayoutConstraint.DeactivateConstraints(_rowLeadingConstraints);
        var pad = (nfloat)WinoSettingsStyle.CardPadding;
        if (_leadingView is { } leading)
        {
            if (leading.Superview != _surface) _surface.AddSubview(leading);
            _rowLeadingConstraints =
            [
                leading.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, pad),
                leading.CenterYAnchor.ConstraintEqualTo(_surface.CenterYAnchor),
                _rowText.LeadingAnchor.ConstraintEqualTo(leading.TrailingAnchor, (nfloat)WinoSettingsStyle.IconGap)
            ];
        }
        else _rowLeadingConstraints = [_rowText.LeadingAnchor.ConstraintEqualTo(_surface.LeadingAnchor, pad)];
        NSLayoutConstraint.ActivateConstraints(_rowLeadingConstraints);
        ApplyListContent();
    }

    /// <summary>Picks the row kind from the content and lays the trailing side out for it.</summary>
    private void ApplyListContent()
    {
        if (_rowTrailing is null || _rowValue is null || _rowChevron is null) return;
        foreach (var view in _rowTrailing.ArrangedSubviews)
        {
            if (view == _rowValue || view == _rowChevron) continue;
            _rowTrailing.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        _actionEnabledObserver?.Dispose();
        _actionEnabledObserver = null;
        _actionButton = null;
        _checkButton = null;

        var kind = WinoSettingsRowContent.Classify(_content, _bottomContent);
        _rowKind = kind;
        UIView? trailing = null;
        var showValue = false;
        var showChevron = false;
        _header.TextColor = WinoStyle.PrimaryText;
        var title = _header.Text;

        if (_isExpanderHeader)
        {
            // An expander pushes its items; a switch in its header stays on the row so it toggles without the push.
            if (kind == WinoSettingsRowKind.Switch || kind == WinoSettingsRowKind.Inline) trailing = _content;
            else showValue = kind == WinoSettingsRowKind.Value;
            showChevron = true;
        }
        else if (_isClickable)
        {
            if (kind is WinoSettingsRowKind.Switch or WinoSettingsRowKind.Inline) trailing = _content;
            else showValue = kind == WinoSettingsRowKind.Value;
            showChevron = true;
        }
        else
        {
            switch (kind)
            {
                case WinoSettingsRowKind.Switch:
                    trailing = _content;
                    break;
                case WinoSettingsRowKind.Inline:
                    trailing = _content;
                    // A check button keeps only its glyph on the row; its text becomes the title when the card has none.
                    if (_content is not null && WinoSettingsRowContent.Controls(_content) is [UIButton { Tag: WinoSettingsRowContent.CheckButtonTag } check])
                    {
                        _checkButton = check;
                        var checkTitle = WinoSettingsRowContent.ButtonTitle(check);
                        if (!string.IsNullOrEmpty(checkTitle) && check.Configuration is { } configuration)
                        {
                            if (string.IsNullOrEmpty(title)) _header.Text = title = checkTitle;
                            check.AccessibilityLabel = checkTitle;
                            configuration.Title = string.Empty;
                            configuration.ImagePadding = 0;
                            check.Configuration = configuration;
                        }
                    }
                    break;
                case WinoSettingsRowKind.Value when _inlineValueControl && _content is { } inline
                                                   && (WinoSettingsRowContent.Stepper(inline) is not null || WinoSettingsRowContent.ValueControl(inline) is UIDatePicker):
                    trailing = inline;
                    break;
                case WinoSettingsRowKind.Value:
                    showValue = true;
                    showChevron = true;
                    break;
                case WinoSettingsRowKind.Push:
                    showChevron = true;
                    break;
                case WinoSettingsRowKind.Action:
                    _actionButton = _content is null ? null : WinoSettingsRowContent.Controls(_content).OfType<UIButton>().FirstOrDefault();
                    if (_actionButton is { } button)
                    {
                        if (string.IsNullOrEmpty(title)) _header.Text = title = WinoSettingsRowContent.ButtonTitle(button);
                        _actionEnabledObserver = button.AddObserver("enabled", Foundation.NSKeyValueObservingOptions.New, _ => ApplyListAppearance());
                    }
                    break;
            }
        }

        if (trailing is not null)
        {
            trailing.TranslatesAutoresizingMaskIntoConstraints = false;
            // Trailing content keeps its own width so the title takes the rest; a caption reads as the row's value.
            WinoLayout.Priorities(trailing, UILayoutConstraintAxis.Horizontal, 751, 750);
            if (trailing is UILabel caption)
            {
                caption.TextAlignment = UITextAlignment.Right;
                caption.Font = WinoSettingsStyle.RowValue;
                caption.Lines = 1;
                caption.LineBreakMode = UILineBreakMode.TailTruncation;
                WinoLayout.Priorities(caption, UILayoutConstraintAxis.Horizontal, 751, 730);
            }
            _rowTrailing.InsertArrangedSubview(trailing, 0);
            foreach (var toggle in WinoSettingsRowContent.Controls(trailing).OfType<UISwitch>()) toggle.OnTintColor = WinoStyle.Accent;
            if (kind == WinoSettingsRowKind.Switch && _content is not null)
                foreach (var control in WinoSettingsRowContent.Controls(_content))
                    if (string.IsNullOrEmpty(control.AccessibilityLabel)) control.AccessibilityLabel = title;
        }
        _rowValue.Hidden = !showValue;
        _rowChevron.Hidden = !showChevron;
        if (_actionIcon != WinoIconGlyph.None && showChevron) { _rowChevron.Icon = _actionIcon; _rowChevron.PointSize = 14; }

        if (_content is not null && showValue)
        {
            ValueOwners.AddOrUpdate(_content, new WeakReference<WinoSettingsCard>(this));
            if (WinoSettingsRowContent.ValueControl(_content) is { } valueControl)
                ValueOwners.AddOrUpdate(valueControl, new WeakReference<WinoSettingsCard>(this));
        }

        // Settings descriptions move to the detail page; information rows and picture rows keep their subtitle.
        var keepsSubtitle = _leadingView is not null || kind is WinoSettingsRowKind.Static or WinoSettingsRowKind.Inline && !_isClickable;
        _description.Hidden = !keepsSubtitle || string.IsNullOrEmpty(_description.Text);
        _description.Font = WinoSettingsStyle.CardDescription;
        RefreshValue();
        ApplyListEnabled();
    }

    private void RemoveListContent(UIView content)
    {
        if (_rowTrailing is not null && content.Superview == _rowTrailing)
        {
            _rowTrailing.RemoveArrangedSubview(content);
            content.RemoveFromSuperview();
        }
    }

    /// <summary>Re-reads the value text from the content (after the detail page pops, or when the binder changes it).</summary>
    public void RefreshValue()
    {
        if (!_listMode || _rowValue is null || _rowValue.Hidden || _content is null) return;
        _rowValue.Text = WinoSettingsRowContent.ValueText(_content);
        UpdateListAccessibility();
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        if (Window is not null) RefreshValue();
    }

    private void UpdateListSurface()
    {
        if (_isGrouped)
        {
            _surface.Fill = _pressed ? WinoSettingsStyle.SubtleFill : null;
            _surface.Stroke = null;
            _surface.CornerRadius = 0;
            return;
        }
        _surface.CornerRadius = WinoSettingsStyle.CardRadius;
        _surface.Fill = _pressed ? WinoSettingsStyle.SubtleFill : WinoSettingsStyle.GroupFill;
        _surface.Stroke = WinoSettingsStyle.CardStroke;
    }

    private void ApplyListEnabled()
    {
        // An action button keeps its own enabled state (Command.CanExecute); the row only mirrors it.
        if (_content is not null && _actionButton is null) SettingsBinder.SetControlsEnabled(_content, _isEnabled);
        if (_bottomContent is not null) SettingsBinder.SetControlsEnabled(_bottomContent, _isEnabled);
        ApplyListAppearance();
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyListAppearance()
    {
        var actionDisabled = _rowKind == WinoSettingsRowKind.Action && _actionButton is { Enabled: false };
        Alpha = _isEnabled && !actionDisabled ? 1 : (nfloat)WinoSettingsStyle.DisabledAlpha;
        if (_rowKind == WinoSettingsRowKind.Action && !_isClickable && !_isExpanderHeader)
            _header.TextColor = IsDestructive || _actionButton is { } button && WinoSettingsRowContent.IsDestructive(button) ? UIColor.SystemRed : WinoStyle.Accent;
        UpdateListAccessibility();
    }

    private void UpdateListAccessibility()
    {
        var tappable = _isClickable || _isExpanderHeader || _rowKind is WinoSettingsRowKind.Value or WinoSettingsRowKind.Push or WinoSettingsRowKind.Action;
        var hasOwnControl = _rowTrailing?.ArrangedSubviews.Any(view => view != _rowValue && view != _rowChevron) == true;
        IsAccessibilityElement = tappable && !hasOwnControl || _rowKind == WinoSettingsRowKind.Static && !_isClickable;
        if (!IsAccessibilityElement) return;
        AccessibilityLabel = _description.Hidden ? _header.Text : $"{_header.Text}, {_description.Text}";
        AccessibilityValue = _rowValue is { Hidden: false } value ? value.Text : null;
        var enabled = IsTappable || !tappable;
        AccessibilityTraits = !tappable ? UIAccessibilityTrait.StaticText
            : enabled ? UIAccessibilityTrait.Button : UIAccessibilityTrait.Button | UIAccessibilityTrait.NotEnabled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _actionEnabledObserver?.Dispose(); _actionEnabledObserver = null; }
        base.Dispose(disposing);
    }
}

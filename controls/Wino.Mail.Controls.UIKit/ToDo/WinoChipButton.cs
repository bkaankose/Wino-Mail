using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.ToDo;

/// <summary>
/// The filter row chip (Windows DropDownButton / ToggleButton; UIKit twin of the AppKit <c>WinoChipButton</c>): a
/// rounded card-filled chip with a glyph and a label. With <see cref="Menu"/> set a tap opens the menu from the chip and
/// a chevron is shown; otherwise <see cref="WinoPressableView.Clicked"/> fires. <see cref="Checked"/> tints the chip in
/// the accent (the Important toggle).
/// </summary>
public sealed class WinoChipButton : WinoPressableView
{
    private readonly WinoIconView _icon;
    private readonly UILabel _label = WinoStyle.Label(string.Empty, WinoStyle.Subheadline);
    private readonly WinoIconView _chevron = new(WinoIconGlyph.ChevronDown, 9, WinoStyle.SecondaryText) { Hidden = true };
    private readonly UIButton _menuButton;
    private bool _checked;

    public WinoChipButton(WinoIconGlyph glyph, string? title, string? accessibilityLabel = null)
    {
        CornerRadius = WinoToDoStyle.CardRadius;
        _icon = new WinoIconView(glyph, 14);
        _label.Text = title ?? string.Empty;
        _label.Hidden = string.IsNullOrEmpty(title);
        var stack = WinoLayout.Insets(WinoLayout.HStack(6, _icon, _label, _chevron), 0, 12, 0, 12);
        stack.UserInteractionEnabled = false;
        WinoLayout.Fill(stack, this);
        // A transparent menu button over the chip: UIKit then owns the menu presentation and its accessibility.
        _menuButton = new UIButton { ShowsMenuAsPrimaryAction = true, Hidden = true };
        WinoLayout.Fill(_menuButton, this);
        HeightAnchor.ConstraintEqualTo((nfloat)WinoToDoStyle.ChipHeight).Active = true;
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 750, 760);
        AccessibilityLabel = accessibilityLabel ?? title;
        _menuButton.AccessibilityLabel = AccessibilityLabel;
        WinoStyle.AccentChanged += AccentChanged;
        Refresh();
    }

    public string Title
    {
        get => _label.Text ?? string.Empty;
        set { _label.Text = value ?? string.Empty; _label.Hidden = string.IsNullOrEmpty(value); }
    }

    public WinoIconGlyph Glyph { set => _icon.Icon = value; }

    /// <summary>Opens from the chip on a tap.</summary>
    public UIMenu? Menu
    {
        get => _menuButton.Menu;
        set
        {
            _menuButton.Menu = value;
            _menuButton.Hidden = value is null;
            _chevron.Hidden = value is null;
        }
    }

    public bool Checked
    {
        get => _checked;
        set { _checked = value; Refresh(); }
    }

    protected override void OnEnabledChanged() { Alpha = Enabled ? 1 : 0.5f; _menuButton.Enabled = Enabled; Refresh(); }

    protected override void OnHoverChanged() => Refresh();

    private void AccentChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var accent = WinoStyle.Accent;
        Fill = _checked ? accent.ColorWithAlpha(0.14f) : IsHovered && Enabled ? WinoToDoStyle.HoverFill : WinoToDoStyle.CardFill;
        Stroke = _checked ? accent.ColorWithAlpha(0.6f) : WinoToDoStyle.CardStroke;
        _icon.Tint = _checked ? accent : null;
        _label.TextColor = _checked ? accent : WinoStyle.PrimaryText;
        AccessibilityTraits = _checked ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

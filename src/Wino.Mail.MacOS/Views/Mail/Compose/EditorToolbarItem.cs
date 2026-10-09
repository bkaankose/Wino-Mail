using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// One command in an <see cref="EditorOverflowRow"/>: the view shown in the row and, like
/// <c>NSToolbarItem.menuFormRepresentation</c>, a factory for the menu item shown in the "More" menu when
/// the row is too narrow. The factory runs each time the menu opens, so check marks are current.
/// </summary>
internal sealed class EditorToolbarItem
{
    public EditorToolbarItem(NSView view, Func<NSMenuItem?>? menuItem, Func<bool>? isAvailable = null)
    {
        View = view;
        MenuItem = menuItem;
        IsAvailable = isAvailable;
    }

    public NSView View { get; }

    /// <summary>The overflow menu form; null leaves the command out of the menu.</summary>
    public Func<NSMenuItem?>? MenuItem { get; }

    /// <summary>False keeps the item hidden whatever the width (for example S/MIME without certificates support).</summary>
    public Func<bool>? IsAvailable { get; }

    public bool IsDivider { get; private init; }

    /// <summary>A vertical hairline between groups; it never dangles at either end of the row.</summary>
    public static EditorToolbarItem Divider()
    {
        var divider = new WinoSeparator(vertical: true);
        divider.HeightAnchor.ConstraintEqualTo(18).Active = true;
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(divider);
        NSLayoutConstraint.ActivateConstraints(
        [
            host.WidthAnchor.ConstraintEqualTo(9),
            host.HeightAnchor.ConstraintEqualTo(18),
            divider.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            divider.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor)
        ]);
        return new EditorToolbarItem(host, null) { IsDivider = true };
    }
}

/// <summary>
/// A 24×26 icon button whose pressed state is a subtle filled backplate (aria-pressed in the design).
/// Used for toggles, plain commands and the buttons that open a menu.
/// </summary>
internal sealed class EditorFormatButton : WinoSurfaceView
{
    private readonly NSButton _button;
    private bool _active;

    public EditorFormatButton(WinoIconGlyph glyph, string label, Action action, string? tooltip = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        CornerRadius = 5;
        Label = label;
        _button = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            Image = WinoIcons.Image(glyph, 14, null, label),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.PrimaryText,
            ToolTip = tooltip ?? label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(_button, label);
        _button.Activated += (_, _) => action();
        WinoLayout.Fill(_button, this);
        WinoLayout.Size(this, 24, 26);
    }

    public string Label { get; }

    public NSButton Button => _button;

    public void SetGlyph(WinoIconGlyph glyph, string? tooltip = null)
    {
        _button.Image = WinoIcons.Image(glyph, 14, null, Label);
        if (tooltip is not null) _button.ToolTip = tooltip;
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Fill = value ? WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.12)) : null;
            _button.AccessibilityValue = Foundation.NSNumber.FromBoolean(value);
        }
    }

    /// <summary>Contextual commands grey out in place rather than hide, so the row never jumps under the pointer.</summary>
    public bool Enabled
    {
        get => _button.Enabled;
        set => _button.Enabled = value;
    }

    /// <summary>Opens <paramref name="menu"/> under the button; the menu is discarded when it closes.</summary>
    public void PopUp(NSMenu menu) => menu.PopUpMenu(null, new CGPoint(0, Bounds.Height + 4), this);
}

/// <summary>
/// One toolbar row that never clips: items are shown leading to trailing while they fit and the rest
/// move into a trailing "More" (…) menu, the way a Windows CommandBar overflows. Widths are measured
/// with <c>FittingSize</c> and only recomputed when the row width or the item set changes.
/// </summary>
internal sealed class EditorOverflowRow : NSView
{
    private const double Spacing = 1;
    private readonly NSStackView _stack;
    private readonly EditorFormatButton _more;
    private readonly List<EditorToolbarItem> _items = [];
    private readonly List<EditorToolbarItem> _overflow = [];
    private double _lastWidth = -1;
    private bool _dirty = true;

    public EditorOverflowRow(string moreLabel)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = (nfloat)Spacing,
            Alignment = NSLayoutAttribute.CenterY,
            DetachesHiddenViews = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _stack.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _stack.SetHuggingPriority(750, NSLayoutConstraintOrientation.Horizontal);
        _more = new EditorFormatButton(WinoIconGlyph.More, moreLabel, ShowOverflowMenu) { Hidden = true };
        AddSubview(_stack);
        AddSubview(_more);
        var trailing = _stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintEqualTo(28),
            _stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            trailing,
            _more.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _more.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
    }

    public IReadOnlyList<EditorToolbarItem> Items => _items;

    public IReadOnlyList<EditorToolbarItem> OverflowItems => _overflow;

    public bool IsOverflowing => !_more.Hidden;

    public void SetItems(IEnumerable<EditorToolbarItem> items)
    {
        foreach (var view in _stack.ArrangedSubviews)
        {
            _stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        _items.Clear();
        _items.AddRange(items);
        foreach (var item in _items) _stack.AddArrangedSubview(item.View);
        Invalidate();
    }

    /// <summary>Re-measures on the next layout, for example after an item changed its width or availability.</summary>
    public void Invalidate()
    {
        _dirty = true;
        NeedsLayout = true;
    }

    public override void Layout()
    {
        var width = Bounds.Width;
        if (_dirty || Math.Abs(width - _lastWidth) > 0.5)
        {
            _dirty = false;
            _lastWidth = width;
            Recompute(width);
        }
        base.Layout();
    }

    private void Recompute(double available)
    {
        _overflow.Clear();
        var candidates = new List<EditorToolbarItem>();
        foreach (var item in _items)
        {
            if (item.IsAvailable is { } available1 && !available1()) item.View.Hidden = true;
            else candidates.Add(item);
        }

        static double WidthOf(EditorToolbarItem item) => Math.Max(0, (double)item.View.FittingSize.Width);
        double total = 0;
        foreach (var item in candidates) total += WidthOf(item) + (total > 0 ? Spacing : 0);

        var shown = new List<EditorToolbarItem>();
        if (available <= 0 || total <= available)
        {
            shown.AddRange(candidates);
        }
        else
        {
            var budget = available - 24 - 4;
            double used = 0;
            int index = 0;
            for (; index < candidates.Count; index++)
            {
                var width = WidthOf(candidates[index]) + (used > 0 ? Spacing : 0);
                if (used + width > budget) break;
                used += width;
                shown.Add(candidates[index]);
            }
            for (; index < candidates.Count; index++) _overflow.Add(candidates[index]);
        }

        // Dividers never lead, trail or double up.
        while (shown.Count > 0 && shown[^1].IsDivider) { _overflow.Insert(0, shown[^1]); shown.RemoveAt(shown.Count - 1); }
        EditorToolbarItem? previous = null;
        foreach (var item in candidates)
        {
            bool visible = shown.Contains(item);
            if (visible && item.IsDivider && (previous is null || previous.IsDivider)) visible = false;
            item.View.Hidden = !visible;
            if (visible) previous = item;
        }
        _more.Hidden = !_overflow.Any(static item => !item.IsDivider && item.MenuItem is not null);
    }

    /// <summary>Builds the overflow menu now; it lives only while it is open.</summary>
    public void ShowOverflowMenu()
    {
        var menu = BuildOverflowMenu();
        if (menu.Count > 0) _more.PopUp(menu);
    }

    public NSMenu BuildOverflowMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        bool pendingSeparator = false;
        foreach (var item in _overflow)
        {
            if (item.IsDivider)
            {
                pendingSeparator = menu.Count > 0;
                continue;
            }
            if (item.MenuItem?.Invoke() is not { } menuItem) continue;
            if (pendingSeparator) menu.AddItem(NSMenuItem.SeparatorItem);
            pendingSeparator = false;
            menu.AddItem(menuItem);
        }
        return menu;
    }
}

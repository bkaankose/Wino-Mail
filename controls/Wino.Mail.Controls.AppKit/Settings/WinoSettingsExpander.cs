using AppKit;
using CoreAnimation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// macOS counterpart of the Windows SettingsExpander: a header card with a chevron, and nested
/// rows on a subtle fill under it, indented to the header text column and separated by hairlines.
/// Expansion toggles visibility only; nested rows keep their bindings.
/// </summary>
public sealed class WinoSettingsExpander : NSView
{
    private readonly WinoSettingsCard _header;
    private readonly WinoSurfaceView _itemsSurface;
    private readonly NSStackView _items;
    private bool _isExpanded;
    private bool _isEnabled = true;

    public WinoSettingsExpander(string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, NSView? content = null, bool isExpanded = false)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _header = new WinoSettingsCard(header, description, icon, content) { IsClickable = true, IsExpanderHeader = true };
        _header.Activated += (_, _) => IsExpanded = !IsExpanded;

        _itemsSurface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.SubtleFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            Corners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner
        };
        _items = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(_items, _itemsSurface);

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        stack.AddArrangedSubview(_header);
        stack.AddArrangedSubview(_itemsSurface);
        _header.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        _itemsSurface.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, this);

        IsExpanded = isExpanded;
        AccessibilityElement = false;
    }

    public WinoSettingsCard HeaderCard => _header;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            _itemsSurface.Hidden = !value;
            _header.IsExpanded = value;
            IsExpandedChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? IsExpandedChanged;

    /// <summary>Disables the header content and every nested row; the header stays clickable so rows remain readable.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            _header.SetContentEnabled(value);
            foreach (var view in _items.ArrangedSubviews) SetRowsEnabled(view, value);
        }
    }

    private static void SetRowsEnabled(NSView view, bool enabled)
    {
        switch (view)
        {
            case WinoSettingsCard card: card.IsEnabled = enabled; return;
            case WinoLabeledSwitch toggle: toggle.IsEnabled = enabled; return;
            case NSControl control: control.Enabled = enabled; return;
        }
        foreach (var child in view.Subviews) SetRowsEnabled(child, enabled);
    }

    /// <summary>Adds a nested row. Cards become flat rows indented to the header text; other views get the same insets.</summary>
    public void Add(NSView row)
    {
        if (_items.ArrangedSubviews.Length > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            _items.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
        }

        row.TranslatesAutoresizingMaskIntoConstraints = false;
        if (row is WinoSettingsCard card)
        {
            card.IsNested = true;
            _items.AddArrangedSubview(card);
            card.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
            return;
        }

        AddHosted(row, 10, WinoSettingsStyle.NestedIndent, 10, WinoSettingsStyle.CardPadding);
    }

    /// <summary>Adds a nested row with custom insets (rows that carry their own leading picture, item lists).</summary>
    public void Add(NSView row, double top, double leading, double bottom, double trailing)
    {
        if (_items.ArrangedSubviews.Length > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            _items.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
        }
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        AddHosted(row, top, leading, bottom, trailing);
    }

    private void AddHosted(NSView row, double top, double leading, double bottom, double trailing)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, top, leading, bottom, trailing);
        _items.AddArrangedSubview(host);
        host.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
    }
}

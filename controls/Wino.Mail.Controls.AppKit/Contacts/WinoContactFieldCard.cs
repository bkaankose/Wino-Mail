using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Contacts;

/// <summary>
/// Read-only field card in the contact detail pane (Windows ContactsPage detail Border): radius 8,
/// 1 pt card stroke, padding 14×12, a tertiary caption title and value rows that carry an optional
/// Wino glyph, the value (selectable) and a caption label beneath it.
/// </summary>
public sealed class WinoContactFieldCard : WinoSurfaceView
{
    private readonly NSStackView _items;

    public WinoContactFieldCard(string title)
    {
        CornerRadius = 8;
        Fill = WinoContactStyle.CardFill;
        Stroke = WinoContactStyle.CardStroke;
        var header = WinoStyle.Label(title, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        _items = WinoLayout.VStack(6);
        var stack = WinoLayout.VStack(8, header, _items);
        WinoLayout.Fill(stack, this, 12, 14, 12, 14);
        NSLayoutConstraint.ActivateConstraints([_items.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor)]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        AccessibilityLabel = title;
    }

    public int Count => _items.ArrangedSubviews.Length;

    /// <summary>Adds one value row. Empty values are skipped so callers can add optional fields freely.</summary>
    public WinoContactFieldCard Add(string? value, string? label = null, WinoIconGlyph glyph = WinoIconGlyph.None, bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        var text = WinoStyle.Label(value, WinoStyle.Body, maximumLines: wrap ? 0 : 1);
        text.Selectable = true;
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var column = WinoLayout.VStack(1, text);
        column.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        if (!string.IsNullOrWhiteSpace(label)) column.AddArrangedSubview(WinoStyle.Label(label, WinoStyle.Caption, WinoStyle.TertiaryText));
        NSView row = column;
        if (glyph != WinoIconGlyph.None)
        {
            var icon = new WinoIconView(glyph, 14, WinoStyle.TertiaryText);
            var line = WinoLayout.HStack(10, icon, column);
            line.Alignment = NSLayoutAttribute.Top;
            icon.TopAnchor.ConstraintEqualTo(line.TopAnchor, 2).Active = true;
            row = line;
        }
        row.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _items.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(_items.WidthAnchor).Active = true;
        return this;
    }
}

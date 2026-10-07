using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Extras;

/// <summary>
/// The briefing's day strip (Windows FlipView, 40pt, rules above and below). Index 0 is the
/// newest day, so the right arrow goes to an older day and the left arrow to a newer one.
/// </summary>
public sealed class WinoDatePager : NSView
{
    private readonly NSButton _newer;
    private readonly NSButton _older;
    private readonly NSTextField _label;
    private IReadOnlyList<string> _items = [];
    private int _selectedIndex = -1;

    public WinoDatePager(string newerTooltip, string olderTooltip)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo(40).Active = true;
        _newer = Arrow(WinoIconGlyph.ArrowLeft, newerTooltip, () => SelectedIndex = _selectedIndex - 1);
        _older = Arrow(WinoIconGlyph.ArrowRight, olderTooltip, () => SelectedIndex = _selectedIndex + 1);
        _label = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        _label.Alignment = NSTextAlignment.Center;
        var row = WinoLayout.HStack(8, _newer, _label, _older);
        row.Distribution = NSStackViewDistribution.Fill;
        row.EdgeInsets = new NSEdgeInsets(0, 4, 0, 4);
        _label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        WinoLayout.Fill(row, this);
        var top = new WinoSeparator { Fill = WinoBriefingCardView.CardStroke };
        var bottom = new WinoSeparator { Fill = WinoBriefingCardView.CardStroke };
        AddSubview(top);
        AddSubview(bottom);
        NSLayoutConstraint.ActivateConstraints(
        [
            top.LeadingAnchor.ConstraintEqualTo(LeadingAnchor), top.TrailingAnchor.ConstraintEqualTo(TrailingAnchor), top.TopAnchor.ConstraintEqualTo(TopAnchor),
            bottom.LeadingAnchor.ConstraintEqualTo(LeadingAnchor), bottom.TrailingAnchor.ConstraintEqualTo(TrailingAnchor), bottom.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
        UpdateArrows();
    }

    /// <summary>Raised after a user action changed <see cref="SelectedIndex"/>.</summary>
    public event EventHandler? SelectedIndexChanged;

    public void SetItems(IReadOnlyList<string> items)
    {
        _items = items;
        if (_selectedIndex >= items.Count) _selectedIndex = items.Count - 1;
        Refresh();
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            Refresh();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the index from the ViewModel without raising <see cref="SelectedIndexChanged"/>.</summary>
    public void Select(int index)
    {
        _selectedIndex = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);
        Refresh();
    }

    private void Refresh()
    {
        _label.StringValue = _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : string.Empty;
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        _newer.Enabled = _selectedIndex > 0;
        _older.Enabled = _selectedIndex >= 0 && _selectedIndex < _items.Count - 1;
    }

    private static NSButton Arrow(WinoIconGlyph glyph, string tooltip, Action action)
    {
        var button = new NSButton
        {
            Bordered = false,
            Image = WinoIcons.Image(glyph, 12),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = tooltip,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, 32, 32);
        button.Activated += (_, _) => action();
        return button;
    }
}

using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>One folder pivot: its title and the selected-item count shown as "(n)" when positive.</summary>
public sealed record WinoPivotItem(string Title, int Count);

/// <summary>
/// The mail list's folder pivot (Windows PivotSegmentedStyle), e.g. "Focused (2) | Other", as a native
/// select-one <see cref="NSSegmentedControl"/>: AppKit owns hit testing, keyboard navigation, selection
/// drawing and the radio-group accessibility. Clicking a segment raises <see cref="SelectionChanged"/>;
/// the owner sets <see cref="SelectedIndex"/> back.
/// </summary>
public sealed class WinoPivotBar : NSView
{
    private readonly NSSegmentedControl _segments = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false,
        TrackingMode = NSSegmentSwitchTracking.SelectOne,
        SegmentStyle = NSSegmentStyle.Automatic,
        ControlSize = NSControlSize.Regular
    };

    public WinoPivotBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(_segments);
        NSLayoutConstraint.ActivateConstraints(
        [
            _segments.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _segments.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor),
            _segments.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            HeightAnchor.ConstraintEqualTo(30)
        ]);
        _segments.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _segments.Activated += SegmentActivated;
    }

    /// <summary>Raised with the index the user clicked; the owner sets <see cref="SelectedIndex"/> back.</summary>
    public event EventHandler<int>? SelectionChanged;

    public int SelectedIndex
    {
        get => (int)_segments.SelectedSegment;
        set => _segments.SelectedSegment = value >= 0 && value < _segments.SegmentCount ? value : -1;
    }

    public void SetItems(IReadOnlyList<WinoPivotItem> items, int selectedIndex)
    {
        _segments.SegmentCount = items.Count;
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            _segments.SetLabel(item.Count > 0 ? $"{item.Title} ({item.Count})" : item.Title, index);
            _segments.SetWidth(0, index);
        }
        SelectedIndex = selectedIndex;
    }

    private void SegmentActivated(object? sender, EventArgs args)
    {
        var index = (int)_segments.SelectedSegment;
        if (index >= 0) SelectionChanged?.Invoke(this, index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _segments.Activated -= SegmentActivated;
            SelectionChanged = null;
        }
        base.Dispose(disposing);
    }
}

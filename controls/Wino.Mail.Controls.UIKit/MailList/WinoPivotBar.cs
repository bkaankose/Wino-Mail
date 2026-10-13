using UIKit;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>One folder pivot: its title and the selected-item count shown as "(n)" when positive.</summary>
public sealed record WinoPivotItem(string Title, int Count);

/// <summary>
/// The mail list's folder pivot (Windows PivotSegmentedStyle; UIKit twin of the AppKit <c>WinoPivotBar</c>), e.g.
/// "Focused (2) | Other", as a native <see cref="UISegmentedControl"/>: UIKit owns touch handling, selection drawing and
/// accessibility. A tap raises <see cref="SelectionChanged"/>; the owner sets <see cref="SelectedIndex"/> back.
/// </summary>
public sealed class WinoPivotBar : UIView
{
    private readonly UISegmentedControl _segments = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    public WinoPivotBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(_segments);
        NSLayoutConstraint.ActivateConstraints(
        [
            _segments.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _segments.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor),
            _segments.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            HeightAnchor.ConstraintEqualTo(36)
        ]);
        _segments.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        _segments.ValueChanged += SegmentChanged;
    }

    /// <summary>Raised with the index the user tapped; the owner sets <see cref="SelectedIndex"/> back.</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>Stretches the segments across the bar (phone) instead of sizing them to their titles.</summary>
    public bool FillsWidth
    {
        get => !_segments.ApportionsSegmentWidthsByContent;
        set
        {
            _segments.ApportionsSegmentWidthsByContent = !value;
            if (value) _segments.TrailingAnchor.ConstraintEqualTo(TrailingAnchor).Active = true;
        }
    }

    public int SelectedIndex
    {
        get => (int)_segments.SelectedSegment;
        set => _segments.SelectedSegment = value >= 0 && value < (int)_segments.NumberOfSegments ? value : -1;
    }

    public void SetItems(IReadOnlyList<WinoPivotItem> items, int selectedIndex)
    {
        _segments.RemoveAllSegments();
        for (int index = 0; index < items.Count; index++)
            _segments.InsertSegment(WinoControlText.WithCount(items[index].Title, items[index].Count), index, false);
        SelectedIndex = selectedIndex;
    }

    private void SegmentChanged(object? sender, EventArgs args)
    {
        var index = (int)_segments.SelectedSegment;
        if (index >= 0) SelectionChanged?.Invoke(this, index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _segments.ValueChanged -= SegmentChanged;
            SelectionChanged = null;
        }
        base.Dispose(disposing);
    }
}

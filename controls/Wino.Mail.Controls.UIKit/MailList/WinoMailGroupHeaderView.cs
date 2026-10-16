using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Mail.Controls.Core;
using Wino.Presentation;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>
/// Date group header ("Today", "Yesterday"; UIKit twin of the AppKit <c>WinoMailGroupHeaderView</c>): on mobile a plain
/// semibold label with no strip and no background (owner decision 15 Oct 2026), aligned with the row content box and a
/// 6pt gap below. A plain table pins its section headers; with no background a pinned label would sit over the rows,
/// so the label hides while the table holds the header above its natural place and the headers read as scrolling rows.
/// <see cref="WinoMailRowMeasure.GroupRowHeight"/> is its height.
/// </summary>
public class WinoMailGroupHeaderView : UITableViewHeaderFooterView
{
    public new const string ReuseIdentifier = "WinoMailGroupHeader";

    private readonly UILabel _title;
    private readonly bool _constructed;

    public WinoMailGroupHeaderView() : this(new NSString(ReuseIdentifier)) { }

    [Export("initWithReuseIdentifier:")]
    public WinoMailGroupHeaderView(NSString reuseIdentifier) : base(reuseIdentifier)
    {
        // The system header background (a blur in plain tables) would cover the rows under a pinned header.
        BackgroundConfiguration = UIBackgroundConfiguration.ClearConfiguration;
        _title = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.GroupHeaderFontSize, UIFontWeight.Semibold), WinoStyle.SecondaryText);
        ContentView.AddSubview(_title);
        NSLayoutConstraint.ActivateConstraints(
        [
            _title.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, (nfloat)(WinoMailRowMetrics.ContentLeading + 12)),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -(nfloat)(WinoMailRowMetrics.ContentTrailing + 12)),
            _title.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor, -3)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Header;
        _constructed = true;
    }

    /// <summary>Kept for callers that refresh visible headers on theme changes; the plain label follows the system colours.</summary>
    public void RefreshTheme() { }

    /// <summary>The table moves a pinned header on every scroll step; that is when the label shows or hides.</summary>
    public override CGRect Frame
    {
        get => base.Frame;
        set
        {
            base.Frame = value;
            UpdatePinned();
        }
    }

    private void UpdatePinned()
    {
        // UIView's initializer sets the frame before this constructor has created the label.
        if (!_constructed) return;
        if (Superview is not UITableView table) { _title.Alpha = 1; return; }
        bool pinned = false;
        for (nint section = 0; section < table.NumberOfSections(); section++)
        {
            if (!ReferenceEquals(table.GetHeaderView(section), this)) continue;
            pinned = Frame.Y > table.RectForSection(section).Y + 0.5;
            break;
        }
        _title.Alpha = pinned ? 0 : 1;
    }

    public string Title
    {
        get => _title.Text ?? string.Empty;
        set { _title.Text = value ?? string.Empty; AccessibilityLabel = value; }
    }
}

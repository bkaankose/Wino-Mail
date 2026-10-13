using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Extras;

/// <summary>
/// A centred empty, unavailable or error state (UIKit twin of the AppKit <c>WinoStateView</c>; Windows: 26pt glyph,
/// strong title, wrapped caption, optional button; spacing 8, margin 24). Add it to a container with <see cref="Place"/>
/// and toggle <see cref="UIView.Hidden"/>.
/// </summary>
public sealed class WinoStateView : UIView
{
    private readonly UIStackView _stack;

    public WinoStateView(WinoIconGlyph glyph, string title, string? caption, UIColor? tint = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Icon = new WinoIconView(glyph, 32, tint);
        Title = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        Title.TextAlignment = UITextAlignment.Center;
        Caption = WinoStyle.Label(caption, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        Caption.TextAlignment = UITextAlignment.Center;
        Caption.Hidden = string.IsNullOrEmpty(caption);
        _stack = WinoLayout.VStack(8, Icon, Title, Caption);
        _stack.Alignment = UIStackViewAlignment.Center;
        AddSubview(_stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            _stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            _stack.WidthAnchor.ConstraintLessThanOrEqualTo(320)
        ]);
    }

    public WinoIconView Icon { get; }
    public UILabel Title { get; }
    public UILabel Caption { get; }

    /// <summary>Adds a control under the caption (Retry).</summary>
    public new void Add(UIView view) => _stack.AddArrangedSubview(view);

    /// <summary>Replaces the icon with a spinner, for loading states.</summary>
    public UIActivityIndicatorView UseSpinner(double size = 32)
    {
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large) { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(spinner, size, size);
        spinner.StartAnimating();
        _stack.InsertArrangedSubview(spinner, 0);
        Icon.RemoveFromSuperview();
        return spinner;
    }

    public void Place(UIView parent) => WinoLayout.Fill(this, parent);
}

/// <summary>
/// The briefing's day strip (Windows FlipView, 44pt, rules above and below; UIKit twin of the AppKit
/// <c>WinoDatePager</c>). Index 0 is the newest day, so the trailing arrow goes to an older day and the leading arrow to
/// a newer one (shared <see cref="WinoPagerState"/>). Swipes page as well.
/// </summary>
public sealed class WinoDatePager : UIView
{
    private readonly UIButton _newer;
    private readonly UIButton _older;
    private readonly UILabel _label;
    private IReadOnlyList<string> _items = Array.Empty<string>();
    private int _selectedIndex = -1;

    public WinoDatePager(string newerTooltip, string olderTooltip)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo(44).Active = true;
        _newer = Arrow(WinoIconGlyph.ArrowLeft, newerTooltip, () => SelectedIndex = _selectedIndex - 1);
        _older = Arrow(WinoIconGlyph.ArrowRight, olderTooltip, () => SelectedIndex = _selectedIndex + 1);
        _label = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        _label.TextAlignment = UITextAlignment.Center;
        _label.AccessibilityTraits = UIAccessibilityTrait.Header;
        var row = WinoLayout.Insets(WinoLayout.HStack(8, _newer, _label, _older), 0, 4, 0, 4);
        row.Distribution = UIStackViewDistribution.Fill;
        _label.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
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
        AddGestureRecognizer(new UISwipeGestureRecognizer(() => SelectedIndex = _selectedIndex + 1) { Direction = UISwipeGestureRecognizerDirection.Left });
        AddGestureRecognizer(new UISwipeGestureRecognizer(() => SelectedIndex = _selectedIndex - 1) { Direction = UISwipeGestureRecognizerDirection.Right });
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
            var clamped = WinoPagerState.Clamp(value, _items.Count);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            Refresh();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the index from the ViewModel without raising <see cref="SelectedIndexChanged"/>.</summary>
    public void Select(int index)
    {
        _selectedIndex = WinoPagerState.Clamp(index, _items.Count);
        Refresh();
    }

    private void Refresh()
    {
        _label.Text = _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : string.Empty;
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        _newer.Enabled = WinoPagerState.CanGoNewer(_selectedIndex);
        _older.Enabled = WinoPagerState.CanGoOlder(_selectedIndex, _items.Count);
    }

    private static UIButton Arrow(WinoIconGlyph glyph, string tooltip, Action action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 14, null, tooltip);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, 44, 44);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }
}

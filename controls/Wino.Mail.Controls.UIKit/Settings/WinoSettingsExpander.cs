using CoreAnimation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The Windows SettingsExpander on UIKit (twin of the AppKit <c>WinoSettingsExpander</c>): a header card with a chevron,
/// and nested rows on a subtle fill under it. Rows use the full width (no desktop indent) and are separated by hairlines.
/// Expansion toggles visibility only; nested rows keep their bindings. On a settings page (<see cref="IsListRow"/>) it
/// is one push row instead: nothing expands in place, and its items become the rows of the generic
/// <see cref="WinoSettingDetailViewController"/>.
/// </summary>
public sealed class WinoSettingsExpander : UIView
{
    private readonly WinoSettingsCard _header;
    private readonly WinoSurfaceView _itemsSurface;
    private readonly UIStackView _items;
    private readonly UIStackView _stack;
    private readonly List<WinoSettingsExpanderEntry> _entries = [];
    private bool _isExpanded;
    private bool _isEnabled = true;
    private bool _listMode;

    public WinoSettingsExpander(string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, UIView? content = null, bool isExpanded = false)
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
        _items = WinoLayout.VStack(0);
        _items.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(_items, _itemsSurface);

        _stack = WinoLayout.VStack(0, _header, _itemsSurface);
        _stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(_stack, this);

        IsExpanded = isExpanded;
    }

    public WinoSettingsCard HeaderCard => _header;

    /// <summary>The nested rows in the order they were added (the detail page's content in list mode).</summary>
    internal IReadOnlyList<WinoSettingsExpanderEntry> Entries => _entries;

    /// <summary>Renders the expander as one settings-page push row (one way).</summary>
    public bool IsListRow
    {
        get => _listMode;
        set
        {
            if (!value || _listMode) return;
            _listMode = true;
            _stack.RemoveArrangedSubview(_itemsSurface);
            _itemsSurface.RemoveFromSuperview();
            _header.ExpanderDetail = () => WinoSettingDetailViewController.Push(_header, WinoSettingDetailViewController.ForExpander(this));
            _header.IsListRow = true;
            _header.IsEnabled = _isEnabled;
        }
    }

    /// <summary>
    /// An expander inside a <see cref="WinoSettingsGroup"/>: the header is a grouped row and the nested rows sit on the
    /// subtle fill without their own stroke or corners (the group's surface clips them).
    /// </summary>
    public bool IsGrouped
    {
        get => _header.IsGrouped;
        set
        {
            _header.IsGrouped = value;
            _itemsSurface.Stroke = value ? null : WinoSettingsStyle.CardStroke;
            _itemsSurface.CornerRadius = value ? 0 : WinoSettingsStyle.CardRadius;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            if (_listMode) return;
            _itemsSurface.Hidden = !value;
            _header.IsExpanded = value;
            IsExpandedChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? IsExpandedChanged;

    /// <summary>Disables the header content and every nested row; the header stays tappable so rows remain readable.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (_listMode) _header.IsEnabled = value;
            else _header.SetContentEnabled(value);
            foreach (var view in _items.ArrangedSubviews) SettingsBinder.SetEnabled(view, value);
        }
    }

    /// <summary>Adds a nested row. Cards become flat full-width rows; other views get the card padding.</summary>
    public new void Add(UIView row)
    {
        AddSeparatorIfNeeded();
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        _entries.Add(new WinoSettingsExpanderEntry(row, null));
        if (row is WinoSettingsCard card)
        {
            if (_listMode) return;
            card.IsNested = true;
            _items.AddArrangedSubview(card);
            return;
        }

        if (_listMode) return;
        AddHosted(row, 10, WinoSettingsStyle.CardPadding, 10, WinoSettingsStyle.CardPadding);
    }

    /// <summary>Adds a nested row with custom insets (rows that carry their own leading picture, item lists).</summary>
    public void Add(UIView row, double top, double leading, double bottom, double trailing)
    {
        AddSeparatorIfNeeded();
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        _entries.Add(new WinoSettingsExpanderEntry(row, new UIEdgeInsets((nfloat)top, (nfloat)leading, (nfloat)bottom, (nfloat)trailing)));
        if (_listMode) return;
        AddHosted(row, top, leading, bottom, trailing);
    }

    private void AddSeparatorIfNeeded()
    {
        if (_listMode || _items.ArrangedSubviews.Length == 0) return;
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(new WinoSeparator { Fill = WinoSettingsStyle.CardStroke }, host, 0, WinoSettingsStyle.CardPadding, 0, 0);
        _items.AddArrangedSubview(host);
    }

    private void AddHosted(UIView row, double top, double leading, double bottom, double trailing)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, top, leading, bottom, trailing);
        _items.AddArrangedSubview(host);
    }
}

/// <summary>
/// A titled run of settings rows (UIKit twin of the AppKit <c>WinoSettingsGroup</c>): the 12pt semibold upper-case
/// section header, then the rows on one inset-grouped surface with hairlines, and an optional 13pt footer. Cards and
/// expanders become settings rows (<see cref="WinoSettingsCard.IsListRow"/>); other views (previews, info bars) stand
/// on their own between the surfaces. Without a title it is only the rows.
/// </summary>
public class WinoSettingsGroup : UIView
{
    private const double LooseSpacing = 8;

    private readonly UIStackView _rows;
    private readonly UILabel _title;
    private readonly UILabel _footer;
    private readonly UIView _titleWrapper;
    private readonly UIView _footerWrapper;
    private int _rowCount;

    public WinoSettingsGroup(string? title = null, string? footer = null, bool isInsetGrouped = false)
    {
        _ = isInsetGrouped;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _title = WinoStyle.Label(title?.ToUpper(System.Globalization.CultureInfo.CurrentCulture), WinoSettingsStyle.SectionHeader, WinoStyle.SecondaryText, 0);
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _footer = WinoStyle.Label(footer, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);

        _rows = WinoLayout.VStack(LooseSpacing);
        _rows.Alignment = UIStackViewAlignment.Fill;

        // The page stack adds the space between groups; the header sits at the foot of that gap, aligned with the row text.
        _titleWrapper = Inset(_title, 0, WinoSettingsStyle.CardPadding, 6);
        _footerWrapper = Inset(_footer, 6, WinoSettingsStyle.CardPadding, 0);
        _titleWrapper.Hidden = string.IsNullOrEmpty(title);
        _footerWrapper.Hidden = string.IsNullOrEmpty(footer);
        var outer = WinoLayout.VStack(0, _titleWrapper, _rows, _footerWrapper);
        outer.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(outer, this);
    }

    /// <summary>The row stack, for callers that need to hide or inspect it.</summary>
    public UIView Surface => _rows;

    public int RowCount => _rowCount;

    /// <summary>Rows always share one inset-grouped surface on iOS (kept for the AppKit-shaped API).</summary>
    public bool IsInsetGrouped { get => true; set { } }

    private static UIView Inset(UILabel label, double top, double leading, double bottom)
    {
        var wrapper = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, wrapper, top, leading, bottom, leading);
        return wrapper;
    }

    public string? Footer
    {
        get => _footer.Text;
        set { _footer.Text = value ?? string.Empty; _footerWrapper.Hidden = string.IsNullOrEmpty(value); }
    }

    public string? Title
    {
        get => _title.Text;
        set { _title.Text = value?.ToUpper(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty; _titleWrapper.Hidden = string.IsNullOrEmpty(value); }
    }

    /// <summary>Removes every row, for groups rebuilt from a collection.</summary>
    public void Clear()
    {
        foreach (var view in _rows.ArrangedSubviews)
        {
            _rows.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        _rowCount = 0;
    }

    /// <summary>
    /// Adds a row. Cards and expanders are separate cards, or join the current surface when the group is inset-grouped;
    /// other views stand on their own.
    /// </summary>
    public new void Add(UIView row)
    {
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        _rowCount++;
        switch (row)
        {
            case WinoSettingsCard card:
                card.IsListRow = true;
                card.IsGrouped = true;
                CurrentSection().AddRow(card);
                break;
            case WinoSettingsExpander expander:
                expander.IsListRow = true;
                expander.IsGrouped = true;
                CurrentSection().AddRow(expander);
                break;
            default:
                _rows.AddArrangedSubview(row);
                break;
        }
    }

    public void AddRange(params UIView[] rows)
    {
        foreach (var row in rows) Add(row);
    }

    private GroupedSection CurrentSection()
    {
        if (_rows.ArrangedSubviews.LastOrDefault() is GroupedSection section) return section;
        section = new GroupedSection();
        _rows.AddArrangedSubview(section);
        return section;
    }

    /// <summary>
    /// One inset-grouped surface: rows stacked without spacing, a hairline above every visible row after the first, and
    /// no surface at all while every row is hidden.
    /// </summary>
    private sealed class GroupedSection : WinoSurfaceView
    {
        private readonly SeparatorStack _stack = new();

        public GroupedSection()
        {
            Fill = WinoSettingsStyle.GroupFill;
            Stroke = WinoSettingsStyle.CardStroke;
            CornerRadius = WinoSettingsStyle.CardRadius;
            ClipsToBounds = true;
            WinoLayout.Fill(_stack, this);
        }

        public void AddRow(UIView row) => _stack.AddArrangedSubview(row);

        public void UpdateRows()
        {
            var first = true;
            foreach (var view in _stack.ArrangedSubviews)
            {
                var separated = !view.Hidden && !first;
                if (view is WinoSettingsCard card) card.ShowsTopSeparator = separated;
                else if (view is WinoSettingsExpander expander) expander.HeaderCard.ShowsTopSeparator = separated;
                if (!view.Hidden) first = false;
            }
            // An all-hidden surface would still draw its stroke as a line.
            Alpha = first ? 0 : 1;
        }
    }

    private sealed class SeparatorStack : UIStackView
    {
        public SeparatorStack()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Axis = UILayoutConstraintAxis.Vertical;
            Alignment = UIStackViewAlignment.Fill;
            Distribution = UIStackViewDistribution.Fill;
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            (Superview as GroupedSection)?.UpdateRows();
        }
    }
}

/// <summary>One nested row of an expander and the insets it was added with (null for the standard row padding).</summary>
internal sealed record WinoSettingsExpanderEntry(UIView View, UIEdgeInsets? Insets);

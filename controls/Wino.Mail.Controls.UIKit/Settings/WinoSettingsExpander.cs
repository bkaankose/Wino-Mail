using CoreAnimation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The Windows SettingsExpander on UIKit (twin of the AppKit <c>WinoSettingsExpander</c>): a header card with a chevron,
/// and nested rows on a subtle fill under it, indented to the header text column and separated by hairlines.
/// Expansion toggles visibility only; nested rows keep their bindings.
/// </summary>
public sealed class WinoSettingsExpander : UIView
{
    private readonly WinoSettingsCard _header;
    private readonly WinoSurfaceView _itemsSurface;
    private readonly UIStackView _items;
    private bool _isExpanded;
    private bool _isEnabled = true;

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

        var stack = WinoLayout.VStack(0, _header, _itemsSurface);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);

        IsExpanded = isExpanded;
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

    /// <summary>Disables the header content and every nested row; the header stays tappable so rows remain readable.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            _header.SetContentEnabled(value);
            foreach (var view in _items.ArrangedSubviews) SettingsBinder.SetEnabled(view, value);
        }
    }

    /// <summary>Adds a nested row. Cards become flat rows indented to the header text; other views get the same insets.</summary>
    public new void Add(UIView row)
    {
        AddSeparatorIfNeeded();
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        if (row is WinoSettingsCard card)
        {
            card.IsNested = true;
            _items.AddArrangedSubview(card);
            return;
        }

        AddHosted(row, 10, WinoSettingsStyle.NestedIndent, 10, WinoSettingsStyle.CardPadding);
    }

    /// <summary>Adds a nested row with custom insets (rows that carry their own leading picture, item lists).</summary>
    public void Add(UIView row, double top, double leading, double bottom, double trailing)
    {
        AddSeparatorIfNeeded();
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        AddHosted(row, top, leading, bottom, trailing);
    }

    private void AddSeparatorIfNeeded()
    {
        if (_items.ArrangedSubviews.Length == 0) return;
        _items.AddArrangedSubview(new WinoSeparator { Fill = WinoSettingsStyle.CardStroke });
    }

    private void AddHosted(UIView row, double top, double leading, double bottom, double trailing)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, top, leading, bottom, trailing);
        _items.AddArrangedSubview(host);
    }
}

/// <summary>
/// A titled run of settings cards (UIKit twin of the AppKit <c>WinoSettingsGroup</c>): the Windows section header
/// followed by cards stacked with 4pt spacing and an optional footer. Without a title it is only the card stack.
/// </summary>
public sealed class WinoSettingsGroup : UIView
{
    private readonly UIStackView _rows;
    private readonly UILabel _title;
    private readonly UILabel _footer;
    private readonly UIView _titleWrapper;
    private readonly UIView _footerWrapper;

    public WinoSettingsGroup(string? title = null, string? footer = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _title = WinoStyle.Label(title, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _footer = WinoStyle.Label(footer, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);

        _rows = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
        _rows.Alignment = UIStackViewAlignment.Fill;

        // The page stack adds the space between sections; the wrappers add the rest of 22 above / 6 below.
        _titleWrapper = Inset(_title, 18, 4, 4);
        _footerWrapper = Inset(_footer, 4, 4, 0);
        _titleWrapper.Hidden = string.IsNullOrEmpty(title);
        _footerWrapper.Hidden = string.IsNullOrEmpty(footer);
        var outer = WinoLayout.VStack(0, _titleWrapper, _rows, _footerWrapper);
        outer.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(outer, this);
    }

    /// <summary>The card stack, for callers that need to hide or inspect it.</summary>
    public UIView Surface => _rows;

    public int RowCount => _rows.ArrangedSubviews.Length;

    private static UIView Inset(UILabel label, double top, double leading, double bottom)
    {
        var wrapper = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, wrapper, top, leading, bottom, 0);
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
        set { _title.Text = value ?? string.Empty; _titleWrapper.Hidden = string.IsNullOrEmpty(value); }
    }

    /// <summary>Removes every row, for groups rebuilt from a collection.</summary>
    public void Clear()
    {
        foreach (var view in _rows.ArrangedSubviews)
        {
            _rows.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    public new void Add(UIView row)
    {
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        _rows.AddArrangedSubview(row);
    }

    public void AddRange(params UIView[] rows)
    {
        foreach (var row in rows) Add(row);
    }
}

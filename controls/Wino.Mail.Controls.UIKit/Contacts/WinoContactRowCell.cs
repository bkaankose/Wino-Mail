using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Contacts;

/// <summary>Colours shared by the contact list and card (UIKit twin of the AppKit <c>WinoContactStyle</c>).</summary>
public static class WinoContactStyle
{
    public static UIColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));

    public static UIColor HoverFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>Card surface in the contact card (Windows CardBackgroundFillColorDefault).</summary>
    public static UIColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));

    public static UIColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));

    /// <summary>Star image: filled in the caution colour when favourite, otherwise a tertiary outline. Always mono.</summary>
    public static UIImage StarImage(bool favorite, double size)
        => WinoIcons.MonoImage(favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, size, favorite ? WinoStyle.Caution : WinoStyle.TertiaryText);

    /// <summary>Contacts board (15 Oct 2026): 58pt rows with a 36pt avatar, name over one secondary line.</summary>
    public const double RowHeight = 58;
    public const double HeaderRowHeight = 30;
    public const double AvatarSize = 36;
    public const double RowRadius = 8;
}

/// <summary>Presentation snapshot of one contact row, read from the shared contact ViewModel by the page.</summary>
public sealed record WinoContactRowModel(string? Name, string? SecondaryValue, string? SourceLabel, string? Address,
    bool IsFavorite, IReadOnlyList<MailCategory> Categories, UIImage? Picture);

/// <summary>
/// One contact row (Windows ContactTemplate; UIKit twin of the AppKit <c>WinoContactRowView</c>) in the iOS canvas look:
/// 36pt avatar, the name (16) over the secondary value (email or phone, 13) with the category chips, the source label
/// trailing (12) and the star toggle that is always present. Rounded selection fill, no separators.
/// </summary>
public sealed class WinoContactRowCell : UITableViewCell
{
    public new const string ReuseIdentifier = "WinoContactRow";

    private readonly WinoSurfaceView _fill = new() { CornerRadius = WinoContactStyle.RowRadius, Hidden = true };
    private readonly WinoContactPicture _picture = new(WinoContactStyle.AvatarSize);
    private readonly UILabel _name = WinoStyle.Label(string.Empty, WinoStyle.Scaled(16));
    private readonly UILabel _secondary = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly UILabel _source = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly UIStackView _meta;
    private readonly UIButton _star;
    private UIStackView? _chips;
    private readonly List<(string? Name, string? Color)> _chipKeys = new();
    private bool? _shownFavorite;
    private INotifyPropertyChanged? _bound;
    private Func<WinoContactRowModel>? _read;
    private Action? _toggleFavorite;

    public WinoContactRowCell() : this(UITableViewCellStyle.Default, new NSString(ReuseIdentifier)) { }

    [Export("initWithStyle:reuseIdentifier:")]
    public WinoContactRowCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier)
    {
#if DEBUG
        Wino.Presentation.UIKit.WinoLiveObjects.Track(this);
#endif
        SelectionStyle = UITableViewCellSelectionStyle.None;
        BackgroundColor = UIColor.Clear;
        WinoLayout.Fill(_fill, ContentView, 1, 4, 1, 4);
        WinoLayout.Priorities(_name, UILayoutConstraintAxis.Horizontal, 1, 250);
        WinoLayout.Priorities(_secondary, UILayoutConstraintAxis.Horizontal, 1, 250);
        // A long source ("Account · Address book") gives way before the name and the address.
        _source.SetContentCompressionResistancePriority(240, UILayoutConstraintAxis.Horizontal);
        _source.WidthAnchor.ConstraintLessThanOrEqualTo(110).Active = true;
        _source.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        // The chips join the secondary line, so a row keeps two lines.
        _meta = WinoLayout.HStack(6, _secondary);
        var text = WinoLayout.VStack(2, _name, _meta);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        _star = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        _star.OnPrimaryAction(this, static cell => cell._toggleFavorite?.Invoke());
        WinoLayout.Size(_star, 44, 44);

        var row = WinoLayout.Insets(WinoLayout.HStack(12, _picture, text, WinoLayout.Spacer(), _source, _star), 6, 12, 6, 0);
        row.Distribution = UIStackViewDistribution.Fill;
        WinoLayout.Fill(row, ContentView);
        IsAccessibilityElement = false;
    }

    /// <summary>Binds the row to a contact; <paramref name="read"/> is re-run whenever the source raises PropertyChanged.</summary>
    public void Bind(INotifyPropertyChanged source, Func<WinoContactRowModel> read, Action toggleFavorite)
    {
        Unbind();
        _bound = source;
        _read = read;
        _toggleFavorite = toggleFavorite;
        source.PropertyChanged += ModelChanged;
        Apply();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!WinoContactRowRules.RefreshesRow(args.PropertyName)) return;
        if (NSThread.IsMain) Apply();
        else BeginInvokeOnMainThread(Apply);
    }

    private void Apply()
    {
        if (_read is null) return;
        var model = _read();
        _name.Text = model.Name ?? string.Empty;
        _secondary.Text = model.SecondaryValue ?? string.Empty;
        _secondary.Hidden = string.IsNullOrEmpty(_secondary.Text);
        _source.Text = model.SourceLabel ?? string.Empty;
        _source.Hidden = string.IsNullOrEmpty(_source.Text);
        _picture.SetIdentity(model.Name, model.Address);
        _picture.Image = model.Picture;
        ApplyCategories(model.Categories);
        ApplyStar(model.IsFavorite);
        _name.AccessibilityLabel = string.Join(", ", new[] { model.Name, model.SecondaryValue, model.SourceLabel }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>Keeps the chips whose category still matches and replaces only the rest (shared <see cref="WinoListDiff"/>).</summary>
    private void ApplyCategories(IReadOnlyList<MailCategory> categories)
    {
        int keep = WinoListDiff.ReusablePrefix(_chipKeys, categories, static (shown, next) => shown == (next.Name, next.BackgroundColorHex));
        if (keep == categories.Count && keep == _chipKeys.Count) return;

        if (categories.Count == 0)
        {
            _chips?.RemoveFromSuperview();
            _chips = null;
            _chipKeys.Clear();
            return;
        }

        if (_chips is null)
        {
            _chips = WinoCategoryChip.Row(null);
            _meta.AddArrangedSubview(_chips);
        }
        var views = _chips.ArrangedSubviews;
        for (int i = views.Length - 1; i >= keep; i--)
        {
            _chips.RemoveArrangedSubview(views[i]);
            views[i].RemoveFromSuperview();
        }
        _chipKeys.RemoveRange(keep, _chipKeys.Count - keep);
        for (int i = keep; i < categories.Count; i++)
        {
            _chips.AddArrangedSubview(new WinoCategoryChip(categories[i]));
            _chipKeys.Add((categories[i].Name, categories[i].BackgroundColorHex));
        }
    }

    private void ApplyStar(bool favorite)
    {
        if (_shownFavorite == favorite) return;
        _shownFavorite = favorite;
        if (_star.Configuration is { } configuration)
        {
            configuration.Image = WinoContactStyle.StarImage(favorite, 16);
            _star.Configuration = configuration;
        }
        WinoAccessibility.Label(_star, favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite);
    }

    public void Unbind()
    {
        if (_bound is { } bound) bound.PropertyChanged -= ModelChanged;
        _bound = null;
        _read = null;
        _toggleFavorite = null;
    }

    public override void SetSelected(bool selected, bool animated)
    {
        base.SetSelected(selected, animated);
        ApplySelection();
    }

    public override void SetHighlighted(bool highlighted, bool animated)
    {
        base.SetHighlighted(highlighted, animated);
        ApplySelection();
    }

    private void ApplySelection()
    {
        _fill.Hidden = !(Selected || Highlighted);
        _fill.Fill = Selected ? WinoContactStyle.SelectedFill : WinoContactStyle.HoverFill;
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Unbind();
        _picture.Image = null;
        ApplySelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Unbind();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Alphabet group header (Windows ContactGroup header) in the iOS canvas look: the letter as a plain label, 13pt
/// semibold in the secondary colour, with no strip behind it (like the mail list's date headers).
/// </summary>
public sealed class WinoContactGroupHeaderView : UITableViewHeaderFooterView
{
    public new const string ReuseIdentifier = "WinoContactGroupHeader";
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(13, UIFontWeight.Semibold), WinoStyle.SecondaryText);

    public WinoContactGroupHeaderView() : this(new NSString(ReuseIdentifier)) { }

    [Export("initWithReuseIdentifier:")]
    public WinoContactGroupHeaderView(NSString reuseIdentifier) : base(reuseIdentifier)
    {
        // The zone surface behind the letter: plain in place, and a pinned header still covers the rows under it.
        BackgroundConfiguration = UIBackgroundConfiguration.ClearConfiguration;
        ContentView.BackgroundColor = WinoStyle.ZoneFill;
        ContentView.AddSubview(_title);
        NSLayoutConstraint.ActivateConstraints(
        [
            _title.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(ContentView.TrailingAnchor, -16),
            _title.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -4)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Header;
    }

    public string Title
    {
        get => _title.Text ?? string.Empty;
        set
        {
            _title.Text = value ?? string.Empty;
            AccessibilityLabel = value;
        }
    }
}

/// <summary>
/// Category chip used in contact rows and the contact card (Windows ContactCategoryChipTemplate; UIKit twin of the AppKit
/// <c>WinoCategoryChip</c>): radius 4, padding 6×1, caption text, tinted with the category colour at low alpha.
/// </summary>
public sealed class WinoCategoryChip : WinoSurfaceView
{
    private readonly UILabel _label;

    public WinoCategoryChip(MailCategory category)
    {
        CornerRadius = 4;
        var color = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.Accent;
        Fill = color.ColorWithAlpha(0.16f);
        _label = WinoStyle.Label(category.Name, WinoStyle.Caption, color);
        _label.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        WinoLayout.Fill(_label, this, 1, 6, 1, 6);
        SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.StaticText;
        AccessibilityLabel = category.Name;
    }

    public override CGSize IntrinsicContentSize
    {
        get
        {
            var size = _label.IntrinsicContentSize;
            return new CGSize(size.Width + 12, size.Height + 2);
        }
    }

    /// <summary>A horizontal chip row (4pt gaps) for the given categories; empty when there are none.</summary>
    public static UIStackView Row(IEnumerable<MailCategory>? categories)
    {
        var row = WinoLayout.HStack(4);
        row.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        if (categories is null) return row;
        foreach (var category in categories) row.AddArrangedSubview(new WinoCategoryChip(category));
        return row;
    }
}

/// <summary>
/// Read-only field card in the contact card (Windows ContactsPage detail Border; UIKit twin of the AppKit
/// <c>WinoContactFieldCard</c>): radius 10, card stroke, padding 14×12, a caption title and value rows with an optional
/// Wino glyph, the value (selectable through the long-press copy menu of the page) and a caption beneath.
/// </summary>
public sealed class WinoContactFieldCard : WinoSurfaceView
{
    private readonly UIStackView _items;

    public WinoContactFieldCard(string title)
    {
        CornerRadius = 10;
        Fill = WinoContactStyle.CardFill;
        Stroke = WinoContactStyle.CardStroke;
        var header = WinoStyle.Label(title, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        header.AccessibilityTraits = UIAccessibilityTrait.Header;
        _items = WinoLayout.VStack(8);
        _items.Alignment = UIStackViewAlignment.Fill;
        var stack = WinoLayout.VStack(8, header, _items);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this, 12, 14, 12, 14);
    }

    public int Count => _items.ArrangedSubviews.Length;

    /// <summary>Adds one value row. Empty values are skipped so callers can add optional fields freely.</summary>
    public WinoContactFieldCard Add(string? value, string? label = null, WinoIconGlyph glyph = WinoIconGlyph.None, bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        var text = WinoStyle.Label(value, WinoStyle.Body, maximumLines: wrap ? 0 : 1);
        text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var column = WinoLayout.VStack(1, text);
        if (!string.IsNullOrWhiteSpace(label)) column.AddArrangedSubview(WinoStyle.Label(label, WinoStyle.Caption, WinoStyle.TertiaryText));
        UIView row = column;
        if (glyph != WinoIconGlyph.None)
        {
            var icon = new WinoIconView(glyph, 16, WinoStyle.TertiaryText);
            var line = WinoLayout.HStack(10, icon, column);
            line.Alignment = UIStackViewAlignment.Top;
            row = line;
        }
        row.IsAccessibilityElement = true;
        row.AccessibilityLabel = string.IsNullOrWhiteSpace(label) ? value : $"{label}, {value}";
        _items.AddArrangedSubview(row);
        return this;
    }
}

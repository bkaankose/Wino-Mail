using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Core.Domain.Entities.Mail;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Contacts;

/// <summary>Colours shared by the contact list and detail pane (design board WireContacts).</summary>
public static class WinoContactStyle
{
    /// <summary>Full-width group header strip tint (Windows LayerFillColorDefault).</summary>
    public static NSColor HeaderFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.045), WinoStyle.Hex(0xFFFFFF, 0.07));

    /// <summary>Selected row fill: subtle, like the Windows list item selection on a zone.</summary>
    public static NSColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));

    public static NSColor HoverFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>Card surface in the detail pane (Windows CardBackgroundFillColorDefault).</summary>
    public static NSColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));

    public static NSColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));

    /// <summary>Star toggle image: filled in the caution colour when favourite, otherwise a tertiary grey outline. Always mono.</summary>
    public static NSImage StarImage(bool favorite, double size)
    {
        var glyph = WinoIcons.Glyph(favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star);
        var color = favorite ? WinoStyle.Caution : WinoStyle.TertiaryText;
        var image = NSImage.ImageWithSize(new CGSize(size, size), false, rect => { WinoIcons.Draw(glyph, rect, color, null, colorful: false); return true; });
        image.Template = false;
        return image;
    }

    public const double RowHeight = 58;
    public const double HeaderRowHeight = 42;
    public const double RowRadius = 6;
}

/// <summary>Presentation snapshot of one contact row, read from the shared contact ViewModel by the page.</summary>
public sealed record WinoContactRowModel(string? Name, string? SecondaryValue, string? SourceLabel, string? Address,
    bool IsFavorite, IReadOnlyList<MailCategory> Categories, NSImage? Picture);

/// <summary>
/// One contact row (Windows ContactTemplate): 40 pt avatar, semibold name, secondary value
/// (email or phone), tertiary source label with category chips, and a star toggle that is always
/// present so hover never shifts the layout. Padding 8×6, column gap 12.
/// </summary>
public sealed class WinoContactRowView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoContactRow";

    private readonly WinoContactPicture _picture = new(40);
    private readonly NSTextField _name = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly NSTextField _secondary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly NSTextField _source = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
    private readonly NSStackView _meta;
    private readonly NSButton _star;
    private NSStackView? _chips;
    private readonly List<(string? Name, string? Color)> _chipKeys = new();
    private bool? _shownFavorite;
    private INotifyPropertyChanged? _bound;
    private Func<WinoContactRowModel>? _read;
    private Action? _toggleFavorite;

    public WinoContactRowView()
    {
        Identifier = ReuseIdentifier;
        _name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _secondary.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _source.SetContentCompressionResistancePriority(260, NSLayoutConstraintOrientation.Horizontal);
        _meta = WinoLayout.HStack(6, _source);
        var text = WinoLayout.VStack(1, _name, _secondary, _meta);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _star = new NSButton { Bordered = false, BezelStyle = NSBezelStyle.Inline, TranslatesAutoresizingMaskIntoConstraints = false };
        _star.SetButtonType(NSButtonType.MomentaryChange);
        _star.ImagePosition = NSCellImagePosition.ImageOnly;
        _star.Activated += (_, _) => _toggleFavorite?.Invoke();
        WinoLayout.Size(_star, 28, 28);

        var row = WinoLayout.HStack(12, _picture, text, _star);
        row.EdgeInsets = new NSEdgeInsets(6, 8, 6, 8);
        row.Distribution = NSStackViewDistribution.Fill;
        WinoLayout.Fill(row, this);
        NSLayoutConstraint.ActivateConstraints([text.WidthAnchor.ConstraintGreaterThanOrEqualTo(40)]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.RowRole;
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
        if (args.PropertyName is null or "" or "IsFavorite" or "Name" or "SecondaryValue" or "Categories" or "ContactPictureFileId" or "SourceContact")
            Apply();
    }

    private void Apply()
    {
        if (_read is null) return;
        var model = _read();
        _name.StringValue = model.Name ?? string.Empty;
        _secondary.StringValue = model.SecondaryValue ?? string.Empty;
        _secondary.Hidden = string.IsNullOrEmpty(_secondary.StringValue);
        _source.StringValue = model.SourceLabel ?? string.Empty;
        ToolTip = model.SecondaryValue;
        _picture.SetIdentity(model.Name, model.Address);
        _picture.Image = model.Picture;
        ApplyCategories(model.Categories);
        ApplyStar(model.IsFavorite);
        AccessibilityLabel = model.Name;
    }

    /// <summary>Keeps the chips whose category still matches and replaces only the rest.</summary>
    private void ApplyCategories(IReadOnlyList<MailCategory> categories)
    {
        int keep = 0;
        while (keep < categories.Count && keep < _chipKeys.Count
            && _chipKeys[keep] == (categories[keep].Name, categories[keep].BackgroundColorHex))
            keep++;
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
        _star.Image = WinoContactStyle.StarImage(favorite, 15);
        _star.ToolTip = favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite;
        WinoAccessibility.Label(_star, _star.ToolTip);
    }

    public void Unbind()
    {
        if (_bound is { } bound) bound.PropertyChanged -= ModelChanged;
        _bound = null;
        _read = null;
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Unbind();
        _picture.Image = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Unbind();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Alphabet group header (Windows ContactGroup header): a full-width rounded strip in the theme
/// header tint with the letter in the accent colour, semibold. Min height 36, padding 12×6.
/// </summary>
public sealed class WinoContactGroupHeaderView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoContactGroupHeader";
    private readonly WinoSurfaceView _strip = new() { CornerRadius = 6 };
    private readonly NSTextField _title = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);

    public WinoContactGroupHeaderView()
    {
        Identifier = ReuseIdentifier;
        _strip.Fill = WinoContactStyle.HeaderFill;
        WinoLayout.Fill(_strip, this, 6, 0, 2, 0);
        _strip.AddSubview(_title);
        NSLayoutConstraint.ActivateConstraints(
        [
            _title.LeadingAnchor.ConstraintEqualTo(_strip.LeadingAnchor, 12),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(_strip.TrailingAnchor, -12),
            _title.CenterYAnchor.ConstraintEqualTo(_strip.CenterYAnchor)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        WinoStyle.AccentChanged += AccentChanged;
    }

    private void AccentChanged(object? sender, EventArgs args) => _title.TextColor = WinoStyle.Accent;

    public string Title
    {
        get => _title.StringValue;
        set
        {
            _title.StringValue = value ?? string.Empty;
            _title.TextColor = WinoStyle.Accent;
            AccessibilityLabel = value;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

/// <summary>Row container: rounded 6 pt selection fill, no separators, no system highlight.</summary>
public sealed class WinoContactTableRowView : NSTableRowView
{
    public const string ReuseIdentifier = "WinoContactTableRow";
    private NSTrackingArea? _tracking;
    private bool _hovered;

    public WinoContactTableRowView() => Identifier = ReuseIdentifier;

    public bool IsHeader { get; set; }

    public override void DrawBackground(CGRect dirtyRect)
    {
        if (IsHeader || Selected || !_hovered) return;
        WinoContactStyle.HoverFill.SetFill();
        NSBezierPath.FromRoundedRect(Bounds.Inset(0, 1), (nfloat)WinoContactStyle.RowRadius, (nfloat)WinoContactStyle.RowRadius).Fill();
    }

    public override void DrawSelection(CGRect dirtyRect)
    {
        if (IsHeader || SelectionHighlightStyle == NSTableViewSelectionHighlightStyle.None) return;
        WinoContactStyle.SelectedFill.SetFill();
        NSBezierPath.FromRoundedRect(Bounds.Inset(0, 1), (nfloat)WinoContactStyle.RowRadius, (nfloat)WinoContactStyle.RowRadius).Fill();
    }

    public override void DrawSeparator(CGRect dirtyRect)
    {
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) { _hovered = true; NeedsDisplay = true; }
    public override void MouseExited(NSEvent theEvent) { _hovered = false; NeedsDisplay = true; }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        _hovered = false;
        IsHeader = false;
    }
}

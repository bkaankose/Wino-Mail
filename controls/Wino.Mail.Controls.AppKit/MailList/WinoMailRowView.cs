using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>
/// The Wino mail row, laid out like the Windows MailListItemTemplates: an 8pt gutter for the
/// selection pill, the avatar, then three lines that each span to the trailing edge.
/// Line 1: nickname, draft tag, sender (bold when unread), thread count, then the attachment and
/// flag glyphs right-aligned. Line 2: thread chevron, subject (semibold accent when unread), the
/// date right-aligned and the unread dot. Line 3: the preview. Detailed rows add a tile line for
/// categories and intelligence; compact rows collapse the tiles into line 1.
/// Hover actions overlay the trailing edge; select mode adds a checkbox before the avatar.
/// </summary>
public class WinoMailRowView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoMailRow";

    private readonly NSView _box;
    private readonly NSStackView _content;
    private readonly NSButton _checkbox;
    private readonly WinoContactPicture _avatar;
    private readonly NSStackView _text;
    private readonly NSStackView _line1;
    private readonly WinoChipView _nicknameLeft;
    private readonly WinoChipView _nicknameRight;
    private readonly NSTextField _draftTag;
    private readonly NSTextField _sender;
    private readonly NSTextField _threadCount;
    private readonly NSStackView _indicators;
    private readonly NSStackView _compactTiles;
    private readonly WinoIconView _pinGlyph;
    private readonly WinoIconView _attachmentGlyph;
    private readonly WinoIconView _flagGlyph;
    private readonly NSProgressIndicator _busy;
    private readonly NSStackView _line2;
    private readonly NSButton _chevron;
    private readonly NSTextField _subject;
    private readonly NSTextField _date;
    private readonly WinoSurfaceView _unreadDot;
    private readonly NSTextField _preview;
    private readonly NSStackView _tiles;
    private readonly WinoSurfaceView _hoverBar;
    private readonly NSButton _archiveButton;
    private readonly NSButton _deleteButton;
    private readonly NSButton _flagButton;
    private readonly NSButton _readButton;
    private readonly NSLayoutConstraint _boxLeading;
    private readonly NSLayoutConstraint _contentTop;
    private readonly NSLayoutConstraint _contentBottom;
    private readonly List<NSView> _tileViews = new();
    private List<WinoMailRowTile> _shownTiles = new();
    private bool _shownCompactTiles;
    private bool? _shownExpanded;
    private bool? _shownFlagged;
    private bool? _shownUnread;
    private WinoMailRowModel _model = new();
    private bool _hovered;
    private bool _selectionMode;
    private bool _hoverActionsEnabled = true;
    private IDisposable? _subscription;
    private (string Archive, string Delete, string Flag, string Unflag, string Read, string Unread) _labels =
        ("Archive", "Delete", "Flag", "Clear flag", "Mark as read", "Mark as unread");

    public WinoMailRowView()
    {
        Identifier = ReuseIdentifier;

        _box = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        AddSubview(_box);
        _boxLeading = _box.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)WinoMailRowMetrics.ContentLeading);
        NSLayoutConstraint.ActivateConstraints(
        [
            _boxLeading,
            _box.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -(nfloat)WinoMailRowMetrics.ContentTrailing),
            _box.TopAnchor.ConstraintEqualTo(TopAnchor),
            _box.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        _checkbox = WinoCheckbox.Create(null, () => CheckboxToggled?.Invoke(this, EventArgs.Empty));
        _checkbox.TranslatesAutoresizingMaskIntoConstraints = false;
        _checkbox.ControlSize = NSControlSize.Small;
        _checkbox.Hidden = true;

        _avatar = new WinoContactPicture(30);

        // ---- Line 1 ----
        _nicknameLeft = NicknameChip();
        _nicknameRight = NicknameChip();
        _draftTag = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.Flagged);
        _draftTag.Hidden = true;
        _sender = WinoStyle.Label(string.Empty, WinoStyle.Body);
        _sender.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _sender.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _threadCount = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _threadCount.Hidden = true;
        _compactTiles = WinoLayout.HStack(4);
        _compactTiles.Hidden = true;
        _pinGlyph = new WinoIconView(WinoIconGlyph.Pin, 12, WinoStyle.SecondaryText) { Hidden = true, ToolTip = "Pinned" };
        _attachmentGlyph = new WinoIconView(WinoIconGlyph.Attachment, 14, WinoStyle.Hex(0xFDCB6E)) { Hidden = true };
        _flagGlyph = new WinoIconView(WinoIconGlyph.Flag, 14, WinoStyle.Hex(0xE74C3C)) { Hidden = true };
        _busy = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Mini, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Size(_busy, 12, 12);
        _indicators = WinoLayout.HStack(4, _compactTiles, _pinGlyph, _attachmentGlyph, _flagGlyph, _busy);
        _indicators.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _indicators.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _indicators.EdgeInsets = new NSEdgeInsets(0, 4, 0, 0);
        _line1 = WinoLayout.HStack(4, _nicknameLeft, _draftTag, _sender, _threadCount, _nicknameRight, _indicators);
        _line1.Distribution = NSStackViewDistribution.Fill;

        // ---- Line 2 ----
        _chevron = new NSButton
        {
            Bordered = false,
            Title = string.Empty,
            ImagePosition = NSCellImagePosition.ImageOnly,
            Image = WinoIcons.Image(WinoIconGlyph.ChevronRight, 10),
            ContentTintColor = WinoStyle.SecondaryText,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true
        };
        _chevron.SetButtonType(NSButtonType.MomentaryChange);
        WinoLayout.Size(_chevron, 12, 16);
        _chevron.Activated += (_, _) => ThreadToggleRequested?.Invoke(this, EventArgs.Empty);
        _subject = WinoStyle.Label(string.Empty, WinoStyle.Body);
        _subject.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _subject.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _date = WinoStyle.Label(string.Empty, WinoStyle.Caption, NSColor.Label.ColorWithAlphaComponent((nfloat)0.7));
        _date.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _date.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _unreadDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_unreadDot, 6, 6);
        _line2 = WinoLayout.HStack(8, _chevron, _subject, _date, _unreadDot);
        _line2.Distribution = NSStackViewDistribution.Fill;
        _line2.SetCustomSpacing(4, _chevron);

        // ---- Line 3 and tiles ----
        _preview = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), NSColor.Label.ColorWithAlphaComponent((nfloat)0.7));
        _preview.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _preview.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _tiles = WinoLayout.HStack(4);
        _tiles.Hidden = true;
        _tiles.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _tiles.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        _text = WinoLayout.VStack(WinoMailRowMetrics.LineSpacing, _line1, _line2, _preview, _tiles);
        _text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        foreach (var line in new NSView[] { _line1, _line2, _preview })
            line.TrailingAnchor.ConstraintEqualTo(_text.TrailingAnchor).Active = true;
        // Tiles hug their content from the leading edge; pinning the row to full width stretched the first tile.
        _tiles.TrailingAnchor.ConstraintLessThanOrEqualTo(_text.TrailingAnchor).Active = true;

        _content = WinoLayout.HStack(8, _checkbox, _avatar, _text);
        _content.Distribution = NSStackViewDistribution.Fill;
        _content.SetCustomSpacing(6, _checkbox);
        _box.AddSubview(_content);
        _contentTop = _content.TopAnchor.ConstraintGreaterThanOrEqualTo(_box.TopAnchor, 6);
        _contentBottom = _content.BottomAnchor.ConstraintLessThanOrEqualTo(_box.BottomAnchor, -6);
        NSLayoutConstraint.ActivateConstraints(
        [
            _content.LeadingAnchor.ConstraintEqualTo(_box.LeadingAnchor, 8),
            _content.TrailingAnchor.ConstraintEqualTo(_box.TrailingAnchor, -8),
            _content.CenterYAnchor.ConstraintEqualTo(_box.CenterYAnchor),
            _contentTop,
            _contentBottom
        ]);

        // ---- Hover actions (Windows: trailing edge, over the row content) ----
        _archiveButton = HoverButton(WinoIconGlyph.Archive, HoverActionKind.Archive);
        _deleteButton = HoverButton(WinoIconGlyph.Delete, HoverActionKind.Delete);
        _flagButton = HoverButton(WinoIconGlyph.Flag, HoverActionKind.ToggleFlag);
        _readButton = HoverButton(WinoIconGlyph.MarkRead, HoverActionKind.ToggleRead);
        var hoverStack = WinoLayout.HStack(2, _archiveButton, _deleteButton, _flagButton, _readButton);
        hoverStack.EdgeInsets = new NSEdgeInsets(2, 2, 2, 2);
        _hoverBar = new WinoSurfaceView
        {
            CornerRadius = 7,
            Fill = WinoStyle.Dynamic(NSColor.White.ColorWithAlphaComponent((nfloat)0.96), WinoStyle.Hex(0x3C3C40, 0.98)),
            Stroke = WinoStyle.ZoneStroke,
            Hidden = true
        };
        WinoLayout.Fill(hoverStack, _hoverBar);
        AddSubview(_hoverBar);
        NSLayoutConstraint.ActivateConstraints(
        [
            _hoverBar.TrailingAnchor.ConstraintEqualTo(_box.TrailingAnchor, -8),
            _hoverBar.CenterYAnchor.ConstraintEqualTo(_box.CenterYAnchor)
        ]);

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.CellRole;
        ApplyHoverLabels();
        ApplyColors();
    }

    private static WinoChipView NicknameChip()
    {
        var chip = new WinoChipView(16) { Hidden = true, CornerRadius = 4 };
        chip.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        return chip;
    }

    private NSButton HoverButton(WinoIconGlyph glyph, HoverActionKind kind)
    {
        var button = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            Image = WinoIcons.Image(glyph, 14),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.PrimaryText,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(button, 28, 26);
        button.Activated += (_, _) => HoverActionInvoked?.Invoke(this, kind);
        return button;
    }

    /// <summary>Raised for the Archive, Delete, ToggleFlag and ToggleRead hover buttons.</summary>
    public event EventHandler<HoverActionKind>? HoverActionInvoked;

    public event EventHandler? ThreadToggleRequested;

    public event EventHandler? CheckboxToggled;

    /// <summary>Owner-managed subscription for the item this reused cell shows; replacing it disposes the previous one.</summary>
    public IDisposable? Subscription
    {
        get => _subscription;
        set
        {
            if (ReferenceEquals(_subscription, value)) return;
            _subscription?.Dispose();
            _subscription = value;
        }
    }

    /// <summary>Owner data, typically the projected list row this cell shows.</summary>
    public object? Item { get; set; }

    public WinoMailRowModel Model => _model;

    /// <summary>Localised labels for the hover buttons.</summary>
    public void SetLabels(string archive, string delete, string flag, string unflag, string markRead, string markUnread)
    {
        _labels = (archive, delete, flag, unflag, markRead, markUnread);
        ApplyHoverLabels();
    }

    private void ApplyHoverLabels()
    {
        _archiveButton.ToolTip = _labels.Archive;
        WinoAccessibility.Label(_archiveButton, _labels.Archive);
        _deleteButton.ToolTip = _labels.Delete;
        WinoAccessibility.Label(_deleteButton, _labels.Delete);
        var flag = _model.IsFlagged ? _labels.Unflag : _labels.Flag;
        _flagButton.ToolTip = flag;
        WinoAccessibility.Label(_flagButton, flag);
        var read = _model.IsUnread ? _labels.Read : _labels.Unread;
        _readButton.ToolTip = read;
        WinoAccessibility.Label(_readButton, read);
    }

    public bool HoverActionsEnabled
    {
        get => _hoverActionsEnabled;
        set { _hoverActionsEnabled = value; UpdateHover(); }
    }

    public bool IsHovered
    {
        get => _hovered;
        set { if (_hovered == value) return; _hovered = value; UpdateHover(); }
    }

    public bool IsSelectionMode
    {
        get => _selectionMode;
        set { _selectionMode = value; _checkbox.Hidden = !value; UpdateHover(); }
    }

    public bool IsChecked
    {
        get => _checkbox.State == NSCellStateValue.On;
        set => _checkbox.State = value ? NSCellStateValue.On : NSCellStateValue.Off;
    }

    private void UpdateHover() => _hoverBar.Hidden = !(_hovered && _hoverActionsEnabled && !_selectionMode && !_model.IsBusy);

    public void Apply(WinoMailRowModel model)
    {
        _model = model;
        var density = model.Density;
        bool compact = density == WinoMailRowDensity.Compact;

        _boxLeading.Constant = (nfloat)(WinoMailRowMetrics.ContentLeading + (model.Kind == WinoMailRowKind.ThreadChild ? WinoMailRowMetrics.ChildIndent(density) : 0));
        _contentTop.Constant = (nfloat)WinoMailRowMetrics.Padding(density);
        _contentBottom.Constant = -(nfloat)WinoMailRowMetrics.Padding(density);

        _avatar.Hidden = !model.ShowPicture;
        _avatar.Size = WinoMailRowMetrics.AvatarSize(density);
        _avatar.Image = model.Picture;
        _avatar.SetIdentity(string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender,
            string.IsNullOrWhiteSpace(model.SenderAddress) ? model.Sender : model.SenderAddress);

        _draftTag.StringValue = $"[{model.DraftLabel}]";
        _draftTag.Hidden = !model.IsDraft;
        _sender.StringValue = model.IsDraft && string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender;
        _threadCount.StringValue = model.ThreadCount.ToString();
        _threadCount.Hidden = model.Kind != WinoMailRowKind.ThreadHead || model.ThreadCount < 2;
        ApplyNickname(_nicknameLeft, model, AccountNicknamePosition.Left);
        ApplyNickname(_nicknameRight, model, AccountNicknamePosition.Right);
        _pinGlyph.Hidden = !model.IsPinned;
        _attachmentGlyph.Hidden = !model.HasAttachments;
        _flagGlyph.Hidden = !model.IsFlagged;
        _busy.Hidden = !model.IsBusy;
        if (model.IsBusy) _busy.StartAnimation(null);
        else _busy.StopAnimation(null);

        _chevron.Hidden = model.Kind != WinoMailRowKind.ThreadHead || model.ThreadCount < 2;
        if (_shownExpanded != model.IsThreadExpanded)
        {
            _shownExpanded = model.IsThreadExpanded;
            _chevron.Image = WinoIcons.Image(model.IsThreadExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight, 10);
        }
        WinoAccessibility.Label(_chevron, model.ThreadCount.ToString());
        _subject.StringValue = model.Subject;
        _date.StringValue = model.DateText;
        _unreadDot.Hidden = !model.IsUnread;

        _preview.StringValue = model.Preview;
        _preview.Hidden = compact || !model.ShowPreview || string.IsNullOrWhiteSpace(model.Preview);

        ApplyTiles(model);

        if (_shownFlagged != model.IsFlagged)
        {
            _shownFlagged = model.IsFlagged;
            _flagButton.Image = WinoIcons.Image(model.IsFlagged ? WinoIconGlyph.ClearFlag : WinoIconGlyph.Flag, 14);
        }
        if (_shownUnread != model.IsUnread)
        {
            _shownUnread = model.IsUnread;
            _readButton.Image = WinoIcons.Image(model.IsUnread ? WinoIconGlyph.MarkRead : WinoIconGlyph.MarkUnread, 14);
        }
        ApplyHoverLabels();
        UpdateHover();

        AccessibilityLabel = model.AccessibilityText;
        ApplyColors();
    }

    private static void ApplyNickname(WinoChipView chip, WinoMailRowModel model, AccountNicknamePosition position)
    {
        bool show = model.ShowNickname && model.NicknamePosition == position;
        chip.Hidden = !show;
        if (!show) return;
        var tint = model.AccountColor ?? WinoStyle.Accent;
        chip.Text = model.AccountNickname ?? string.Empty;
        chip.Fill = tint.ColorWithAlphaComponent((nfloat)0.16);
        chip.TextColor = tint;
    }

    private void ApplyTiles(WinoMailRowModel model)
    {
        bool compact = model.Density == WinoMailRowDensity.Compact;
        var tiles = model.Tiles.Take(6).ToList();
        if (compact != _shownCompactTiles || !tiles.SequenceEqual(_shownTiles))
            SyncTiles(tiles, compact);
        _compactTiles.Hidden = !compact || model.Tiles.Count == 0;
        _tiles.Hidden = compact || model.Tiles.Count == 0;
        _text.SetCustomSpacing((nfloat)WinoMailRowMetrics.LineSpacing, _line2);
        _text.SetCustomSpacing((nfloat)WinoMailRowMetrics.LineSpacing, _preview);
        if (!_tiles.Hidden)
            _text.SetCustomSpacing((nfloat)(WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.TileLineTopMargin(model.Density)), _preview.Hidden ? _line2 : _preview);
    }

    /// <summary>
    /// Updates the retained tile views in place and adds or removes only the difference. Switching
    /// between compact and detailed moves tiles to another line with another shape, so it starts over;
    /// a compact tile that changes between category dot and intelligence chip is rebuilt from there on.
    /// </summary>
    private void SyncTiles(List<WinoMailRowTile> tiles, bool compact)
    {
        int keep = 0;
        if (compact == _shownCompactTiles)
        {
            while (keep < tiles.Count && keep < _tileViews.Count
                && (!compact || tiles[keep].IsIntelligence == _shownTiles[keep].IsIntelligence))
                keep++;
        }

        for (int i = _tileViews.Count - 1; i >= keep; i--)
        {
            var view = _tileViews[i];
            if (view.Superview is NSStackView stack) stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
            _tileViews.RemoveAt(i);
        }

        for (int i = 0; i < keep; i++)
        {
            if (tiles[i] == _shownTiles[i]) continue;
            if (compact) CompactTile(tiles[i], _tileViews[i]);
            else DetailedTile(tiles[i], _tileViews[i]);
        }

        var target = compact ? _compactTiles : _tiles;
        for (int i = keep; i < tiles.Count; i++)
        {
            var view = compact ? CompactTile(tiles[i], null) : DetailedTile(tiles[i], null);
            _tileViews.Add(view);
            target.AddArrangedSubview(view);
        }

        _shownTiles = tiles;
        _shownCompactTiles = compact;
    }

    /// <summary>Configures <paramref name="existing"/> (same tile kind) or creates a new compact tile.</summary>
    private static NSView CompactTile(WinoMailRowTile tile, NSView? existing)
    {
        if (!tile.IsIntelligence)
        {
            if (existing is not WinoSurfaceView dot)
            {
                dot = new WinoSurfaceView { CornerRadius = 4 };
                WinoLayout.Size(dot, 8, 8);
            }
            dot.Fill = tile.Background ?? WinoStyle.Accent;
            dot.ToolTip = tile.Text;
            return dot;
        }
        if (existing is not WinoChipView chip)
        {
            chip = new WinoChipView(18) { Fill = WinoStyle.SubtleFill, TextColor = WinoStyle.SecondaryText };
            WinoLayout.Size(chip, 18, 18);
        }
        chip.ToolTip = tile.Tooltip ?? tile.Text;
        chip.SetGlyph(tile.Glyph, 10);
        chip.DotColor = string.IsNullOrEmpty(tile.Glyph) ? WinoStyle.SecondaryText : null;
        chip.AccessibilityLabel = tile.Tooltip ?? tile.Text;
        return chip;
    }

    /// <summary>Configures <paramref name="existing"/> or creates a new detailed tile.</summary>
    private static NSView DetailedTile(WinoMailRowTile tile, NSView? existing)
    {
        if (existing is not WinoChipView chip)
        {
            chip = new WinoChipView(WinoMailRowMetrics.TileHeight) { CornerRadius = 4, MaxTextWidth = 180 };
            chip.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        }
        chip.Text = tile.Text;
        chip.ToolTip = tile.Tooltip ?? tile.Text;
        if (tile.IsIntelligence)
        {
            chip.Fill = WinoStyle.SubtleFill;
            chip.TextColor = WinoStyle.PrimaryText;
            chip.SetGlyph(tile.Glyph, 11);
            chip.DotColor = string.IsNullOrEmpty(tile.Glyph) ? WinoStyle.SecondaryText : null;
            chip.GlyphTint = tile.IsWarning ? WinoStyle.Caution : WinoStyle.SecondaryText;
        }
        else
        {
            var background = tile.Background ?? WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.16);
            chip.Fill = background;
            chip.TextColor = tile.Foreground ?? ReadableText(background);
            chip.SetGlyph(null);
            chip.DotColor = null;
        }
        return chip;
    }

    /// <summary>Black or white, whichever reads on the given fill (Windows GetReadableTextColor).</summary>
    public static NSColor ReadableText(NSColor background)
    {
        var rgb = background.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? background;
        double luminance = 0.299 * rgb.RedComponent + 0.587 * rgb.GreenComponent + 0.114 * rgb.BlueComponent;
        return luminance > 0.6 ? NSColor.Black : NSColor.White;
    }

    private void ApplyColors()
    {
        bool unread = _model.IsUnread;
        _sender.Font = NSFont.SystemFontOfSize(13, unread ? NSFontWeight.Bold : NSFontWeight.Regular);
        _sender.TextColor = WinoStyle.PrimaryText;
        _subject.Font = NSFont.SystemFontOfSize(13, unread ? NSFontWeight.Semibold : NSFontWeight.Regular);
        _subject.TextColor = unread ? WinoStyle.Accent : WinoStyle.PrimaryText;
        _unreadDot.Fill = WinoStyle.Accent;
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Subscription = null;
        Item = null;
        _hovered = false;
        UpdateHover();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Subscription = null;
            HoverActionInvoked = null;
            ThreadToggleRequested = null;
            CheckboxToggled = null;
        }
        base.Dispose(disposing);
    }
}

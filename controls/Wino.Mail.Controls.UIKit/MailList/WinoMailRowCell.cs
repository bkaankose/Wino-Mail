using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>
/// The Wino mail row as a table cell (UIKit twin of the AppKit <c>WinoMailRowView</c> and <c>WinoMailTableRowView</c>),
/// laid out like the Windows MailListItemTemplates: a highlight box inset by <see cref="WinoMailRowMetrics.RowGap"/> that
/// holds the selection pill, the avatar, then three lines. Line 1: nickname, draft tag, sender (bold when unread),
/// thread count, then the pin, attachment and flag glyphs. Line 2: thread chevron, subject (semibold accent when
/// unread), the date and the unread dot. Line 3: the preview. Detailed rows add a wrapping tile row; compact rows
/// collapse the tiles into line 1. Select mode shows a round check before the avatar. Swipe actions belong to the
/// page (UITableViewDelegate); the iPad pointer can show the hover actions when <see cref="HoverActionsEnabled"/> is set.
/// </summary>
public class WinoMailRowCell : UITableViewCell
{
    public new const string ReuseIdentifier = "WinoMailRow";

    private readonly UIView _box;
    private readonly WinoSurfaceView _highlight;
    private readonly WinoSurfaceView _pill;
    private readonly UIStackView _content;
    private readonly WinoIconView _checkbox;
    private readonly WinoContactPicture _avatar;
    private readonly UIStackView _text;
    private readonly UIStackView _line1;
    private readonly WinoChipView _nicknameLeft;
    private readonly WinoChipView _nicknameRight;
    private readonly UILabel _draftTag;
    private readonly UILabel _sender;
    private readonly UILabel _threadCount;
    private readonly UIStackView _indicators;
    private readonly UIStackView _compactTiles;
    private readonly WinoIconView _pinGlyph;
    private readonly WinoIconView _attachmentGlyph;
    private readonly WinoIconView _flagGlyph;
    private readonly UIActivityIndicatorView _busy;
    private readonly UIStackView _line2;
    private readonly UIButton _chevron;
    private readonly UILabel _subject;
    private readonly UILabel _date;
    private readonly WinoSurfaceView _unreadDot;
    private readonly UILabel _preview;
    private readonly WinoFlowView _tiles;
    private readonly WinoSurfaceView _hoverBar;
    private readonly UIButton[] _hoverButtons;
    private readonly HoverActionKind[] _hoverKinds = new HoverActionKind[3];
    private readonly WinoIconGlyph[] _shownHoverGlyphs = new WinoIconGlyph[3];
    private readonly NSLayoutConstraint _boxLeading;
    private readonly NSLayoutConstraint _contentTop;
    private readonly NSLayoutConstraint _contentBottom;
    private readonly NSLayoutConstraint _pillHeight;
    private readonly List<UIView> _tileViews = new();
    private List<WinoMailRowTile> _shownTiles = new();
    private bool _shownCompactTiles;
    private bool? _shownExpanded;
    private WinoMailRowModel _model = new();
    private bool _hovered;
    private bool _selectionMode;
    private bool _systemSelectionControl;
    private bool _checked;
    private bool _hoverActionsEnabled;
    private IDisposable? _subscription;
    private HoverActionSet _hoverActions = new(HoverActionKind.Archive, HoverActionKind.Delete, HoverActionKind.ToggleFlag);
    private (string Archive, string Delete, string Flag, string Unflag, string Read, string Unread, string Junk) _labels =
        ("Archive", "Delete", "Flag", "Clear flag", "Mark as read", "Mark as unread", "Move to Junk");

    public WinoMailRowCell() : this(ReuseIdentifier) { }

    public WinoMailRowCell(string reuseIdentifier) : this(UITableViewCellStyle.Default, new NSString(reuseIdentifier)) { }

    /// <summary>The designated initializer, so the cell also works with <c>UITableView.RegisterClassForCellReuse</c>.</summary>
    [Export("initWithStyle:reuseIdentifier:")]
    public WinoMailRowCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier)
    {
        SelectionStyle = UITableViewCellSelectionStyle.None;
        BackgroundColor = UIColor.Clear;
        ContentView.BackgroundColor = UIColor.Clear;

        _box = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        ContentView.AddSubview(_box);
        _boxLeading = _box.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, (nfloat)WinoMailRowMetrics.ContentLeading);
        NSLayoutConstraint.ActivateConstraints(
        [
            _boxLeading,
            _box.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -(nfloat)WinoMailRowMetrics.ContentTrailing),
            _box.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, (nfloat)WinoMailRowMetrics.RowGap),
            _box.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -(nfloat)WinoMailRowMetrics.RowGap)
        ]);

        // Windows ListViewItem selection: a subtle rounded fill with a 3pt accent pill inside its leading edge.
        _highlight = new WinoSurfaceView { CornerRadius = 6, Hidden = true };
        WinoLayout.Fill(_highlight, _box);
        _pill = new WinoSurfaceView { CornerRadius = WinoMailRowMetrics.PillWidth / 2, Fill = WinoStyle.Accent };
        _highlight.AddSubview(_pill);
        _pillHeight = _pill.HeightAnchor.ConstraintEqualTo(16);
        NSLayoutConstraint.ActivateConstraints(
        [
            _pill.LeadingAnchor.ConstraintEqualTo(_highlight.LeadingAnchor, (nfloat)WinoMailRowMetrics.PillInset),
            _pill.WidthAnchor.ConstraintEqualTo((nfloat)WinoMailRowMetrics.PillWidth),
            _pill.CenterYAnchor.ConstraintEqualTo(_highlight.CenterYAnchor),
            _pillHeight
        ]);

        _checkbox = new WinoIconView(WinoIconGlyph.Circle, 18, WinoStyle.SecondaryText) { Colorful = false, Hidden = true };
        WinoLayout.Size(_checkbox, WinoMailRowMetrics.CheckboxWidth + 2, WinoMailRowMetrics.CheckboxWidth + 2);
        _checkbox.UserInteractionEnabled = true;
        _checkbox.AddGestureRecognizer(new UITapGestureRecognizer(() => CheckboxToggled?.Invoke(this, EventArgs.Empty)));

        _avatar = new WinoContactPicture(30);

        // ---- Line 1 ----
        _nicknameLeft = NicknameChip();
        _nicknameRight = NicknameChip();
        _draftTag = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.PrimaryFontSize), WinoStyle.Flagged);
        _draftTag.Hidden = true;
        _sender = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.PrimaryFontSize));
        WinoLayout.Priorities(_sender, UILayoutConstraintAxis.Horizontal, 1, 200);
        _threadCount = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(11), WinoStyle.SecondaryText);
        _threadCount.Hidden = true;
        _compactTiles = WinoLayout.HStack(4);
        _compactTiles.Hidden = true;
        _pinGlyph = new WinoIconView(WinoIconGlyph.Pin, 12, WinoStyle.SecondaryText) { Hidden = true };
        _attachmentGlyph = new WinoIconView(WinoIconGlyph.Attachment, 14, WinoStyle.Hex(0xFDCB6E)) { Hidden = true };
        _flagGlyph = new WinoIconView(WinoIconGlyph.Flag, 14, WinoStyle.Hex(0xE74C3C)) { Hidden = true };
        _busy = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _busy.Transform = CGAffineTransform.MakeScale(0.6f, 0.6f);
        WinoLayout.Size(_busy, 12, 12);
        _indicators = WinoLayout.Insets(WinoLayout.HStack(4, _compactTiles, _pinGlyph, _attachmentGlyph, _flagGlyph, _busy), 0, 4, 0, 0);
        WinoLayout.Priorities(_indicators, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        _line1 = WinoLayout.HStack(4, _nicknameLeft, _draftTag, _sender, _threadCount, _nicknameRight, _indicators);
        _line1.Distribution = UIStackViewDistribution.Fill;

        // ---- Line 2 ----
        var chevronConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        chevronConfiguration.Image = WinoIcons.Image(WinoIconGlyph.ChevronRight, 10);
        chevronConfiguration.BaseForegroundColor = WinoStyle.SecondaryText;
        chevronConfiguration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        _chevron = new UIButton { Configuration = chevronConfiguration, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(_chevron, 16, 16);
        _chevron.PrimaryActionTriggered += (_, _) => ThreadToggleRequested?.Invoke(this, EventArgs.Empty);
        _subject = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.PrimaryFontSize));
        WinoLayout.Priorities(_subject, UILayoutConstraintAxis.Horizontal, 1, 200);
        _date = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(11), UIColor.Label.ColorWithAlpha(0.7f));
        WinoLayout.Priorities(_date, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        _unreadDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_unreadDot, 6, 6);
        _line2 = WinoLayout.HStack(8, _chevron, _subject, _date, _unreadDot);
        _line2.Distribution = UIStackViewDistribution.Fill;
        _line2.SetCustomSpacing(4, _chevron);

        // ---- Line 3 and tiles ----
        _preview = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.PreviewFontSize), UIColor.Label.ColorWithAlpha(0.7f));
        WinoLayout.Priorities(_preview, UILayoutConstraintAxis.Horizontal, 1, 200);
        // Windows: a WinoWrapPanel with 4pt vertical spacing, so many tiles wrap instead of clipping.
        _tiles = new WinoFlowView { Spacing = WinoMailRowMetrics.TileSpacing, LineSpacing = WinoMailRowMetrics.TileLineSpacing, Hidden = true };
        WinoLayout.Priorities(_tiles, UILayoutConstraintAxis.Horizontal, 1, 200);
        _tiles.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Vertical);

        _text = WinoLayout.VStack(WinoMailRowMetrics.LineSpacing, _line1, _line2, _preview, _tiles);
        _text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(_text, UILayoutConstraintAxis.Horizontal, 1, 200);

        _content = WinoLayout.HStack(WinoMailRowMetrics.AvatarSpacing, _checkbox, _avatar, _text);
        _content.Distribution = UIStackViewDistribution.Fill;
        _content.SetCustomSpacing((nfloat)WinoMailRowMetrics.CheckboxSpacing, _checkbox);
        _box.AddSubview(_content);
        _contentTop = _content.TopAnchor.ConstraintGreaterThanOrEqualTo(_box.TopAnchor, (nfloat)WinoMailRowMetrics.Padding(WinoMailRowDensity.Medium));
        _contentBottom = _content.BottomAnchor.ConstraintLessThanOrEqualTo(_box.BottomAnchor, -(nfloat)WinoMailRowMetrics.Padding(WinoMailRowDensity.Medium));
        // The table sizes the row from WinoMailRowMetrics; if a measurement is a point off, let the padding give.
        _contentTop.Priority = 999;
        _contentBottom.Priority = 999;
        NSLayoutConstraint.ActivateConstraints(
        [
            _content.LeadingAnchor.ConstraintEqualTo(_box.LeadingAnchor, (nfloat)WinoMailRowMetrics.InnerLeading),
            _content.TrailingAnchor.ConstraintEqualTo(_box.TrailingAnchor, -(nfloat)WinoMailRowMetrics.InnerTrailing),
            _content.CenterYAnchor.ConstraintEqualTo(_box.CenterYAnchor),
            _contentTop,
            _contentBottom
        ]);

        // ---- Hover actions (iPad pointer; Windows: trailing edge, over the row content) ----
        _hoverButtons = [HoverButton(0), HoverButton(1), HoverButton(2)];
        var hoverStack = WinoLayout.Insets(WinoLayout.HStack(2, _hoverButtons), 2, 2, 2, 2);
        _hoverBar = new WinoSurfaceView
        {
            CornerRadius = 7,
            Fill = WinoStyle.Dynamic(UIColor.White.ColorWithAlpha(0.96f), WinoStyle.Hex(0x3C3C40, 0.98)),
            Stroke = WinoStyle.ZoneStroke,
            Hidden = true
        };
        WinoLayout.Fill(hoverStack, _hoverBar);
        ContentView.AddSubview(_hoverBar);
        NSLayoutConstraint.ActivateConstraints(
        [
            _hoverBar.TrailingAnchor.ConstraintEqualTo(_box.TrailingAnchor, -(nfloat)WinoMailRowMetrics.InnerTrailing),
            _hoverBar.CenterYAnchor.ConstraintEqualTo(_box.CenterYAnchor)
        ]);
        AddGestureRecognizer(new UIHoverGestureRecognizer(PointerChanged));

        IsAccessibilityElement = true;
        WinoStyle.AccentChanged += AccentChanged;
        ApplyHoverActions();
        ApplyColors();
        ApplySelection();
    }

    private static WinoChipView NicknameChip()
    {
        var chip = new WinoChipView(16) { Hidden = true, CornerRadius = 4 };
        chip.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        return chip;
    }

    private UIButton HoverButton(int slot)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(button, 28, 26);
        button.PrimaryActionTriggered += (_, _) =>
        {
            if (_hoverKinds[slot] != HoverActionKind.None) HoverActionInvoked?.Invoke(this, _hoverKinds[slot]);
        };
        return button;
    }

    private static void SetButtonImage(UIButton button, UIImage image)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Image = image;
        button.Configuration = configuration;
    }

    /// <summary>Raised with the action of the hover button that was tapped (iPad pointer).</summary>
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
    public void SetLabels(string archive, string delete, string flag, string unflag, string markRead, string markUnread, string moveToJunk)
    {
        _labels = (archive, delete, flag, unflag, markRead, markUnread, moveToJunk);
        ApplyHoverActions();
    }

    /// <summary>The left, center and right hover actions for this row's account; None leaves its slot out.</summary>
    public HoverActionSet HoverActions
    {
        get => _hoverActions;
        set { if (_hoverActions == value) return; _hoverActions = value; ApplyHoverActions(); UpdateHover(); }
    }

    private void ApplyHoverActions()
    {
        _hoverKinds[0] = _hoverActions.Left;
        _hoverKinds[1] = _hoverActions.Center;
        _hoverKinds[2] = _hoverActions.Right;
        for (int slot = 0; slot < _hoverButtons.Length; slot++)
        {
            var button = _hoverButtons[slot];
            var kind = _hoverKinds[slot];
            button.Hidden = kind == HoverActionKind.None;
            if (kind == HoverActionKind.None) continue;

            var glyph = HoverGlyph(kind);
            if (_shownHoverGlyphs[slot] != glyph)
            {
                _shownHoverGlyphs[slot] = glyph;
                SetButtonImage(button, WinoIcons.Image(glyph, 14));
            }
            WinoAccessibility.Label(button, HoverLabel(kind));
        }
    }

    private WinoIconGlyph HoverGlyph(HoverActionKind kind) => kind switch
    {
        HoverActionKind.Archive => WinoIconGlyph.Archive,
        HoverActionKind.Delete => WinoIconGlyph.Delete,
        HoverActionKind.ToggleFlag => _model.IsFlagged ? WinoIconGlyph.ClearFlag : WinoIconGlyph.Flag,
        HoverActionKind.ToggleRead => _model.IsUnread ? WinoIconGlyph.MarkRead : WinoIconGlyph.MarkUnread,
        HoverActionKind.MoveToJunk => WinoIconGlyph.Blocked,
        _ => WinoIconGlyph.None,
    };

    private string HoverLabel(HoverActionKind kind) => kind switch
    {
        HoverActionKind.Archive => _labels.Archive,
        HoverActionKind.Delete => _labels.Delete,
        HoverActionKind.ToggleFlag => _model.IsFlagged ? _labels.Unflag : _labels.Flag,
        HoverActionKind.ToggleRead => _model.IsUnread ? _labels.Read : _labels.Unread,
        HoverActionKind.MoveToJunk => _labels.Junk,
        _ => string.Empty,
    };

    private bool HasHoverActions => _hoverActions != HoverActionSet.None;

    /// <summary>Shows the hover actions under the iPad pointer. Off by default: the phone uses swipe actions only.</summary>
    public bool HoverActionsEnabled
    {
        get => _hoverActionsEnabled;
        set { _hoverActionsEnabled = value; UpdateHover(); }
    }

    public bool IsHovered
    {
        get => _hovered;
        set { if (_hovered == value) return; _hovered = value; UpdateHover(); ApplySelection(); }
    }

    /// <summary>Select mode shows the round check and draws a tint instead of the pill.</summary>
    public bool IsSelectionMode
    {
        get => _selectionMode;
        set { _selectionMode = value; _checkbox.Hidden = !value || _systemSelectionControl; UpdateHover(); ApplySelection(); }
    }

    /// <summary>
    /// The table draws the selection circles (UITableView editing with multiple selection, which also gives range drags
    /// over the circles and two-finger pans): the row's own check stays hidden in select mode. The table only draws a
    /// selected circle for a cell whose selection style is not None, so the cell keeps a selection style and clear
    /// selected backgrounds; the row draws its own selection.
    /// </summary>
    public bool UsesSystemSelectionControl
    {
        get => _systemSelectionControl;
        set
        {
            _systemSelectionControl = value;
            SelectionStyle = value ? UITableViewCellSelectionStyle.Default : UITableViewCellSelectionStyle.None;
            SelectedBackgroundView = value ? new UIView { BackgroundColor = UIColor.Clear } : null;
            MultipleSelectionBackgroundView = value ? new UIView { BackgroundColor = UIColor.Clear } : null;
            _checkbox.Hidden = !_selectionMode || value;
        }
    }

    public bool IsChecked
    {
        get => _checked;
        set
        {
            _checked = value;
            _checkbox.Icon = value ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle;
            _checkbox.Tint = value ? WinoStyle.Accent : WinoStyle.SecondaryText;
        }
    }

    private void UpdateHover() => _hoverBar.Hidden = !(_hovered && _hoverActionsEnabled && HasHoverActions && !_selectionMode && !_model.IsBusy);

    private void PointerChanged(UIHoverGestureRecognizer recognizer)
        => IsHovered = recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;

    public override void SetSelected(bool selected, bool animated)
    {
        base.SetSelected(selected, animated);
        IsChecked = selected;
        ApplySelection();
    }

    public override void SetHighlighted(bool highlighted, bool animated)
    {
        base.SetHighlighted(highlighted, animated);
        ApplySelection();
    }

    /// <summary>The Windows look: selected rows get the subtle fill and accent pill; select mode a 12% accent tint.</summary>
    private void ApplySelection()
    {
        bool selected = Selected;
        bool pressed = Highlighted && !selected;
        _highlight.Hidden = !(selected || pressed || (_hovered && !selected));
        if (selected && _selectionMode) _highlight.Fill = WinoStyle.Accent.ColorWithAlpha(0.12f);
        else if (selected) _highlight.Fill = WinoStyle.SelectedFill;
        else _highlight.Fill = UIColor.Label.ColorWithAlpha(pressed ? 0.08f : 0.045f);
        _pill.Hidden = !selected || _selectionMode;
        _pill.Fill = WinoStyle.Accent;
        AccessibilityTraits = selected ? UIAccessibilityTrait.Selected : UIAccessibilityTrait.None;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        // Windows ListViewItem pill: vertically centred, shorter than the row.
        var height = (nfloat)Math.Clamp((double)_box.Bounds.Height - 28, 16, 40);
        if (_pillHeight.Constant != height) _pillHeight.Constant = height;
    }

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

        _draftTag.Text = $"[{model.DraftLabel}]";
        _draftTag.Hidden = !model.IsDraft;
        _sender.Text = model.IsDraft && string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender;
        _threadCount.Text = model.ThreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _threadCount.Hidden = model.Kind != WinoMailRowKind.ThreadHead || model.ThreadCount < 2;
        ApplyNickname(_nicknameLeft, model, AccountNicknamePosition.Left);
        ApplyNickname(_nicknameRight, model, AccountNicknamePosition.Right);
        _pinGlyph.Hidden = !model.IsPinned;
        _attachmentGlyph.Hidden = !model.HasAttachments;
        _flagGlyph.Hidden = !model.IsFlagged;
        if (model.IsBusy) _busy.StartAnimating();
        else _busy.StopAnimating();

        _chevron.Hidden = model.Kind != WinoMailRowKind.ThreadHead || model.ThreadCount < 2;
        if (_shownExpanded != model.IsThreadExpanded)
        {
            _shownExpanded = model.IsThreadExpanded;
            SetButtonImage(_chevron, WinoIcons.Image(model.IsThreadExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight, 10));
        }
        WinoAccessibility.Label(_chevron, model.ThreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture));
        _subject.Text = model.Subject;
        _date.Text = model.DateText;
        _unreadDot.Hidden = !model.IsUnread;

        _preview.Text = model.Preview;
        _preview.Hidden = compact || !model.ShowPreview || string.IsNullOrWhiteSpace(model.Preview);

        ApplyTiles(model);

        ApplyHoverActions();
        UpdateHover();

        AccessibilityLabel = model.AccessibilityText;
        AccessibilityValue = model.AccessibleDateText;
        ApplyColors();
    }

    private static void ApplyNickname(WinoChipView chip, WinoMailRowModel model, AccountNicknamePosition position)
    {
        bool show = model.ShowNickname && model.NicknamePosition == position;
        chip.Hidden = !show;
        if (!show) return;
        var tint = model.AccountColor ?? WinoStyle.Accent;
        chip.Text = model.AccountNickname ?? string.Empty;
        chip.Fill = tint.ColorWithAlpha(0.16f);
        chip.TextColor = tint;
    }

    private void ApplyTiles(WinoMailRowModel model)
    {
        bool compact = model.Density == WinoMailRowDensity.Compact;
        var tiles = model.Tiles.Take(WinoMailRowMetrics.MaxTiles).ToList();
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
    /// Updates the retained tile views in place and adds or removes only the difference (shared
    /// <see cref="WinoListDiff.ReusablePrefix"/>). Switching between compact and detailed starts over.
    /// </summary>
    private void SyncTiles(List<WinoMailRowTile> tiles, bool compact)
    {
        int keep = compact == _shownCompactTiles
            ? Math.Min(_tileViews.Count, WinoListDiff.ReusablePrefix(_shownTiles, tiles, (shown, next) => !compact || shown.IsIntelligence == next.IsIntelligence))
            : 0;

        for (int i = _tileViews.Count - 1; i >= keep; i--)
        {
            var view = _tileViews[i];
            if (view.Superview is UIStackView stack) stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
            _tileViews.RemoveAt(i);
        }

        for (int i = 0; i < keep; i++)
        {
            if (tiles[i] == _shownTiles[i]) continue;
            if (compact) CompactTile(tiles[i], _tileViews[i]);
            else DetailedTile(tiles[i], _tileViews[i] as WinoChipView);
        }

        for (int i = keep; i < tiles.Count; i++)
        {
            UIView view = compact ? CompactTile(tiles[i], null) : DetailedTile(tiles[i], null);
            _tileViews.Add(view);
            if (compact) _compactTiles.AddArrangedSubview(view);
            else _tiles.AddItem(view);
        }
        if (!compact) _tiles.ItemsChanged();

        _shownTiles = tiles;
        _shownCompactTiles = compact;
    }

    /// <summary>Configures <paramref name="existing"/> (same tile kind) or creates a new compact tile.</summary>
    private static UIView CompactTile(WinoMailRowTile tile, UIView? existing)
    {
        if (!tile.IsIntelligence)
        {
            if (existing is not WinoSurfaceView dot || existing is WinoChipView)
            {
                dot = new WinoSurfaceView { CornerRadius = 4 };
                WinoLayout.Size(dot, 8, 8);
            }
            dot.Fill = tile.Background ?? WinoStyle.Accent;
            dot.IsAccessibilityElement = true;
            dot.AccessibilityLabel = tile.Text;
            return dot;
        }
        if (existing is not WinoChipView chip)
        {
            chip = new WinoChipView(18) { Fill = WinoStyle.SubtleFill, TextColor = WinoStyle.SecondaryText };
            WinoLayout.Size(chip, 18, 18);
        }
        chip.SetGlyph(tile.Glyph, 10);
        chip.DotColor = string.IsNullOrEmpty(tile.Glyph) ? WinoStyle.SecondaryText : null;
        chip.AccessibilityLabel = tile.Tooltip ?? tile.Text;
        return chip;
    }

    /// <summary>Configures <paramref name="existing"/> or creates a new detailed tile (also the measuring prototype).</summary>
    internal static WinoChipView DetailedTile(WinoMailRowTile tile, WinoChipView? existing)
    {
        var chip = existing ?? new WinoChipView(WinoMailRowMetrics.TileHeight) { CornerRadius = 4, MaxTextWidth = 180 };
        chip.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        chip.Text = tile.Text;
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
            var background = tile.Background ?? WinoStyle.Accent.ColorWithAlpha(0.16f);
            chip.Fill = background;
            chip.TextColor = tile.Foreground ?? ReadableText(background);
            chip.SetGlyph(null);
            chip.DotColor = null;
        }
        chip.AccessibilityLabel = tile.Tooltip ?? tile.Text;
        return chip;
    }

    /// <summary>Black or white, whichever reads on the given fill (Windows GetReadableTextColor).</summary>
    public static UIColor ReadableText(UIColor background) => WinoStyle.ReadableText(background);

    private void AccentChanged(object? sender, EventArgs args)
    {
        ApplyColors();
        ApplySelection();
    }

    private void ApplyColors()
    {
        bool unread = _model.IsUnread;
        _sender.Font = WinoMailRowMeasure.Font(WinoMailRowMetrics.PrimaryFontSize, unread ? UIFontWeight.Bold : UIFontWeight.Regular);
        _sender.TextColor = WinoStyle.PrimaryText;
        _subject.Font = WinoMailRowMeasure.Font(WinoMailRowMetrics.PrimaryFontSize, unread ? UIFontWeight.Semibold : UIFontWeight.Regular);
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
        ApplySelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentChanged -= AccentChanged;
            Subscription = null;
            HoverActionInvoked = null;
            ThreadToggleRequested = null;
            CheckboxToggled = null;
        }
        base.Dispose(disposing);
    }
}

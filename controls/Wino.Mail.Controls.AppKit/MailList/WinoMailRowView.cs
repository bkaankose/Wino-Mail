using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Presentation.AppKit;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>
/// The Wino mail row, laid out like the Windows MailListItemTemplates: a highlight box (inset by
/// <see cref="WinoMailRowMetrics.RowGap"/> so neighbours do not touch) that holds the selection pill,
/// the avatar, then three lines that each span to the trailing edge.
/// Line 1: nickname, draft tag, sender (bold when unread), thread count, then the attachment and
/// flag glyphs right-aligned. Line 2: thread chevron, subject (semibold accent when unread), the
/// date right-aligned and the unread dot. Line 3: the preview. Detailed rows add a tile row for
/// categories and intelligence that wraps onto more lines; compact rows collapse the tiles into line 1.
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
    private readonly WinoFlowView _tiles;
    private readonly WinoSurfaceView _hoverBar;
    private readonly NSButton[] _hoverButtons;
    private readonly HoverActionKind[] _hoverKinds = new HoverActionKind[3];
    private readonly WinoIconGlyph[] _shownHoverGlyphs = new WinoIconGlyph[3];
    private readonly List<NSLayoutConstraint> _hoverButtonSize = new();
    private NSLayoutConstraint[] _hoverPlacement = [];
    private MailHoverActionButtonSize _hoverButtonSizeSetting = MailHoverActionButtonSize.Small;
    private HoverActionPosition _hoverPosition = HoverActionPosition.RightCenter;
    private HoverActionAnimation _hoverAnimation = HoverActionAnimation.Popup;
    private bool _hoverShown;
    private int _hoverGeneration;
    private readonly NSLayoutConstraint _boxLeading;
    private readonly NSLayoutConstraint _contentTop;
    private readonly NSLayoutConstraint _contentBottom;
    private readonly List<NSView> _tileViews = new();
    private List<WinoMailRowTile> _shownTiles = new();
    private bool _shownCompactTiles;
    private bool? _shownExpanded;
    private WinoMailRowModel _model = new();
    private bool _hovered;
    private bool _selectionMode;
    private bool _hoverActionsEnabled = true;
    private IDisposable? _subscription;
    private HoverActionSet _hoverActions = new(HoverActionKind.Archive, HoverActionKind.Delete, HoverActionKind.ToggleFlag);
    private (string Archive, string Delete, string Flag, string Unflag, string Read, string Unread, string Junk) _labels =
        ("Archive", "Delete", "Flag", "Clear flag", "Mark as read", "Mark as unread", "Move to Junk");

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
            _box.TopAnchor.ConstraintEqualTo(TopAnchor, (nfloat)WinoMailRowMetrics.RowGap),
            _box.BottomAnchor.ConstraintEqualTo(BottomAnchor, -(nfloat)WinoMailRowMetrics.RowGap)
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
        _date = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.LabelWithOpacity(0.7));
        _date.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _date.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _unreadDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_unreadDot, 6, 6);
        _line2 = WinoLayout.HStack(8, _chevron, _subject, _date, _unreadDot);
        _line2.Distribution = NSStackViewDistribution.Fill;
        _line2.SetCustomSpacing(4, _chevron);

        // ---- Line 3 and tiles ----
        _preview = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.LabelWithOpacity(0.7));
        _preview.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _preview.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        // Windows: a WinoWrapPanel with 4pt vertical spacing, so many tiles wrap instead of clipping.
        _tiles = new WinoFlowView { Spacing = WinoMailRowMetrics.TileSpacing, LineSpacing = WinoMailRowMetrics.TileLineSpacing, Hidden = true };
        _tiles.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _tiles.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Vertical);

        _text = WinoLayout.VStack(WinoMailRowMetrics.LineSpacing, _line1, _line2, _preview, _tiles);
        _text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        // The flow view spans the column so it knows where to wrap; tiles keep their fitting width inside it.
        foreach (var line in new NSView[] { _line1, _line2, _preview, _tiles })
            line.TrailingAnchor.ConstraintEqualTo(_text.TrailingAnchor).Active = true;

        _content = WinoLayout.HStack(WinoMailRowMetrics.AvatarSpacing, _checkbox, _avatar, _text);
        _content.Distribution = NSStackViewDistribution.Fill;
        _content.SetCustomSpacing((nfloat)WinoMailRowMetrics.CheckboxSpacing, _checkbox);
        _box.AddSubview(_content);
        _contentTop = _content.TopAnchor.ConstraintGreaterThanOrEqualTo(_box.TopAnchor, (nfloat)WinoMailRowMetrics.Padding(WinoMailRowDensity.Medium));
        _contentBottom = _content.BottomAnchor.ConstraintLessThanOrEqualTo(_box.BottomAnchor, -(nfloat)WinoMailRowMetrics.Padding(WinoMailRowDensity.Medium));
        // The table sizes the row from WinoMailRowMetrics; if a measurement is a point off, let the
        // padding give instead of breaking the centring.
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

        // ---- Hover actions (Windows: trailing edge, over the row content) ----
        _hoverButtons = [HoverButton(0), HoverButton(1), HoverButton(2)];
        var hoverStack = WinoLayout.HStack(2, _hoverButtons);
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
        ApplyHoverPlacement();

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.CellRole;
        ApplyHoverActions();
        ApplyColors();
    }

    private static WinoChipView NicknameChip()
    {
        var chip = new WinoChipView(16) { Hidden = true, CornerRadius = 4 };
        chip.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        return chip;
    }

    private NSButton HoverButton(int slot)
    {
        var button = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.PrimaryText,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var width = button.WidthAnchor.ConstraintEqualTo(28);
        var height = button.HeightAnchor.ConstraintEqualTo(26);
        width.Active = height.Active = true;
        _hoverButtonSize.Add(width);
        _hoverButtonSize.Add(height);
        button.Activated += (_, _) =>
        {
            if (_hoverKinds[slot] != HoverActionKind.None) HoverActionInvoked?.Invoke(this, _hoverKinds[slot]);
        };
        return button;
    }

    /// <summary>Raised with the action of the hover button that was clicked.</summary>
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
                button.Image = WinoIcons.Image(glyph, 14);
            }
            var label = HoverLabel(kind);
            button.ToolTip = label;
            WinoAccessibility.Label(button, label);
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

    /// <summary>Small keeps the compact Mac buttons; Medium and Large are the Windows 32 and 40 point squares.</summary>
    public MailHoverActionButtonSize HoverButtonSize
    {
        get => _hoverButtonSizeSetting;
        set
        {
            if (_hoverButtonSizeSetting == value) return;
            _hoverButtonSizeSetting = value;
            var (width, height) = value switch
            {
                MailHoverActionButtonSize.Large => (40, 40),
                MailHoverActionButtonSize.Medium => (32, 32),
                _ => (28, 26),
            };
            for (int i = 0; i < _hoverButtonSize.Count; i += 2)
            {
                _hoverButtonSize[i].Constant = width;
                _hoverButtonSize[i + 1].Constant = height;
            }
        }
    }

    public HoverActionPosition HoverPosition
    {
        get => _hoverPosition;
        set { if (_hoverPosition == value) return; _hoverPosition = value; ApplyHoverPlacement(); }
    }

    public HoverActionAnimation HoverAnimation
    {
        get => _hoverAnimation;
        set => _hoverAnimation = value;
    }

    private void ApplyHoverPlacement()
    {
        NSLayoutConstraint.DeactivateConstraints(_hoverPlacement);
        const double edge = 4;
        var trailing = _hoverBar.TrailingAnchor.ConstraintEqualTo(_box.TrailingAnchor, -(nfloat)WinoMailRowMetrics.InnerTrailing);
        var centerX = _hoverBar.CenterXAnchor.ConstraintEqualTo(_box.CenterXAnchor);
        var top = _hoverBar.TopAnchor.ConstraintEqualTo(_box.TopAnchor, (nfloat)edge);
        var bottom = _hoverBar.BottomAnchor.ConstraintEqualTo(_box.BottomAnchor, -(nfloat)edge);
        var centerY = _hoverBar.CenterYAnchor.ConstraintEqualTo(_box.CenterYAnchor);
        _hoverPlacement = _hoverPosition switch
        {
            HoverActionPosition.RightTop => [trailing, top],
            HoverActionPosition.RightBottom => [trailing, bottom],
            HoverActionPosition.TopCenter => [centerX, top],
            HoverActionPosition.BottomCenter => [centerX, bottom],
            _ => [trailing, centerY],
        };
        NSLayoutConstraint.ActivateConstraints(_hoverPlacement);
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

    private void UpdateHover() => SetHoverShown(_hovered && _hoverActionsEnabled && HasHoverActions && !_selectionMode && !_model.IsBusy, animate: true);

    /// <summary>Shows or hides the hover bar with the chosen animation (Windows WinoMailListViewItem: 150 ms in, 90 ms out).</summary>
    private void SetHoverShown(bool shown, bool animate)
    {
        if (shown == _hoverShown && (animate || _hoverBar.Hidden == !shown)) return;
        _hoverShown = shown;
        int generation = ++_hoverGeneration;
        var layer = _hoverBar.Layer;
        bool motion = animate && _hoverAnimation != HoverActionAnimation.NoAnimation && layer is not null
            && !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;
        layer?.RemoveAllAnimations();
        if (!motion)
        {
            _hoverBar.AlphaValue = 1;
            _hoverBar.Hidden = !shown;
            return;
        }

        LayoutSubtreeIfNeeded();
        var bounds = _hoverBar.Bounds;
        var offset = HoverOffsetTransform(bounds);
        var duration = shown ? 0.15 : 0.09;
        var transform = CoreAnimation.CABasicAnimation.FromKeyPath("transform");
        transform.Duration = duration;
        transform.From = NSValue.FromCATransform3D(shown ? offset : CoreAnimation.CATransform3D.Identity);
        transform.To = NSValue.FromCATransform3D(shown ? CoreAnimation.CATransform3D.Identity : offset);
        transform.TimingFunction = shown
            ? CoreAnimation.CAMediaTimingFunction.FromControlPoints(0.1f, 0.9f, 0.2f, 1f)
            : CoreAnimation.CAMediaTimingFunction.FromControlPoints(0.7f, 0f, 1f, 0.5f);
        layer!.AddAnimation(transform, "hover");

        var fade = CoreAnimation.CABasicAnimation.FromKeyPath("opacity");
        fade.Duration = duration;
        fade.From = NSNumber.FromDouble(shown ? 0 : 1);
        fade.To = NSNumber.FromDouble(shown ? 1 : 0);
        fade.TimingFunction = transform.TimingFunction;
        fade.FillMode = CoreAnimation.CAFillMode.Forwards;
        fade.RemovedOnCompletion = false;
        layer.AddAnimation(fade, "hover-fade");
        _hoverBar.Hidden = false;

        // Not NSAnimationContext: a managed exception inside its block cannot unwind through AppKit and aborts the app.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAfter(new CoreFoundation.DispatchTime(CoreFoundation.DispatchTime.Now, TimeSpan.FromSeconds(duration)), () =>
        {
            if (generation != _hoverGeneration) return;
            _hoverBar.Layer?.RemoveAllAnimations();
            _hoverBar.Hidden = !shown;
        });
    }

    private CoreAnimation.CATransform3D HoverOffsetTransform(CGRect bounds)
    {
        if (_hoverAnimation == HoverActionAnimation.Popup)
        {
            // Layers scale around their origin on macOS; recentre so the bar grows from its middle.
            const double scale = 0.85;
            var cx = bounds.Width / 2;
            var cy = bounds.Height / 2;
            return CoreAnimation.CATransform3D.MakeTranslation(cx, cy, 0).Scale((nfloat)scale, (nfloat)scale, 1).Translate(-cx, -cy, 0);
        }
        const double slide = 24;
        return _hoverPosition switch
        {
            // The bar is a flipped view, so y grows downward as on Windows.
            HoverActionPosition.TopCenter => CoreAnimation.CATransform3D.MakeTranslation(0, -(nfloat)slide, 0),
            HoverActionPosition.BottomCenter => CoreAnimation.CATransform3D.MakeTranslation(0, (nfloat)slide, 0),
            _ => CoreAnimation.CATransform3D.MakeTranslation((nfloat)slide, 0, 0),
        };
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

        ApplyHoverActions();
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
        var tiles = model.Tiles.Take(WinoMailRowMetrics.MaxTiles).ToList();
        if (compact != _shownCompactTiles || !tiles.SequenceEqual(_shownTiles))
            SyncTiles(tiles, compact);
        _compactTiles.Hidden = !compact || model.Tiles.Count == 0;
        _tiles.Hidden = compact || model.Tiles.Count == 0;
        // Custom spacing changes invalidate the stack's layout, so they are applied only when the tile line moves.
        double tileSpacing = _tiles.Hidden ? 0 : WinoMailRowMetrics.LineSpacing + WinoMailRowMetrics.TileLineTopMargin(model.Density);
        var spacingKey = (tileSpacing, _preview.Hidden);
        if (spacingKey == _shownSpacing) return;
        _shownSpacing = spacingKey;
        _text.SetCustomSpacing((nfloat)WinoMailRowMetrics.LineSpacing, _line2);
        _text.SetCustomSpacing((nfloat)WinoMailRowMetrics.LineSpacing, _preview);
        if (!_tiles.Hidden) _text.SetCustomSpacing((nfloat)tileSpacing, _preview.Hidden ? _line2 : _preview);
    }

    private (double TileSpacing, bool PreviewHidden)? _shownSpacing;

    /// <summary>
    /// Updates the retained tile views in place and adds or removes only the difference. Switching
    /// between compact and detailed moves tiles to another line with another shape, so it starts over;
    /// a compact tile that changes between category dot and intelligence chip is rebuilt from there on.
    /// </summary>
    private void SyncTiles(List<WinoMailRowTile> tiles, bool compact)
    {
        int keep = compact == _shownCompactTiles
            ? Math.Min(_tileViews.Count, WinoListDiff.ReusablePrefix(_shownTiles, tiles, (shown, next) => !compact || shown.IsIntelligence == next.IsIntelligence))
            : 0;

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

        for (int i = keep; i < tiles.Count; i++)
        {
            var view = compact ? CompactTile(tiles[i], null) : DetailedTile(tiles[i], null);
            _tileViews.Add(view);
            if (compact) _compactTiles.AddArrangedSubview(view);
            else _tiles.AddItem(view);
        }
        if (!compact) _tiles.ItemsChanged();

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

    /// <summary>Configures <paramref name="existing"/> or creates a new detailed tile (also the measuring prototype).</summary>
    internal static NSView DetailedTile(WinoMailRowTile tile, NSView? existing)
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
        return Wino.Core.Domain.Presentation.ReadableTextColor.PrefersDarkText(rgb.RedComponent, rgb.GreenComponent, rgb.BlueComponent)
            ? NSColor.Black
            : NSColor.White;
    }

    private static readonly NSFont SenderUnreadFont = NSFont.SystemFontOfSize(13, NSFontWeight.Bold);
    private static readonly NSFont SubjectUnreadFont = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
    private static readonly NSFont ReadFont = NSFont.SystemFontOfSize(13, NSFontWeight.Regular);
    private bool? _shownUnread;

    private void ApplyColors()
    {
        bool unread = _model.IsUnread;
        // Setting a font re-measures the field; the read state rarely changes between binds of the same row.
        if (_shownUnread != unread)
        {
            _shownUnread = unread;
            _sender.Font = unread ? SenderUnreadFont : ReadFont;
            _subject.Font = unread ? SubjectUnreadFont : ReadFont;
        }
        _sender.TextColor = WinoStyle.PrimaryText;
        _subject.TextColor = unread ? WinoStyle.Accent : WinoStyle.PrimaryText;
        _unreadDot.Fill = WinoStyle.Accent;
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Subscription = null;
        Item = null;
        _hovered = false;
        SetHoverShown(false, animate: false);
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

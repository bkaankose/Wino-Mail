using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Chip;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.MailList;

/// <summary>
/// The Wino mail row (UIKit <c>WinoMailRowCell</c>, AppKit <c>WinoMailRowView</c>) as an Android view inflated from
/// <c>wino_mail_row.xml</c>. Selection is the Windows ListViewItem look from the canvas: the accent at 10% and a 3 dp
/// accent pill, never the system highlight. Select mode shows a round check before the avatar. Unread rows use a medium
/// sender and an accent subject with the unread dot. The row only renders; the adapter and page own clicks, swipes and
/// selection. Usable from XML as <c>com.winomail.controls.WinoMailRowView</c>.
/// </summary>
[Register("com.winomail.controls.WinoMailRowView")]
public class WinoMailRowView : FrameLayout
{
    private static readonly Typeface Regular = Typeface.Default ?? throw new InvalidOperationException("No default typeface.");
    private static readonly Typeface Medium = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal) ?? Regular;

    private readonly GradientDrawable _highlightFill = new();
    private readonly GradientDrawable _pillFill = new();
    private readonly GradientDrawable _unreadFill = new();
    private readonly List<View> _compactTileViews = new();
    private readonly List<View> _tileViews = new();
    private RowParts? _parts;
    private WinoMailRowModel _model = new();
    private IReadOnlyList<WinoMailRowTile> _shownTiles = Array.Empty<WinoMailRowTile>();
    private bool _shownCompactTiles;
    private bool _isSelectionMode;
    private bool _isChecked;
    private bool _isRowSelected;

    public WinoMailRowView(Context context) : base(context) => Initialize(context);
    public WinoMailRowView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoMailRowView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize(Context context)
    {
        LayoutInflater.From(context)?.Inflate(Resource.Layout.wino_mail_row, this, true);
        _parts = new RowParts(this);
        var p = Parts;

        float radius = WinoUnits.DpF(context, 4);
        _highlightFill.SetCornerRadius(radius);
        p.Highlight.Background = _highlightFill;
        _pillFill.SetCornerRadius(WinoUnits.DpF(context, WinoMailRowMetrics.PillWidth / 2));
        p.Pill.Background = _pillFill;
        _unreadFill.SetShape(ShapeType.Oval);
        p.Unread.Background = _unreadFill;

        p.Check.GlyphSize = 20;
        p.Check.Click += (_, _) => CheckboxToggled?.Invoke(this, EventArgs.Empty);
        p.Pin.Icon = WinoIconGlyph.Pin;
        p.Pin.GlyphSize = 12;
        p.Attachment.Icon = WinoIconGlyph.Attachment;
        p.Attachment.GlyphSize = 14;
        p.Flag.Icon = WinoIconGlyph.Flag;
        p.Flag.GlyphSize = 14;
        p.Busy.Icon = WinoIconGlyph.Clock;
        p.Busy.GlyphSize = 12;
        p.Chevron.GlyphSize = 11;
        p.Chevron.Clickable = true;
        p.Chevron.Click += (_, _) => ThreadToggleRequested?.Invoke(this, EventArgs.Empty);
        foreach (var chip in new[] { p.NicknameLeft, p.NicknameRight }) chip.MaxTextWidth = 96;

        // Touch feedback: the subtle fill as a ripple bounded by the highlight box.
        var mask = new GradientDrawable();
        mask.SetColor(Color.White);
        mask.SetCornerRadius(radius);
        int inset = WinoUnits.Dp(context, 4);
        int gap = WinoUnits.Dp(context, 2);
        Foreground = new RippleDrawable(ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, new InsetDrawable(mask, inset, gap, inset, gap));
        Clickable = true;
        LongClickable = true;
        Focusable = true;
        ApplyColors();
        ApplySelection();
    }

    private RowParts Parts => _parts ?? throw new InvalidOperationException("The mail row is not initialized.");

    /// <summary>The views of wino_mail_row.xml.</summary>
    private sealed class RowParts(View root)
    {
        public readonly View Highlight = root.Require<View>(Resource.Id.wino_row_highlight);
        public readonly View Pill = root.Require<View>(Resource.Id.wino_row_pill);
        public readonly View Content = root.Require<View>(Resource.Id.wino_row_content);
        public readonly WinoIconView Check = root.Require<WinoIconView>(Resource.Id.wino_row_check);
        public readonly WinoContactPicture Avatar = root.Require<WinoContactPicture>(Resource.Id.wino_row_avatar);
        public readonly WinoChipView NicknameLeft = root.Require<WinoChipView>(Resource.Id.wino_row_nickname_left);
        public readonly WinoChipView NicknameRight = root.Require<WinoChipView>(Resource.Id.wino_row_nickname_right);
        public readonly TextView Draft = root.Require<TextView>(Resource.Id.wino_row_draft);
        public readonly TextView Sender = root.Require<TextView>(Resource.Id.wino_row_sender);
        public readonly TextView ThreadCount = root.Require<TextView>(Resource.Id.wino_row_thread_count);
        public readonly WinoIconView Pin = root.Require<WinoIconView>(Resource.Id.wino_row_pin);
        public readonly WinoIconView Attachment = root.Require<WinoIconView>(Resource.Id.wino_row_attachment);
        public readonly WinoIconView Flag = root.Require<WinoIconView>(Resource.Id.wino_row_flag);
        public readonly WinoIconView Busy = root.Require<WinoIconView>(Resource.Id.wino_row_busy);
        public readonly LinearLayout CompactTiles = root.Require<LinearLayout>(Resource.Id.wino_row_compact_tiles);
        public readonly TextView Date = root.Require<TextView>(Resource.Id.wino_row_date);
        public readonly WinoIconView Chevron = root.Require<WinoIconView>(Resource.Id.wino_row_chevron);
        public readonly TextView Subject = root.Require<TextView>(Resource.Id.wino_row_subject);
        public readonly View Unread = root.Require<View>(Resource.Id.wino_row_unread);
        public readonly TextView Preview = root.Require<TextView>(Resource.Id.wino_row_preview);
        public readonly ChipGroup Tiles = root.Require<ChipGroup>(Resource.Id.wino_row_tiles);
    }

    /// <summary>The select-mode check was tapped.</summary>
    public event EventHandler? CheckboxToggled;

    /// <summary>The thread chevron was tapped.</summary>
    public event EventHandler? ThreadToggleRequested;

    /// <summary>The row the adapter bound (the projection row); the view never reads it.</summary>
    public object? Item { get; set; }

    public WinoMailRowModel Model => _model;

    /// <summary>Shows the round select check before the avatar (multi-select ActionMode).</summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set { _isSelectionMode = value; ApplySelection(); }
    }

    /// <summary>The check state in select mode.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; ApplySelection(); }
    }

    /// <summary>The Windows selected look: accent fill and pill.</summary>
    public bool IsRowSelected
    {
        get => _isRowSelected;
        set { _isRowSelected = value; ApplySelection(); }
    }

    public void Apply(WinoMailRowModel model)
    {
        var p = Parts;
        _model = model;
        var context = this.RequireContext();
        var density = model.Density;
        bool compact = density == WinoMailRowDensity.Compact;

        SetMinimumHeight(WinoUnits.Dp(context, WinoMailRowSizes.MinHeight(density)));
        int indent = model.Kind == WinoMailRowKind.ThreadChild ? WinoUnits.Dp(context, WinoMailRowMetrics.ChildIndent(density)) : 0;
        int vertical = WinoUnits.Dp(context, WinoMailRowSizes.VerticalPadding(density));
        p.Content.SetPadding(WinoUnits.Dp(context, 14) + indent, vertical, WinoUnits.Dp(context, 16), vertical);
        if (p.Highlight.LayoutParameters is MarginLayoutParams highlight)
        {
            highlight.MarginStart = WinoUnits.Dp(context, WinoMailRowMetrics.ContentLeading) + indent;
            p.Highlight.LayoutParameters = highlight;
        }

        p.Avatar.Visibility = model.ShowPicture ? ViewStates.Visible : ViewStates.Gone;
        p.Avatar.Size = WinoMailRowSizes.AvatarSize(density);
        p.Avatar.Image = model.Picture;
        p.Avatar.SetIdentity(string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender,
            string.IsNullOrWhiteSpace(model.SenderAddress) ? model.Sender : model.SenderAddress);

        p.Draft.Text = $"[{model.DraftLabel}]";
        p.Draft.Visibility = Visible(model.IsDraft);
        p.Sender.Text = model.IsDraft && string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender;
        bool threaded = model.Kind == WinoMailRowKind.ThreadHead && model.ThreadCount > 1;
        p.ThreadCount.Text = model.ThreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        p.ThreadCount.Visibility = Visible(threaded);
        ApplyNickname(p.NicknameLeft, model, AccountNicknamePosition.Left);
        ApplyNickname(p.NicknameRight, model, AccountNicknamePosition.Right);
        p.Pin.Visibility = Visible(model.IsPinned);
        p.Attachment.Visibility = Visible(model.HasAttachments);
        p.Flag.Visibility = Visible(model.IsFlagged);
        p.Busy.Visibility = Visible(model.IsBusy);
        p.Date.Text = model.DateText;

        p.Chevron.Visibility = Visible(threaded);
        p.Chevron.Icon = model.IsThreadExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
        p.Chevron.ContentDescription = model.IsThreadExpanded ? Wino.Core.Domain.Translator.MacOS_MailList_CollapseThread : Wino.Core.Domain.Translator.MacOS_MailList_ExpandThread;
        p.Subject.Text = model.Subject;
        p.Unread.Visibility = Visible(model.IsUnread);

        p.Preview.Text = model.Preview;
        p.Preview.Visibility = Visible(!compact && model.ShowPreview && !string.IsNullOrWhiteSpace(model.Preview));

        ApplyTiles(model);
        ContentDescription = model.AccessibilityText;
        ApplyColors();
    }

    private static ViewStates Visible(bool visible) => visible ? ViewStates.Visible : ViewStates.Gone;

    private void ApplyNickname(WinoChipView chip, WinoMailRowModel model, AccountNicknamePosition position)
    {
        bool show = model.ShowNickname && model.NicknamePosition == position;
        chip.Visibility = Visible(show);
        if (!show) return;
        var tint = model.AccountColor ?? WinoColors.Accent(this.RequireContext());
        chip.Text = model.AccountNickname ?? string.Empty;
        chip.Fill = WinoColors.WithAlpha(tint, 0.16);
        chip.TextColor = tint;
    }

    /// <summary>Rebuilds the tile views only when the tiles change (shared <see cref="WinoListDiff.ReusablePrefix"/>).</summary>
    private void ApplyTiles(WinoMailRowModel model)
    {
        var p = Parts;
        bool compact = model.Density == WinoMailRowDensity.Compact;
        var tiles = model.Tiles.Take(WinoMailRowMetrics.MaxTiles).ToArray();
        if (compact != _shownCompactTiles || !tiles.SequenceEqual(_shownTiles))
        {
            var context = this.RequireContext();
            int keep = compact == _shownCompactTiles ? WinoListDiff.ReusablePrefix(_shownTiles, tiles, static (shown, next) => shown == next) : 0;
            var views = compact ? _compactTileViews : _tileViews;
            var host = compact ? (ViewGroup)p.CompactTiles : p.Tiles;
            if (compact != _shownCompactTiles)
            {
                p.CompactTiles.RemoveAllViews();
                p.Tiles.RemoveAllViews();
                _compactTileViews.Clear();
                _tileViews.Clear();
            }
            for (int index = views.Count - 1; index >= keep; index--)
            {
                host.RemoveView(views[index]);
                views.RemoveAt(index);
            }
            for (int index = keep; index < tiles.Length; index++)
            {
                var view = compact ? CompactTile(context, tiles[index]) : DetailedTile(context, tiles[index]);
                views.Add(view);
                host.AddView(view);
            }
            _shownTiles = tiles;
            _shownCompactTiles = compact;
        }
        p.CompactTiles.Visibility = Visible(compact && tiles.Length > 0);
        p.Tiles.Visibility = Visible(!compact && tiles.Length > 0);
    }

    private static View CompactTile(Context context, WinoMailRowTile tile)
    {
        int size = WinoUnits.Dp(context, tile.IsIntelligence ? 18 : 8);
        if (!tile.IsIntelligence)
        {
            var dot = new View(context) { ContentDescription = tile.Text };
            var fill = new GradientDrawable();
            fill.SetShape(ShapeType.Oval);
            fill.SetColor(tile.Background ?? WinoColors.Accent(context));
            dot.Background = fill;
            dot.LayoutParameters = new LinearLayout.LayoutParams(size, size) { MarginStart = WinoUnits.Dp(context, 3) };
            return dot;
        }
        var chip = new WinoChipView(context) { ContentDescription = tile.Tooltip ?? tile.Text };
        chip.SetGlyph(tile.Glyph, 10);
        chip.DotColor = string.IsNullOrEmpty(tile.Glyph) ? WinoColors.SecondaryText(context) : null;
        chip.SetPadding(0, 0, 0, 0);
        chip.SetGravity(GravityFlags.Center);
        chip.LayoutParameters = new LinearLayout.LayoutParams(size, size) { MarginStart = WinoUnits.Dp(context, 3) };
        return chip;
    }

    private static WinoChipView DetailedTile(Context context, WinoMailRowTile tile)
    {
        var chip = new WinoChipView(context) { ChipHeight = WinoMailRowMetrics.TileHeight, MaxTextWidth = 180, ContentDescription = tile.Tooltip ?? tile.Text };
        chip.Text = tile.Text;
        if (tile.IsIntelligence)
        {
            chip.Fill = WinoColors.SubtleFill(context);
            chip.TextColor = WinoColors.PrimaryText(context);
            chip.SetGlyph(tile.Glyph, 11);
            chip.DotColor = string.IsNullOrEmpty(tile.Glyph) ? WinoColors.SecondaryText(context) : null;
            chip.GlyphTint = tile.IsWarning ? WinoColors.Caution(context) : WinoColors.SecondaryText(context);
        }
        else
        {
            var background = tile.Background ?? WinoColors.WithAlpha(WinoColors.Accent(context), 0.16);
            chip.Fill = background;
            chip.TextColor = tile.Foreground ?? WinoColors.ReadableText(background);
            chip.SetGlyph((string?)null);
        }
        return chip;
    }

    private void ApplyColors()
    {
        var p = Parts;
        var context = this.RequireContext();
        bool unread = _model.IsUnread;
        p.Sender.Typeface = unread ? Medium : Regular;
        p.Subject.Typeface = unread ? Medium : Regular;
        p.Subject.SetTextColor(unread ? WinoColors.AccentText(context) : WinoColors.PrimaryText(context));
        _unreadFill.SetColor(WinoColors.Accent(context));
        _pillFill.SetColor(WinoColors.Accent(context));
        p.Attachment.SetTextColor(WinoColors.Caution(context));
        p.Flag.SetTextColor(WinoColors.Flagged(context));
        p.Pin.SetTextColor(WinoColors.SecondaryText(context));
        p.Busy.SetTextColor(WinoColors.SecondaryText(context));
        p.Chevron.SetTextColor(WinoColors.SecondaryText(context));
    }

    private void ApplySelection()
    {
        var p = Parts;
        var context = this.RequireContext();
        p.Check.Visibility = Visible(_isSelectionMode);
        p.Check.Icon = _isChecked ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle;
        p.Check.SetTextColor(_isChecked ? WinoColors.Accent(context) : WinoColors.SecondaryText(context));
        bool selected = _isRowSelected || (_isSelectionMode && _isChecked);
        _highlightFill.SetColor(selected ? WinoColors.SelectedFill(context) : Color.Transparent);
        p.Pill.Visibility = Visible(selected && !_isSelectionMode);
        Selected = selected;
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoColors.AccentChanged += AccentChanged;
    }

    protected override void OnDetachedFromWindow()
    {
        WinoColors.AccentChanged -= AccentChanged;
        base.OnDetachedFromWindow();
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        ApplyColors();
        ApplySelection();
    }
}

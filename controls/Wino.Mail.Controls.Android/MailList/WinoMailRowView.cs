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
/// accent pill, never the system highlight. Select mode shows the check in place of a checked row's avatar. Unread rows use a medium
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
    // What the views show, so a bind only touches what changed (each view call crosses JNI and many re-layout).
    private WinoMailRowModel? _applied;
    private RowPalette _palette;
    private bool _paletteDirty = true;
    private bool? _shownUnread;
    private (bool Picture, bool CheckAsAvatar, bool SelectionMode, bool Checked, bool Selected, WinoMailRowDensity Density)? _shownSelection;
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
        // Tapping the sender picture selects the row (canvas Mail inbox redesign), as in Material mail apps.
        p.Avatar.Clickable = true;
        p.Avatar.Click += (_, _) => AvatarTapped?.Invoke(this, EventArgs.Empty);
        p.Avatar.ContentDescription = Wino.Core.Domain.Translator.Android_MailList_SelectMessage;
        p.Avatar.LongClick += (_, args) =>
        {
            args.Handled = true;
            AvatarTapped?.Invoke(this, EventArgs.Empty);
        };
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

    /// <summary>The sender picture was tapped.</summary>
    public event EventHandler? AvatarTapped;

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

    /// <summary>Sets the select-mode look in one pass (the adapter's partial rebind).</summary>
    public void SetSelectionState(bool isSelectionMode, bool isChecked, bool isRowSelected)
    {
        _isSelectionMode = isSelectionMode;
        _isChecked = isChecked;
        _isRowSelected = isRowSelected;
        ApplySelection();
    }

    public void Apply(WinoMailRowModel model) => Apply(model, _isSelectionMode, _isChecked, _isRowSelected);

    /// <summary>Shows <paramref name="model"/> with the select-mode look; only the views whose content changed are touched.</summary>
    public void Apply(WinoMailRowModel model, bool isSelectionMode, bool isChecked, bool isRowSelected)
    {
        var p = Parts;
        var old = _applied;
        _model = model;
        _isSelectionMode = isSelectionMode;
        _isChecked = isChecked;
        _isRowSelected = isRowSelected;
        var density = model.Density;
        bool compact = density == WinoMailRowDensity.Compact;

        if (old is null || old.Density != density || old.Kind != model.Kind)
        {
            var context = this.RequireContext();
            SetMinimumHeight(WinoUnits.Dp(context, WinoMailRowSizes.MinHeight(density)));
            int indent = model.Kind == WinoMailRowKind.ThreadChild ? WinoUnits.Dp(context, WinoMailRowMetrics.ChildIndent(density)) : 0;
            int vertical = WinoUnits.Dp(context, WinoMailRowSizes.VerticalPadding(density));
            p.Content.SetPadding(WinoUnits.Dp(context, 14) + indent, vertical, WinoUnits.Dp(context, 16), vertical);
            int highlightStart = WinoUnits.Dp(context, WinoMailRowMetrics.ContentLeading) + indent;
            if (p.Highlight.LayoutParameters is MarginLayoutParams highlight && highlight.MarginStart != highlightStart)
            {
                highlight.MarginStart = highlightStart;
                p.Highlight.LayoutParameters = highlight;
            }
            p.Avatar.Size = WinoMailRowSizes.AvatarSize(density);
        }

        p.Avatar.Image = model.Picture;
        p.Avatar.SetIdentity(string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender,
            string.IsNullOrWhiteSpace(model.SenderAddress) ? model.Sender : model.SenderAddress);

        if (old?.DraftLabel != model.DraftLabel) p.Draft.Text = $"[{model.DraftLabel}]";
        Show(p.Draft, old?.IsDraft, model.IsDraft);
        Text(p.Sender, old is null ? null : SenderText(old), SenderText(model));
        bool threaded = IsThreaded(model);
        if (old?.ThreadCount != model.ThreadCount) p.ThreadCount.Text = model.ThreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Show(p.ThreadCount, old is null ? null : IsThreaded(old), threaded);
        if (old is null || old.ShowNickname != model.ShowNickname || old.NicknamePosition != model.NicknamePosition
            || old.AccountNickname != model.AccountNickname || old.AccountColor != model.AccountColor)
        {
            ApplyNickname(p.NicknameLeft, model, AccountNicknamePosition.Left);
            ApplyNickname(p.NicknameRight, model, AccountNicknamePosition.Right);
        }
        Show(p.Pin, old?.IsPinned, model.IsPinned);
        Show(p.Attachment, old?.HasAttachments, model.HasAttachments);
        Show(p.Flag, old?.IsFlagged, model.IsFlagged);
        Show(p.Busy, old?.IsBusy, model.IsBusy);
        Text(p.Date, old?.DateText, model.DateText);

        Show(p.Chevron, old is null ? null : IsThreaded(old), threaded);
        if (old?.IsThreadExpanded != model.IsThreadExpanded)
        {
            p.Chevron.Icon = model.IsThreadExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
            p.Chevron.ContentDescription = model.IsThreadExpanded ? Wino.Core.Domain.Translator.MacOS_MailList_CollapseThread : Wino.Core.Domain.Translator.MacOS_MailList_ExpandThread;
        }
        Text(p.Subject, old?.Subject, model.Subject);
        Show(p.Unread, old?.IsUnread, model.IsUnread);

        Text(p.Preview, old?.Preview, model.Preview);
        Show(p.Preview, old is null ? null : IsPreviewVisible(old), IsPreviewVisible(model));

        ApplyTiles(model);
        if (old?.AccessibilityText != model.AccessibilityText) ContentDescription = model.AccessibilityText;
        _applied = model;
        ApplyColors();
        ApplySelection();
    }

    private static ViewStates Visible(bool visible) => visible ? ViewStates.Visible : ViewStates.Gone;

    private static void Show(View view, bool? shown, bool visible)
    {
        if (shown != visible) view.Visibility = Visible(visible);
    }

    private static void Text(TextView view, string? shown, string value)
    {
        if (!string.Equals(shown, value, StringComparison.Ordinal)) view.Text = value;
    }

    private static string SenderText(WinoMailRowModel model)
        => model.IsDraft && string.IsNullOrWhiteSpace(model.Sender) ? model.SenderAddress : model.Sender;

    private static bool IsThreaded(WinoMailRowModel model) => model.Kind == WinoMailRowKind.ThreadHead && model.ThreadCount > 1;

    private static bool IsPreviewVisible(WinoMailRowModel model)
        => model.Density != WinoMailRowDensity.Compact && model.ShowPreview && !string.IsNullOrWhiteSpace(model.Preview);

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

    /// <summary>The row colours, resolved from resources once and again after an accent or configuration change.</summary>
    private readonly record struct RowPalette(Color Accent, Color AccentText, Color PrimaryText, Color SecondaryText, Color Caution, Color Flagged, Color SelectedFill);

    private void ApplyColors()
    {
        var p = Parts;
        if (_paletteDirty)
        {
            var context = this.RequireContext();
            _palette = new RowPalette(WinoColors.Accent(context), WinoColors.AccentText(context), WinoColors.PrimaryText(context),
                WinoColors.SecondaryText(context), WinoColors.Caution(context), WinoColors.Flagged(context), WinoColors.SelectedFill(context));
            _paletteDirty = false;
            _shownUnread = null;
            _shownSelection = null;
            _unreadFill.SetColor(_palette.Accent);
            _pillFill.SetColor(_palette.Accent);
            p.Attachment.SetTextColor(_palette.Caution);
            p.Flag.SetTextColor(_palette.Flagged);
            p.Pin.SetTextColor(_palette.SecondaryText);
            p.Busy.SetTextColor(_palette.SecondaryText);
            p.Chevron.SetTextColor(_palette.SecondaryText);
        }
        bool unread = _model.IsUnread;
        if (_shownUnread == unread) return;
        _shownUnread = unread;
        p.Sender.Typeface = unread ? Medium : Regular;
        p.Subject.Typeface = unread ? Medium : Regular;
        p.Subject.SetTextColor(unread ? _palette.AccentText : _palette.PrimaryText);
    }

    private void ApplySelection()
    {
        // Select mode (canvas Material pass InboxDark): with sender pictures a checked row shows the check in place of
        // the avatar and an unchecked row keeps its avatar; without pictures the round check stands before the text.
        bool picture = _model.ShowPicture;
        bool checkAsAvatar = _isSelectionMode && picture && _isChecked;
        bool selected = _isRowSelected || (_isSelectionMode && _isChecked);
        var state = (picture, checkAsAvatar, _isSelectionMode, _isChecked, selected, _model.Density);
        if (_shownSelection == state) return;
        _shownSelection = state;

        var p = Parts;
        var context = this.RequireContext();
        p.Avatar.Visibility = Visible(picture && !checkAsAvatar);
        p.Check.Visibility = Visible(_isSelectionMode && (!picture || _isChecked));
        if (p.Check.LayoutParameters is MarginLayoutParams check)
        {
            int size = WinoUnits.Dp(context, checkAsAvatar ? WinoMailRowSizes.AvatarSize(_model.Density) : WinoMailRowSizes.SelectCheckSize);
            int gap = WinoUnits.Dp(context, checkAsAvatar ? WinoMailRowSizes.AvatarGap : WinoMailRowSizes.SelectCheckGap);
            if (check.Width != size || check.MarginEnd != gap)
            {
                check.Width = size;
                check.Height = size;
                check.MarginEnd = gap;
                p.Check.LayoutParameters = check;
            }
        }
        p.Check.GlyphSize = checkAsAvatar ? WinoMailRowSizes.AvatarSize(_model.Density) : WinoMailRowSizes.SelectCheckGlyph;
        p.Check.Icon = _isChecked ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle;
        p.Check.ContentDescription = _isChecked ? Wino.Core.Domain.Translator.Android_MailList_DeselectMessage : Wino.Core.Domain.Translator.Android_MailList_SelectMessage;
        p.Check.SetTextColor(_isChecked ? _palette.Accent : _palette.SecondaryText);
        _highlightFill.SetColor(selected ? _palette.SelectedFill : Color.Transparent);
        p.Pill.Visibility = Visible(selected && !_isSelectionMode);
        Selected = selected;
    }

    protected override void OnConfigurationChanged(Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        RefreshAppearance();
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

    private void AccentChanged(object? sender, EventArgs args) => RefreshAppearance();

    /// <summary>Re-resolves the colours and re-applies the current model in full (accent, theme or night mode changed).</summary>
    private void RefreshAppearance()
    {
        _paletteDirty = true;
        _applied = null;
        Apply(_model);
    }
}

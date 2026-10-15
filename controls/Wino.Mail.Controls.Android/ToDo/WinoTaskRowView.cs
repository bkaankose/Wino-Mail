using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.ToDo;

/// <summary>
/// A task in the To Do list (UIKit <c>WinoTaskRowCell</c>, AppKit <c>WinoTaskCardView</c>; canvas Material pass ToDo):
/// the round check, the title (body-large; struck through and dimmed when completed) over the supporting line (list in
/// its colour, due text in red when overdue, My Day, steps, a note glyph), and the importance star button. The check
/// and the star raise <see cref="CompletionToggled"/> and <see cref="ImportanceToggled"/>; the row itself is clickable.
/// Sizes come from the wino_tasks_* tokens; content from the shared <see cref="WinoTaskRowModel"/>.
/// </summary>
public class WinoTaskRowView : LinearLayout
{
    private readonly WinoRoundCheckbox _check;
    private readonly TextView _title;
    private readonly TextView _meta;
    private readonly WinoIconView _star;
    private WinoTaskRowModel _model = new();
    private bool _isRowSelected;

    public WinoTaskRowView(Context context) : base(context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_min_height));
        // The check's 48 dp touch target overlaps the row padding, so the drawn circle sits 16 dp in, as drawn.
        int touchInset = (WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large) - WinoTokens.Px(context, Resource.Dimension.wino_tasks_check)) / 2;
        SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_padding_horizontal) - touchInset,
            WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_padding_vertical),
            WinoTokens.Px(context, Resource.Dimension.wino_space_s),
            WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_padding_vertical));

        _check = new WinoRoundCheckbox(context, Resource.Dimension.wino_tasks_check);
        _check.Toggled += (_, _) => CompletionToggled?.Invoke(this, EventArgs.Empty);
        AddView(_check, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var text = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _title = WinoTokens.Style(new TextView(context), WinoTextStyle.ItemTitle);
        _title.SetTextSize(global::Android.Util.ComplexUnitType.Px, WinoTokens.PxF(context, Resource.Dimension.wino_tasks_title_text));
        _title.SetMaxLines(2);
        _title.Ellipsize = TextUtils.TruncateAt.End;
        _meta = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
        _meta.SetTextSize(global::Android.Util.ComplexUnitType.Px, WinoTokens.PxF(context, Resource.Dimension.wino_tasks_meta_text));
        _meta.SetMaxLines(1);
        _meta.Ellipsize = TextUtils.TruncateAt.End;
        text.AddView(_title);
        text.AddView(_meta);
        AddView(text, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
        {
            MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_gap) - touchInset
        });

        _star = new WinoIconView(context)
        {
            GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_tasks_star_glyph),
            Clickable = true,
            Focusable = true,
            ImportantForAccessibility = ImportantForAccessibility.Yes
        };
        _star.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, Attribute(context, global::Android.Resource.Attribute.SelectableItemBackgroundBorderless));
        _star.Click += (_, _) => { if (_model.IsEditable) ImportanceToggled?.Invoke(this, EventArgs.Empty); };
        int star = WinoTokens.Px(context, Resource.Dimension.wino_tasks_star_button);
        AddView(_star, new LayoutParams(star, star));

        Clickable = true;
        Focusable = true;
        Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, Attribute(context, global::Android.Resource.Attribute.SelectableItemBackground));
    }

    /// <summary>The round check was tapped.</summary>
    public event EventHandler? CompletionToggled;

    /// <summary>The star was tapped.</summary>
    public event EventHandler? ImportanceToggled;

    private static int Attribute(Context context, int attribute)
    {
        var value = new global::Android.Util.TypedValue();
        context.Theme?.ResolveAttribute(attribute, value, true);
        return value.ResourceId;
    }

    public void Apply(WinoTaskRowModel model)
    {
        _model = model;
        var context = this.RequireContext();
        _check.Checked = model.IsCompleted;
        _check.Enabled = model.IsEditable;
        _check.Label = model.CompletionActionText;

        _title.Text = model.Title;
        _title.SetTextColor(model.IsCompleted ? WinoColors.TertiaryText(context) : WinoColors.PrimaryText(context));
        _title.PaintFlags = model.IsCompleted ? _title.PaintFlags | PaintFlags.StrikeThruText : _title.PaintFlags & ~PaintFlags.StrikeThruText;

        var meta = new SpannableStringBuilder();
        void Segment(string text, Color? color)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (meta.Length() > 0) meta.Append(" · ");
            int start = meta.Length();
            meta.Append(text);
            if (color is { } tint) meta.SetSpan(new global::Android.Text.Style.ForegroundColorSpan(tint), start, meta.Length(), SpanTypes.ExclusiveExclusive);
        }
        Segment(model.ListName ?? string.Empty, WinoColors.FromHexString(model.ListColorHex));
        Segment(model.DueText, model.IsOverdue ? WinoColors.Critical(context) : null);
        if (model.IsInMyDay) Segment(model.MyDayText, null);
        Segment(model.StepSummary, null);
        if (model.HasNote)
        {
            if (meta.Length() > 0) meta.Append(" · ");
            meta.Append(WinoIcons.WithGlyph(WinoIconGlyph.Note, string.Empty, WinoIcons.Mono(context)));
        }
        _meta.TextFormatted = meta;
        _meta.Visibility = meta.Length() == 0 ? ViewStates.Gone : ViewStates.Visible;

        _star.Icon = model.IsImportant ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star;
        _star.SetTextColor(model.IsImportant ? WinoColors.AccentText(context) : WinoColors.SecondaryText(context));
        _star.ContentDescription = model.ImportanceActionText;
        _star.Visibility = model.IsEditable || model.IsImportant ? ViewStates.Visible : ViewStates.Invisible;
        ContentDescription = meta.Length() == 0 ? model.Title : $"{model.Title}, {meta}";
    }

    /// <summary>The open task (list beside the detail): the Wino selection fill.</summary>
    public bool IsRowSelected
    {
        get => _isRowSelected;
        set
        {
            if (_isRowSelected == value) return;
            _isRowSelected = value;
            Selected = value;
            var context = this.RequireContext();
            if (value) SetBackgroundColor(WinoColors.SelectedFill(context));
            else Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, Attribute(context, global::Android.Resource.Attribute.SelectableItemBackground));
        }
    }
}

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.Accessibility;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.ToDo;

/// <summary>
/// The round Wino task check (UIKit and AppKit <c>WinoRoundCheckbox</c>; canvas Material pass ToDo): a 2 dp outline
/// circle, filled with the accent and a white check mark while checked. A tap raises <see cref="Toggled"/>; the owner
/// changes <see cref="Checked"/> when the ViewModel confirms. TalkBack reads it as a checkbox with <see cref="Label"/>.
/// </summary>
public class WinoRoundCheckbox : View
{
    private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeWidth = 0 };
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _glyph = new(PaintFlags.AntiAlias) { TextAlign = Paint.Align.Center };
    private readonly int _sizeResource;
    private bool _checked;

    public WinoRoundCheckbox(Context context, int sizeResource) : base(context)
    {
        _sizeResource = sizeResource;
        _stroke.SetStyle(Paint.Style.Stroke);
        _stroke.StrokeWidth = WinoTokens.PxF(context, Resource.Dimension.wino_tasks_check_stroke);
        _glyph.SetTypeface(WinoIcons.Mono(context));
        Clickable = true;
        Focusable = true;
        Click += (_, _) => { if (Enabled) Toggled?.Invoke(this, EventArgs.Empty); };
        Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, BorderlessRipple(context));
    }

    /// <summary>The check was tapped.</summary>
    public event EventHandler? Toggled;

    /// <summary>What TalkBack reads (the completion action text).</summary>
    public string? Label
    {
        get => ContentDescription;
        set => ContentDescription = value;
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            SendAccessibilityEvent(EventTypes.ViewSelected);
        }
    }

    private static int BorderlessRipple(Context context)
    {
        var value = new global::Android.Util.TypedValue();
        context.Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackgroundBorderless, value, true);
        return value.ResourceId;
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var context = this.RequireContext();
        // A 48 dp touch target around the drawn circle.
        int size = Math.Max(WinoTokens.Px(context, _sizeResource), WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large));
        SetMeasuredDimension(ResolveSize(size, widthMeasureSpec), ResolveSize(size, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var context = this.RequireContext();
        float diameter = WinoTokens.PxF(context, _sizeResource);
        float cx = Width / 2f, cy = Height / 2f, radius = diameter / 2f - _stroke.StrokeWidth / 2f;
        var accent = WinoColors.Accent(context);
        if (_checked)
        {
            _fill.Color = accent;
            canvas.DrawCircle(cx, cy, diameter / 2f, _fill);
            _glyph.Color = WinoColors.OnAccent(context);
            _glyph.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_tasks_check_glyph) * diameter / WinoTokens.PxF(context, Resource.Dimension.wino_tasks_check);
            float baseline = cy - (_glyph.Descent() + _glyph.Ascent()) / 2f;
            canvas.DrawText(WinoIcons.Glyph(WinoIconGlyph.Checkmark), cx, baseline, _glyph);
            return;
        }
        _stroke.Color = Enabled ? WinoColors.SecondaryText(context) : WinoColors.Outline(context);
        canvas.DrawCircle(cx, cy, radius, _stroke);
    }

    public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? info)
    {
        base.OnInitializeAccessibilityNodeInfo(info);
        if (info is null) return;
        var compat = AndroidX.Core.View.Accessibility.AccessibilityNodeInfoCompat.Wrap(info);
        compat.ClassName = "android.widget.CheckBox";
        compat.Checkable = true;
        compat.Checked = _checked ? 1 : 0;
    }
}

using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// The Wino fast scroller for indexed lists (canvas Material pass Contacts; the UIKit table section index): the section
/// letters stacked down the end edge, centred in the available height. Touching or dragging over it raises
/// <see cref="SectionSelected"/> with the section under the finger, and a bubble shows the letter beside it while the
/// finger is down. Letters are label text in on-surface-variant, the active one in the accent. Sizes come from the
/// wino_people_index_* tokens. TalkBack reads it as an adjustable control: volume keys or swipes move by one section.
/// </summary>
public class WinoIndexScroller : View
{
    private readonly Paint _text = new(PaintFlags.AntiAlias);
    private readonly Paint _bubble = new(PaintFlags.AntiAlias);
    private readonly Paint _bubbleText = new(PaintFlags.AntiAlias);
    private IReadOnlyList<string> _sections = [];
    private int _active = -1;
    private bool _touching;

    public WinoIndexScroller(Context context) : base(context) => Initialize(context);
    public WinoIndexScroller(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);

    private void Initialize(Context context)
    {
        _text.TextAlign = Paint.Align.Center;
        _text.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_people_index_text);
        _text.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _bubbleText.TextAlign = Paint.Align.Center;
        _bubbleText.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_people_index_bubble_text);
        _bubbleText.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        Focusable = true;
    }

    /// <summary>A section was chosen by touch or accessibility (index into <see cref="Sections"/>).</summary>
    public event EventHandler<int>? SectionSelected;

    /// <summary>The section titles, top to bottom.</summary>
    public IReadOnlyList<string> Sections
    {
        get => _sections;
        set
        {
            _sections = value ?? [];
            if (_active >= _sections.Count) _active = -1;
            Visibility = _sections.Count > 1 ? ViewStates.Visible : ViewStates.Gone;
            Invalidate();
        }
    }

    /// <summary>The section at the top of the list (drawn in the accent).</summary>
    public int ActiveSection
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var context = this.RequireContext();
        int width = WinoTokens.Px(context, Resource.Dimension.wino_people_index_width);
        // The bubble draws to the start of the letters; it needs room inside the view while touching.
        SetMeasuredDimension(ResolveSize(width, widthMeasureSpec), MeasureSpec.GetSize(heightMeasureSpec));
    }

    private float LineHeight => _text.TextSize * 1.2f;

    private float TopOffset => Math.Max(PaddingTop, (Height - LineHeight * _sections.Count) / 2f);

    private int SectionAt(float y)
    {
        if (_sections.Count == 0) return -1;
        int index = (int)Math.Floor((y - TopOffset) / LineHeight);
        return Math.Clamp(index, 0, _sections.Count - 1);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_sections.Count == 0) return;
        var context = this.RequireContext();
        float x = Width - WinoTokens.PxF(context, Resource.Dimension.wino_people_index_width) / 2f;
        float top = TopOffset;
        var secondary = WinoColors.SecondaryText(context);
        var accent = WinoColors.AccentText(context);
        for (int index = 0; index < _sections.Count; index++)
        {
            _text.Color = index == _active ? accent : secondary;
            float baseline = top + LineHeight * index + (LineHeight + _text.TextSize) / 2f - _text.Descent() / 2f;
            canvas.DrawText(_sections[index], x, baseline, _text);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || _sections.Count == 0) return base.OnTouchEvent(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.Move:
                Parent?.RequestDisallowInterceptTouchEvent(true);
                _touching = true;
                Choose(SectionAt(e.GetY()));
                ShowBubble(true);
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _touching = false;
                ShowBubble(false);
                if (e.ActionMasked == MotionEventActions.Up) PerformClick();
                return true;
        }
        return base.OnTouchEvent(e);
    }

    public override bool PerformClick() => base.PerformClick();

    private void Choose(int index)
    {
        if (index < 0 || index >= _sections.Count) return;
        bool changed = index != _active;
        ActiveSection = index;
        if (changed || _touching)
        {
            SectionSelected?.Invoke(this, index);
            if (changed) PerformHapticFeedback(FeedbackConstants.ClockTick);
        }
    }

    // ---- The letter bubble (an overlay on the parent while touching) ----

    private BubbleDrawable? _overlay;

    private void ShowBubble(bool show)
    {
        if (Parent is not ViewGroup parent) return;
        if (!show || _active < 0)
        {
            if (_overlay is not null) parent.Overlay.Remove(_overlay);
            _overlay = null;
            return;
        }
        var context = this.RequireContext();
        int size = WinoTokens.Px(context, Resource.Dimension.wino_people_index_bubble);
        if (_overlay is null)
        {
            _overlay = new BubbleDrawable(this);
            parent.Overlay.Add(_overlay);
        }
        int right = Left - WinoTokens.Px(context, Resource.Dimension.wino_space_s);
        int centre = Top + (int)(TopOffset + LineHeight * (_active + 0.5f));
        int top = Math.Clamp(centre - size / 2, 0, Math.Max(0, parent.Height - size));
        _overlay.SetBounds(right - size, top, right, top + size);
        _overlay.InvalidateSelf();
    }

    private sealed class BubbleDrawable(WinoIndexScroller owner) : global::Android.Graphics.Drawables.Drawable
    {
        public override void Draw(Canvas canvas)
        {
            if (owner._active < 0 || owner._active >= owner._sections.Count || owner.Context is not { } context) return;
            var bounds = Bounds;
            owner._bubble.Color = WinoColors.PrimaryContainer(context);
            canvas.DrawCircle(bounds.ExactCenterX(), bounds.ExactCenterY(), bounds.Width() / 2f, owner._bubble);
            owner._bubbleText.Color = WinoColors.OnPrimaryContainer(context);
            float baseline = bounds.ExactCenterY() - (owner._bubbleText.Descent() + owner._bubbleText.Ascent()) / 2f;
            canvas.DrawText(owner._sections[owner._active], bounds.ExactCenterX(), baseline, owner._bubbleText);
        }

        public override void SetAlpha(int alpha) { }
        public override void SetColorFilter(ColorFilter? colorFilter) { }
        public override int Opacity => (int)Format.Translucent;
    }

    // ---- Accessibility: an adjustable control ----

    public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? info)
    {
        base.OnInitializeAccessibilityNodeInfo(info);
        if (info is null) return;
        info.ClassName = "android.widget.SeekBar";
        if (_active >= 0 && _active < _sections.Count) info.Text = _sections[_active];
        info.AddAction(AccessibilityNodeInfo.AccessibilityAction.ActionScrollForward);
        info.AddAction(AccessibilityNodeInfo.AccessibilityAction.ActionScrollBackward);
    }

    public override bool PerformAccessibilityAction(global::Android.Views.Accessibility.Action action, global::Android.OS.Bundle? arguments)
    {
        if (action == global::Android.Views.Accessibility.Action.ScrollForward) { Choose(Math.Min(_sections.Count - 1, _active + 1)); return true; }
        if (action == global::Android.Views.Accessibility.Action.ScrollBackward) { Choose(Math.Max(0, _active - 1)); return true; }
        return base.PerformAccessibilityAction(action, arguments);
    }
}

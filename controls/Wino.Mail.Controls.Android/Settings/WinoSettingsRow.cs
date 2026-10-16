using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// One settings row (canvas Android SettingsControls "Row kinds"): a Material one-line list item, 56 dp, padding 16 / 12,
/// a 16 sp title, then a 14 sp trailing value (at most 45 % of the row), the inline switch, or a 12 dp chevron. Rows of
/// list items (an account, a category) keep their picture and supporting text. A row follows its
/// <see cref="WinoSettingsCard"/> or <see cref="WinoSettingsExpander"/> (title, value, visibility, enabled state) while it
/// is attached: the sources are descriptions whose controls live with the page's bindings.
/// </summary>
public sealed class WinoSettingsRow : LinearLayout
{
    private readonly FrameLayout _leading;
    private readonly TextView _title;
    private readonly TextView _supporting;
    private readonly TextView _value;
    private readonly FrameLayout _trailing;
    private readonly WinoIconView _chevron;
    private readonly PreDrawListener _preDraw;
    private readonly List<(TextView View, TextWatcher Watcher)> _watched = [];
    private View? _source;
    private bool _inDetail;
    private Action? _tap;
    private int _valueMax = -1;
    private int _titleMode = -1;
    private const int LongValue = 28;
    private bool _syncing;

    public WinoSettingsRow(Context context) : base(context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_row_height));
        WinoTokens.Pad(this, Resource.Dimension.wino_m3_settings_row_padding_start, Resource.Dimension.wino_m3_settings_row_padding_vertical,
            Resource.Dimension.wino_m3_settings_row_padding_end, Resource.Dimension.wino_m3_settings_row_padding_vertical);
        int gap = WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_row_gap);

        _leading = new FrameLayout(context) { Visibility = ViewStates.Gone };
        AddView(_leading, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = gap });

        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _title = WinoTokens.Style(new TextView(context), WinoTextStyle.SettingsRowTitle);
        _title.SetMaxLines(2);
        _title.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(_title);
        _supporting = WinoTokens.Style(new TextView(context), WinoTextStyle.SettingsRowValue);
        _supporting.SetMaxLines(2);
        _supporting.Ellipsize = TextUtils.TruncateAt.End;
        _supporting.Visibility = ViewStates.Gone;
        texts.AddView(_supporting);
        AddView(texts, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));

        _value = WinoTokens.Style(new TextView(context), WinoTextStyle.SettingsRowValue);
        _value.SetSingleLine(true);
        _value.Ellipsize = TextUtils.TruncateAt.End;
        _value.Visibility = ViewStates.Gone;
        AddView(_value, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = gap });

        _trailing = new FrameLayout(context) { Visibility = ViewStates.Gone };
        AddView(_trailing, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = gap });

        _chevron = new WinoIconView(context)
        {
            Icon = WinoIconGlyph.ChevronRight,
            GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_settings_chevron),
            Visibility = ViewStates.Gone,
            ImportantForAccessibility = ImportantForAccessibility.No
        };
        _chevron.Gravity = GravityFlags.Center;
        AddView(_chevron, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = gap / 2 });

        _preDraw = new PreDrawListener(Sync);
        Click += (_, _) =>
        {
            if (!Enabled) return;
            try { _tap?.Invoke(); }
            catch (Exception exception) { global::Android.Util.Log.Warn("Wino", $"A settings row action failed. {exception}"); }
        };
        ApplyColors();
    }

    /// <summary>The row of a card or expander on a settings page.</summary>
    public static WinoSettingsRow ForPage(Context context, View source)
    {
        var row = new WinoSettingsRow(context) { _source = source };
        row.Sync();
        return row;
    }

    /// <summary>The row of an item on the detail page (switches, actions, navigation, nested expanders, read-only values).</summary>
    public static WinoSettingsRow ForDetail(Context context, View source)
    {
        var row = new WinoSettingsRow(context) { _source = source, _inDetail = true };
        row.Sync();
        return row;
    }

    /// <summary>A row built by the detail page itself (a stepper, a time, an action) that follows <paramref name="follow"/>.</summary>
    public static WinoSettingsRow Custom(Context context, string title, View? trailing, Action? tap, View? follow = null, Func<string?>? value = null, bool action = false, bool destructive = false)
    {
        var row = new WinoSettingsRow(context) { _inDetail = true };
        row._custom = new CustomRow(title, trailing, tap, follow, value, action, destructive);
        row.Sync();
        return row;
    }

    private sealed record CustomRow(string Title, View? Trailing, Action? Tap, View? Follow, Func<string?>? Value, bool Action, bool Destructive);

    private CustomRow? _custom;

    /// <summary>Hosts a view that is not a row (a preview, a list) with the row insets inside a section card.</summary>
    public static View Embed(Context context, View content, bool padded = true) => new WinoSettingsEmbed(context, content, padded);

    /// <summary>The card or expander this row shows.</summary>
    public View? Source => _source;

    /// <summary>The 18 sp lines of footers (TextAppearance lineHeight needs a MaterialTextView).</summary>
    public static void ApplyFooterLineHeight(TextView view)
    {
        if (view.Context is { } context)
            TextViewCompat.SetLineHeight(view, WinoTokens.Px(context, Resource.Dimension.wino_settings_footer_line));
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        ViewTreeObserver?.AddOnPreDrawListener(_preDraw);
        Subscribe(true);
        ApplyColors();
        Sync();
    }

    protected override void OnDetachedFromWindow()
    {
        ViewTreeObserver?.RemoveOnPreDrawListener(_preDraw);
        Subscribe(false);
        base.OnDetachedFromWindow();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        if (width > 0 && Context is { } context)
        {
            int max = (int)(width * WinoColors.Fraction(context, Resource.Fraction.wino_m3_settings_value_max));
            if (max != _valueMax)
            {
                _valueMax = max;
                _value.SetMaxWidth(max);
            }
        }
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }

    private void Subscribe(bool on)
    {
        switch (_source)
        {
            case WinoSettingsCard card:
                if (on) card.Changed += SourceChanged; else card.Changed -= SourceChanged;
                break;
            case WinoSettingsExpander expander:
                if (on) expander.Changed += SourceChanged; else expander.Changed -= SourceChanged;
                break;
        }
        foreach (var (view, watcher) in _watched) view.RemoveTextChangedListener(watcher);
        _watched.Clear();
        if (!on) return;
        foreach (var control in ValueControls())
            foreach (var text in TextViews(control))
            {
                var watcher = new TextWatcher(() => Post(Sync));
                text.AddTextChangedListener(watcher);
                _watched.Add((text, watcher));
            }
    }

    private void SourceChanged(object? sender, EventArgs args)
    {
        Sync();
        if (IsAttachedToWindow)
        {
            // The controls may have changed: watch the new ones.
            Subscribe(false);
            Subscribe(true);
        }
    }

    private IEnumerable<View> ValueControls()
    {
        var card = Card;
        if (card is null) yield break;
        if (card.Content is { } content) yield return content;
        if (card.BottomContent is { } bottom) yield return bottom;
        if (_source is WinoSettingsExpander expander)
            foreach (var item in expander.Items)
                if (item is WinoSettingsCard child)
                {
                    if (child.Content is { } c) yield return c;
                    if (child.BottomContent is { } b) yield return b;
                }
    }

    private static IEnumerable<TextView> TextViews(View view)
    {
        switch (view)
        {
            case TextView text:
                yield return text;
                break;
            case ViewGroup group:
                for (int index = 0; index < group.ChildCount && index < 8; index++)
                    if (group.GetChildAt(index) is { } child)
                        foreach (var nested in TextViews(child)) yield return nested;
                break;
        }
    }

    private WinoSettingsCard? Card => _source switch
    {
        WinoSettingsCard card => card,
        WinoSettingsExpander expander => expander.HeaderCard,
        _ => null
    };

    private void ApplyColors()
    {
        var context = this.RequireContext();
        _titleMode = -1;
        _chevron.SetTextColor(WinoTokens.Color(context, Resource.Color.wino_settings_chevron));
        var mask = new ColorDrawable(Color.White);
        Foreground = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, mask);
    }

    /// <summary>Reads the source again: kind, title, value, trailing control, visibility and enabled state.</summary>
    public void Sync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (_custom is { } custom) SyncCustom(custom);
            else if (Card is { } card) SyncSource(card);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("Wino", $"A settings row could not read its source. {exception}");
        }
        finally { _syncing = false; }
    }

    private void SyncCustom(CustomRow custom)
    {
        SetTitle(custom.Title, custom.Action, custom.Destructive);
        SetValue(custom.Value?.Invoke());
        Host(_trailing, custom.Trailing);
        SetChevron(false);
        _tap = custom.Tap;
        bool visible = custom.Follow is null || custom.Follow.Visibility == ViewStates.Visible;
        bool enabled = custom.Follow is null || custom.Follow.Enabled;
        if (custom.Trailing is not null && !custom.Trailing.Enabled) enabled = enabled && custom.Action;
        ApplyState(visible, enabled, custom.Tap is not null);
        ContentDescription = Describe(custom.Title, _value.Text);
    }

    private void SyncSource(WinoSettingsCard card)
    {
        bool isExpander = _source is WinoSettingsExpander;
        var kind = isExpander ? WinoSettingsRowKind.Push : WinoSettingsRules.Classify(card);
        bool visible = (_source?.Visibility ?? ViewStates.Visible) == ViewStates.Visible;
        bool enabled = card.Enabled;

        // A list item keeps its picture and supporting text.
        bool entity = card.LeadingView is not null;
        Host(_leading, card.LeadingView);
        var supporting = entity ? card.Description ?? string.Empty : string.Empty;
        // Read-only text too long for the trailing value (an address, a source) goes under the title.
        if (!entity && WinoSettingsRules.Classify(card) == WinoSettingsRowKind.Info
            && WinoSettingsControls.ValueText(card.Content ?? card.BottomContent) is { Length: > LongValue } longText)
            supporting = longText;
        if (_supporting.Text != supporting) _supporting.Text = supporting;
        var supportingVisibility = supporting.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        if (_supporting.Visibility != supportingVisibility) _supporting.Visibility = supportingVisibility;

        string title = card.Header;
        View? trailing = null;
        string? value = null;
        bool chevron = false;
        Action? tap = null;
        bool action = false, destructive = false;

        switch (kind)
        {
            case WinoSettingsRowKind.Switch:
                trailing = card.Content ?? card.BottomContent;
                var toggle = WinoSettingsControls.ToggleOf(trailing);
                tap = toggle is null ? null : () => { if (toggle.Enabled) toggle.PerformClick(); };
                break;
            case WinoSettingsRowKind.Value:
                value = WinoSettingsRules.ValueOf(card);
                chevron = true;
                tap = () => WinoSettingsNavigation.Open(this, _source);
                break;
            case WinoSettingsRowKind.Push:
                value = isExpander && _source is WinoSettingsExpander expander ? WinoSettingsRules.ValueOf(expander) : WinoSettingsRules.PushValueOf(card);
                chevron = true;
                tap = () => WinoSettingsNavigation.Open(this, _source);
                break;
            case WinoSettingsRowKind.Navigate:
                var contentKind = WinoSettingsControls.KindOf(card.Content);
                // A switch (Signature on, and the page behind it) or a list item's glyph buttons stay on the row.
                if (contentKind is WinoSettingsControlKind.Toggle or WinoSettingsControlKind.IconButtons) trailing = card.Content;
                else if (!entity) value = WinoSettingsControls.FirstText(card.Content);
                else if (card.Content is { } status) trailing = status;
                chevron = card.ActionIcon == WinoIconGlyph.None;
                tap = card.Activate;
                break;
            case WinoSettingsRowKind.Action:
                var button = WinoSettingsControls.ButtonsOf(card.Content ?? card.BottomContent).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(title) && button is not null) title = button.Text ?? string.Empty;
                action = true;
                destructive = card.IsDestructive || (button is not null && WinoSettingsControls.IsDestructive(button));
                if (button is not null) enabled = enabled && button.Enabled;
                tap = button is not null ? () => button.PerformClick() : card.IsClickable ? card.Activate : null;
                break;
            case WinoSettingsRowKind.Info:
                value = WinoSettingsControls.ValueText(card.Content ?? card.BottomContent);
                if (value is { Length: > LongValue }) value = null;
                break;
            default:
                trailing = card.Content ?? card.BottomContent;
                break;
        }

        // Expanders and cards that open something in the detail list show the same chevron.
        if (_inDetail && kind == WinoSettingsRowKind.Push) chevron = true;
        var glyph = card.ActionIcon != WinoIconGlyph.None && kind == WinoSettingsRowKind.Navigate ? card.ActionIcon : WinoIconGlyph.ChevronRight;
        if (_chevron.Icon != glyph) _chevron.Icon = glyph;
        if (card.ActionIcon != WinoIconGlyph.None && kind == WinoSettingsRowKind.Navigate) chevron = true;

        SetTitle(title, action, destructive);
        SetValue(value);
        Host(_trailing, trailing);
        SetChevron(chevron);
        _tap = tap;
        // A row without a title or anything to show (an empty status line) is not drawn.
        if (string.IsNullOrWhiteSpace(title) && trailing is null && value is null && supporting.Length == 0) visible = false;
        ApplyState(visible, enabled, tap is not null);
        var description = card.ContentDescription;
        ContentDescription = string.IsNullOrWhiteSpace(description) ? Describe(title, _value.Text) : description;
    }

    private static string Describe(string title, string? value) => string.IsNullOrWhiteSpace(value) ? title : $"{title}, {value}";

    private void SetChevron(bool shown)
    {
        var visibility = shown ? ViewStates.Visible : ViewStates.Gone;
        if (_chevron.Visibility != visibility) _chevron.Visibility = visibility;
    }

    private void SetTitle(string title, bool action, bool destructive)
    {
        var context = this.RequireContext();
        if (_title.Text != title) _title.Text = title;
        int mode = action ? (destructive ? 2 : 1) : 0;
        if (mode == _titleMode) return;
        _titleMode = mode;
        if (action)
        {
            _title.SetTextColor(destructive ? WinoTokens.Color(context, Resource.Color.wino_settings_destructive) : WinoColors.AccentText(context));
            _title.Typeface = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal);
        }
        else
        {
            _title.SetTextColor(WinoColors.PrimaryText(context));
            _title.Typeface = Typeface.Default;
        }
    }

    private void SetValue(string? value)
    {
        if (_value.Text != (value ?? string.Empty)) _value.Text = value ?? string.Empty;
        var visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        if (_value.Visibility != visibility) _value.Visibility = visibility;
    }

    private void ApplyState(bool visible, bool enabled, bool clickable)
    {
        var visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (Visibility != visibility)
        {
            Visibility = visibility;
            (Parent?.Parent as WinoSettingsGroup)?.QueueUpdateEmpty();
        }
        if (base.Enabled != enabled) base.Enabled = enabled;
        float alpha = enabled ? 1f : WinoColors.Fraction(this.RequireContext(), Resource.Fraction.wino_m3_settings_disabled_alpha);
        if (Math.Abs(Alpha - alpha) > 0.001f) Alpha = alpha;
        if (Clickable != clickable)
        {
            Clickable = clickable;
            Focusable = clickable;
        }
    }

    /// <summary>Moves <paramref name="view"/> into <paramref name="host"/> (or empties it), keeping a fixed size the view brought.</summary>
    private static void Host(FrameLayout host, View? view)
    {
        var current = host.ChildCount > 0 ? host.GetChildAt(0) : null;
        if (ReferenceEquals(current, view) && view?.Parent == host) return;
        host.RemoveAllViews();
        host.Visibility = view is null ? ViewStates.Gone : ViewStates.Visible;
        if (view is null) return;
        (view.Parent as ViewGroup)?.RemoveView(view);
        int width = view.LayoutParameters?.Width is > 0 and var w ? w : ViewGroup.LayoutParams.WrapContent;
        int height = view.LayoutParameters?.Height is > 0 and var h ? h : ViewGroup.LayoutParams.WrapContent;
        host.AddView(view, new FrameLayout.LayoutParams(width, height, GravityFlags.CenterVertical | GravityFlags.End));
    }

    private sealed class PreDrawListener(Action sync) : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        public bool OnPreDraw()
        {
            sync();
            return true;
        }
    }

    private sealed class TextWatcher(Action changed) : Java.Lang.Object, ITextWatcher
    {
        public void AfterTextChanged(IEditable? s) => changed();
        public void BeforeTextChanged(Java.Lang.ICharSequence? s, int start, int count, int after) { }
        public void OnTextChanged(Java.Lang.ICharSequence? s, int start, int before, int count) { }
    }
}

/// <summary>A view that is not a row, hosted inside a section card with the row insets; it hides with its content.</summary>
internal sealed class WinoSettingsEmbed : FrameLayout
{
    private readonly View? _content;
    private readonly Listener? _listener;

    public WinoSettingsEmbed(Context context, View content, bool padded) : base(context)
    {
        _content = content;
        if (padded) WinoTokens.Pad(this, Resource.Dimension.wino_m3_settings_content_padding, Resource.Dimension.wino_m3_settings_content_padding_vertical,
            Resource.Dimension.wino_m3_settings_content_padding, Resource.Dimension.wino_m3_settings_content_padding_vertical);
        (content.Parent as ViewGroup)?.RemoveView(content);
        AddView(content, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _listener = new Listener(Sync);
    }

    private WinoSettingsEmbed(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (_listener is not null) ViewTreeObserver?.AddOnPreDrawListener(_listener);
        Sync();
    }

    protected override void OnDetachedFromWindow()
    {
        if (_listener is not null) ViewTreeObserver?.RemoveOnPreDrawListener(_listener);
        base.OnDetachedFromWindow();
    }

    private void Sync()
    {
        if (_content is null) return;
        var visibility = _content.Visibility == ViewStates.Visible ? ViewStates.Visible : ViewStates.Gone;
        if (Visibility == visibility) return;
        Visibility = visibility;
        (Parent?.Parent as WinoSettingsGroup)?.QueueUpdateEmpty();
    }

    private sealed class Listener(Action sync) : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        public bool OnPreDraw()
        {
            sync();
            return true;
        }
    }
}

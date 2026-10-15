using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Dialogs;

/// <summary>
/// A small rich text editor for signatures and email templates (iOS WinoRichTextEditorView, which uses the WebKit mail
/// editor): a formatting row (bold, italic, underline, clear formatting) over a multi-line field. The text is HTML in and
/// out through the platform's HTML conversion, which keeps paragraphs, line breaks, bold, italic, underline, links and
/// colours. The WebView mail editor session (compose) can replace it once it exists for Android.
/// </summary>
public sealed class WinoHtmlTextEditor : LinearLayout
{
    private readonly EditText _field;

    /// <param name="minimumHeight">The minimum height dimension token of the field (0: wino_editor_min_height).</param>
    public WinoHtmlTextEditor(Context context, string accessibilityLabel, int minimumHeight = 0) : base(context)
    {
        Orientation = Orientation.Vertical;
        var tools = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        tools.AddView(Tool(context, WinoIconGlyph.TextBold, Translator.Composer_Bold, () => ToggleStyle(TypefaceStyle.Bold)));
        tools.AddView(Tool(context, WinoIconGlyph.TextItalic, Translator.Composer_Italic, () => ToggleStyle(TypefaceStyle.Italic)));
        tools.AddView(Tool(context, WinoIconGlyph.TextUnderline, Translator.Composer_Underline, ToggleUnderline));
        tools.AddView(Tool(context, WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, ClearFormatting));
        AddView(tools);

        _field = new EditText(context)
        {
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            Gravity = GravityFlags.Top | GravityFlags.Start,
            ContentDescription = accessibilityLabel
        };
        _field.SetMinHeight(WinoTokens.Px(context, minimumHeight == 0 ? Resource.Dimension.wino_editor_min_height : minimumHeight));
        _field.SetTextColor(WinoColors.PrimaryText(context));
        _field.SetBackgroundResource(Resource.Drawable.wino_editor_background);
        int pad = WinoTokens.Px(context, Resource.Dimension.wino_space_m);
        _field.SetPadding(pad, pad, pad, pad);
        _field.TextChanged += (_, _) => ContentChanged?.Invoke(this, EventArgs.Empty);
        AddView(_field, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) });
    }

    /// <summary>Raised on every edit.</summary>
    public event EventHandler? ContentChanged;

    public EditText Field => _field;

    /// <summary>The content as HTML (a paragraph per line block).</summary>
    public string Html
    {
        get
        {
            if (_field.EditableText is not { } text || text.Length() == 0) return string.Empty;
            return (OperatingSystem.IsAndroidVersionAtLeast(24)
                ? global::Android.Text.Html.ToHtml(text, ToHtmlOptions.ParagraphLinesIndividual)
                : global::Android.Text.Html.ToHtml(text)) ?? string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { _field.Text = string.Empty; return; }
            var spanned = OperatingSystem.IsAndroidVersionAtLeast(24)
                ? global::Android.Text.Html.FromHtml(value, FromHtmlOptions.ModeCompact)
                : global::Android.Text.Html.FromHtml(value);
            _field.SetText(spanned, TextView.BufferType.Spannable);
        }
    }

    private static MaterialButton Tool(Context context, WinoIconGlyph glyph, string label, Action action)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.materialIconButtonStyle)
        {
            Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), WinoColors.PrimaryText(context)),
            ContentDescription = label
        };
        button.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.PrimaryText(context));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) button.TooltipText = label;
        button.Click += (_, _) => action();
        return button;
    }

    private (int Start, int End) Selection()
    {
        int start = Math.Max(0, Math.Min(_field.SelectionStart, _field.SelectionEnd));
        int end = Math.Max(0, Math.Max(_field.SelectionStart, _field.SelectionEnd));
        return (start, end);
    }

    private void ToggleStyle(TypefaceStyle style)
    {
        var (start, end) = Selection();
        if (start == end || _field.EditableText is not { } text) return;
        var existing = text.GetSpans(start, end, Java.Lang.Class.FromType(typeof(StyleSpan)))?.OfType<StyleSpan>().Where(span => span.Style == style).ToList() ?? [];
        if (existing.Count > 0) foreach (var span in existing) text.RemoveSpan(span);
        else text.SetSpan(new StyleSpan(style), start, end, SpanTypes.ExclusiveExclusive);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleUnderline()
    {
        var (start, end) = Selection();
        if (start == end || _field.EditableText is not { } text) return;
        var existing = text.GetSpans(start, end, Java.Lang.Class.FromType(typeof(UnderlineSpan)))?.OfType<UnderlineSpan>().ToList() ?? [];
        if (existing.Count > 0) foreach (var span in existing) text.RemoveSpan(span);
        else text.SetSpan(new UnderlineSpan(), start, end, SpanTypes.ExclusiveExclusive);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearFormatting()
    {
        var (start, end) = Selection();
        if (_field.EditableText is not { } text) return;
        if (start == end) { start = 0; end = text.Length(); }
        var spans = text.GetSpans(start, end, Java.Lang.Class.FromType(typeof(CharacterStyle)))?.OfType<CharacterStyle>().ToList() ?? [];
        foreach (var span in spans) text.RemoveSpan(span);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}

using Android.Views;
using Wino.Core.Domain;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// The mobile settings row rule (owner, 15 Oct 2026; canvas Android SettingsControls "API mapping"): a switch is an inline
/// row; a picker, stepper, slider, text field or time is a "Title · value ›" row; custom content, several controls and
/// every expander push the detail page; a button is an action row; a clickable card runs its own activation.
/// </summary>
public static class WinoSettingsRules
{
    /// <summary>The row kind of <paramref name="card"/>.</summary>
    public static WinoSettingsRowKind Classify(WinoSettingsCard card)
    {
        if (card.RowKind is { } explicitKind) return explicitKind;
        if (card.IsClickable) return WinoSettingsRowKind.Navigate;
        var content = card.Content;
        var bottom = card.BottomContent;
        if (content is null && bottom is null) return WinoSettingsRowKind.Plain;
        if (content is not null && bottom is not null)
        {
            // Glyph buttons beside a list item stay on the row; everything else needs the detail page.
            var bottomKind = WinoSettingsControls.KindOf(bottom);
            bool readOnlyBottom = bottomKind == WinoSettingsControlKind.Label
                || (bottomKind == WinoSettingsControlKind.Custom && WinoSettingsControls.ValueStack(bottom) is null && WinoSettingsControls.ButtonsOf(bottom).Count == 0);
            return readOnlyBottom && (card.LeadingView is not null || WinoSettingsControls.KindOf(content) == WinoSettingsControlKind.IconButtons)
                ? WinoSettingsRowKind.Plain
                : WinoSettingsRowKind.Push;
        }
        var control = content ?? bottom;
        var kind = WinoSettingsControls.KindOf(control);
        switch (kind)
        {
            case WinoSettingsControlKind.Toggle:
                return WinoSettingsRowKind.Switch;
            case WinoSettingsControlKind.Label:
                return WinoSettingsRowKind.Info;
            case WinoSettingsControlKind.IconButtons:
                return WinoSettingsRowKind.Plain;
            case WinoSettingsControlKind.Button:
                return WinoSettingsControls.ButtonsOf(control).Count == 1 ? WinoSettingsRowKind.Action : WinoSettingsRowKind.Push;
            case WinoSettingsControlKind.Custom:
                return WinoSettingsControls.ValueStack(control) is not null ? WinoSettingsRowKind.Value : WinoSettingsRowKind.Push;
            default:
                return WinoSettingsControls.IsValueKind(kind) ? WinoSettingsRowKind.Value : WinoSettingsRowKind.Push;
        }
    }

    /// <summary>The value text of a value row: the control's value, or the values of a stack of dependent pickers.</summary>
    public static string? ValueOf(WinoSettingsCard card)
    {
        var control = card.Content ?? card.BottomContent;
        if (WinoSettingsControls.ValueStack(control) is { } stack)
            return Join(stack.Where(view => WinoSettingsControls.IsValueKind(WinoSettingsControls.KindOf(view))).Select(WinoSettingsControls.ValueText));
        return WinoSettingsControls.ValueText(control);
    }

    /// <summary>
    /// The value of a pushed card: Off while its switch is off, else the value of its second control (an undo delay),
    /// else On.
    /// </summary>
    public static string? PushValueOf(WinoSettingsCard card)
    {
        var content = card.Content;
        var bottom = card.BottomContent;
        if (WinoSettingsControls.ToggleOf(content) is { } toggle && WinoSettingsControls.KindOf(content) == WinoSettingsControlKind.Toggle)
        {
            if (!toggle.Checked) return Translator.Android_Settings_Off;
            return WinoSettingsControls.IsValueKind(WinoSettingsControls.KindOf(bottom)) ? WinoSettingsControls.ValueText(bottom) : Translator.Android_Settings_On;
        }
        return null;
    }

    /// <summary>
    /// The value of an expander row: Off while its header switch is off, else the values of its first two pickers or
    /// steppers ("Roboto · 15"), else On when it has a switch.
    /// </summary>
    public static string? ValueOf(WinoSettingsExpander expander)
    {
        var header = expander.HeaderCard.Content;
        var toggle = WinoSettingsControls.KindOf(header) == WinoSettingsControlKind.Toggle ? WinoSettingsControls.ToggleOf(header) : null;
        if (toggle is { Checked: false }) return Translator.Android_Settings_Off;
        if (header is not null && toggle is null && WinoSettingsControls.IsValueKind(WinoSettingsControls.KindOf(header)))
            return WinoSettingsControls.ValueText(header);
        var values = new List<string>();
        foreach (var item in expander.Items)
        {
            if (values.Count == 2) break;
            if (item is not WinoSettingsCard { Visibility: ViewStates.Visible, Enabled: true } card || Classify(card) != WinoSettingsRowKind.Value) continue;
            if (ValueOf(card) is { Length: > 0 } value) values.Add(value);
        }
        if (values.Count > 0) return string.Join(" · ", values);
        return toggle is not null ? Translator.Android_Settings_On : null;
    }

    private static string? Join(IEnumerable<string?> values)
    {
        var parts = values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(2).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>
/// Opening the generic detail page. The app sets <see cref="Presenter"/>; a row asks it to show the detail of its
/// source. Detail pages are addressed by a path of segments (title and occurrence) from the page's sources, so a
/// recreated activity finds the same setting in the page's new views.
/// </summary>
public static class WinoSettingsNavigation
{
    /// <summary>Shows the detail page of <c>source</c>, opened from the row <c>origin</c>. Set by the app.</summary>
    public static Action<View, View>? Presenter { get; set; }

    internal static void Open(View origin, View? source)
    {
        if (source is null) return;
        Presenter?.Invoke(origin, source);
    }

    /// <summary>The title of a card or expander.</summary>
    public static string Title(View source) => source switch
    {
        WinoSettingsCard card => card.Header,
        WinoSettingsExpander expander => expander.HeaderCard.Header,
        _ => string.Empty
    };

    /// <summary>Every card and expander shown in the sections under <paramref name="root"/>, in order.</summary>
    public static List<View> PageSources(View root)
    {
        var sources = new List<View>();
        void Walk(View view)
        {
            if (view is WinoSettingsGroup group)
            {
                sources.AddRange(group.Sources.Where(source => source is WinoSettingsCard or WinoSettingsExpander));
                return;
            }
            if (view is ViewGroup parent)
                for (int index = 0; index < parent.ChildCount; index++)
                    if (parent.GetChildAt(index) is { } child) Walk(child);
        }
        Walk(root);
        return sources;
    }

    /// <summary>The cards and expanders inside a detail page's source (an expander's items).</summary>
    public static List<View> ChildSources(View source)
        => source is WinoSettingsExpander expander ? expander.Items.Where(item => item is WinoSettingsCard or WinoSettingsExpander).ToList() : [];

    /// <summary>The path segment of <paramref name="target"/> among <paramref name="sources"/>: its title and occurrence.</summary>
    public static string? Segment(IReadOnlyList<View> sources, View target)
    {
        var title = Title(target);
        int occurrence = 0;
        foreach (var source in sources)
        {
            if (ReferenceEquals(source, target)) return $"{occurrence}:{title}";
            if (Title(source) == title) occurrence++;
        }
        return null;
    }

    /// <summary>The source a segment from <see cref="Segment"/> names, or null.</summary>
    public static View? Resolve(IReadOnlyList<View> sources, string segment)
    {
        int split = segment.IndexOf(':');
        if (split <= 0 || !int.TryParse(segment[..split], out int occurrence)) return null;
        var title = segment[(split + 1)..];
        foreach (var source in sources)
        {
            if (Title(source) != title) continue;
            if (occurrence-- == 0) return source;
        }
        return null;
    }
}

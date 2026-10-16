using System.Globalization;
using System.Runtime.CompilerServices;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Slider;
using Google.Android.Material.TextField;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>What a settings control is, so rows and the detail page can render it by rule.</summary>
public enum WinoSettingsControlKind
{
    /// <summary>A view the row rules do not know (a preview, swatches, a list): pushed and hosted as a section.</summary>
    Custom,
    /// <summary>A two-state switch (stays inline everywhere).</summary>
    Toggle,
    /// <summary>One of several options (Windows ComboBox, RadioButtons, segmented buttons): a radio list on the detail page.</summary>
    Choice,
    /// <summary>A number with minus and plus (Windows NumberBox).</summary>
    Stepper,
    /// <summary>A slider.</summary>
    Slider,
    /// <summary>A text field.</summary>
    Text,
    /// <summary>A button that opens the time picker.</summary>
    Time,
    /// <summary>Read-only value text.</summary>
    Label,
    /// <summary>A button with a title (or a row of them): action rows.</summary>
    Button,
    /// <summary>Glyph-only buttons (remove, more): kept at the end of the row.</summary>
    IconButtons
}

/// <summary>
/// The options of a choice control (a dropdown or segmented group), read and written through the same binding the
/// inline control uses, so the detail page's radio list writes back exactly as the dropdown did.
/// </summary>
public sealed class WinoSettingsChoice(Func<IReadOnlyList<string>> items, Func<int> selectedIndex, Action<int> select)
{
    public IReadOnlyList<string> Items()
    {
        try { return items(); }
        catch { return []; }
    }

    public int SelectedIndex()
    {
        try { return selectedIndex(); }
        catch { return -1; }
    }

    public void Select(int index) => select(index);
}

/// <summary>
/// Remembers what each settings control is (registered by <see cref="SettingsBinder"/>) and reads its current value as
/// text for the trailing value of a row. Controls are identified by instance; the table does not keep them alive.
/// </summary>
public static class WinoSettingsControls
{
    private sealed class Info
    {
        public WinoSettingsControlKind Kind;
        public WinoSettingsChoice? Choice;
        public Func<string?>? Value;
        public bool IsDestructive;
    }

    private static readonly ConditionalWeakTable<View, Info> Table = new();

    private static Info For(View view) => Table.GetValue(view, static _ => new Info { Kind = WinoSettingsControlKind.Custom });

    /// <summary>Records what <paramref name="view"/> is; <paramref name="value"/> reads its value text.</summary>
    public static T Register<T>(T view, WinoSettingsControlKind kind, Func<string?>? value = null, WinoSettingsChoice? choice = null) where T : View
    {
        var info = For(view);
        info.Kind = kind;
        info.Value = value;
        info.Choice = choice;
        return view;
    }

    /// <summary>Marks a button as destructive: its action row uses the error colour.</summary>
    public static T MarkDestructive<T>(T view) where T : View
    {
        For(view).IsDestructive = true;
        return view;
    }

    public static bool IsDestructive(View view) => Table.TryGetValue(view, out var info) && info.IsDestructive;

    public static WinoSettingsChoice? Choice(View view) => Unwrap(view) is { } control && Table.TryGetValue(control, out var info) ? info.Choice : null;

    /// <summary>
    /// The control inside a plain one-child row (pages wrap a dropdown or stepper in a row to keep its own width), or
    /// <paramref name="view"/> itself.
    /// </summary>
    public static View? Unwrap(View? view)
    {
        while (view is LinearLayout { ChildCount: 1 } row && row.GetType() == typeof(LinearLayout) && !Table.TryGetValue(row, out _)
            && row.GetChildAt(0) is { } child)
            view = child;
        return view;
    }

    /// <summary>The kind of <paramref name="view"/>: registered, or inferred from its type and children.</summary>
    public static WinoSettingsControlKind KindOf(View? view)
    {
        view = Unwrap(view);
        if (view is null) return WinoSettingsControlKind.Custom;
        if (Table.TryGetValue(view, out var info) && info.Kind != WinoSettingsControlKind.Custom) return info.Kind;
        return view switch
        {
            CompoundButton { Text: null or { Length: 0 } } => WinoSettingsControlKind.Toggle,
            Slider => WinoSettingsControlKind.Slider,
            TextInputLayout or EditText => WinoSettingsControlKind.Text,
            // Checkable buttons are toggles of a custom control (quiet-hours days), not actions.
            MaterialButton { Checkable: true } => WinoSettingsControlKind.Custom,
            Button { Text: null or { Length: 0 } } => WinoSettingsControlKind.IconButtons,
            Button => WinoSettingsControlKind.Button,
            TextView => WinoSettingsControlKind.Label,
            ViewGroup group when Children(group) is { Count: > 0 } children && children.All(child => KindOf(child) == WinoSettingsControlKind.Button)
                => WinoSettingsControlKind.Button,
            ViewGroup group when Children(group) is { Count: > 0 } children && children.All(child => KindOf(child) == WinoSettingsControlKind.IconButtons)
                => WinoSettingsControlKind.IconButtons,
            // A switch with its busy ring or state text before it.
            LinearLayout { Orientation: Orientation.Horizontal, ChildCount: > 0 } row when row.GetChildAt(row.ChildCount - 1) is CompoundButton { Text: null or { Length: 0 } }
                => WinoSettingsControlKind.Toggle,
            _ => WinoSettingsControlKind.Custom
        };
    }

    /// <summary>The switch inside a toggle control (the control itself, or the last view of its row).</summary>
    public static CompoundButton? ToggleOf(View? view) => Unwrap(view) switch
    {
        CompoundButton toggle => toggle,
        LinearLayout { ChildCount: > 0 } row => row.GetChildAt(row.ChildCount - 1) as CompoundButton,
        _ => null
    };

    /// <summary>The buttons of a button control, in order.</summary>
    public static List<Button> ButtonsOf(View? view)
    {
        var buttons = new List<Button>();
        void Walk(View? current)
        {
            switch (current)
            {
                case Button button:
                    buttons.Add(button);
                    break;
                case ViewGroup group:
                    foreach (var child in Children(group)) Walk(child);
                    break;
            }
        }
        Walk(view);
        return buttons;
    }

    /// <summary>A stack of value controls (a choice and the choice that depends on it): each is rendered on its own.</summary>
    public static List<View>? ValueStack(View? view)
    {
        view = Unwrap(view);
        if (view is not LinearLayout stack || view.GetType() != typeof(LinearLayout) || Table.TryGetValue(view, out _)) return null;
        var children = Children(stack).Select(child => Unwrap(child) ?? child).ToList();
        if (children.Count == 0) return null;
        return children.All(child => IsValueKind(KindOf(child)) || KindOf(child) == WinoSettingsControlKind.Label) && children.Any(child => IsValueKind(KindOf(child))) ? children : null;
    }

    public static bool IsValueKind(WinoSettingsControlKind kind)
        => kind is WinoSettingsControlKind.Choice or WinoSettingsControlKind.Stepper or WinoSettingsControlKind.Slider
            or WinoSettingsControlKind.Text or WinoSettingsControlKind.Time;

    /// <summary>The current value of a control as row text, or null.</summary>
    public static string? ValueText(View? view)
    {
        if (view is { Visibility: not ViewStates.Visible }) return null;
        view = Unwrap(view);
        if (view is null || view.Visibility != ViewStates.Visible) return null;
        if (Table.TryGetValue(view, out var info) && info.Value is { } value)
        {
            try { return Clean(value()); }
            catch { return null; }
        }
        return KindOf(view) switch
        {
            WinoSettingsControlKind.Label when view is TextView label => Clean(label.Text),
            WinoSettingsControlKind.Text => Clean((view as TextInputLayout)?.EditText?.Text ?? (view as EditText)?.Text),
            WinoSettingsControlKind.Slider when view is Slider slider => slider.Value.ToString("0.##", CultureInfo.CurrentCulture),
            WinoSettingsControlKind.Choice when view is MaterialButtonToggleGroup group => CheckedText(group),
            WinoSettingsControlKind.Choice or WinoSettingsControlKind.Time when view is TextView text => Clean(text.Text),
            _ => FirstText(view)
        };
    }

    private static string? CheckedText(MaterialButtonToggleGroup group)
        => Children(group).OfType<MaterialButton>().FirstOrDefault(button => button.Checked)?.Text;

    /// <summary>The first plain text in a custom view (a "Clouds" theme name next to its preview).</summary>
    public static string? FirstText(View? view) => view switch
    {
        Button => null,
        EditText => null,
        TextView text when text.Visibility == ViewStates.Visible => Clean(text.Text),
        ViewGroup group => Children(group).Where(child => child.Visibility == ViewStates.Visible).Select(FirstText).FirstOrDefault(text => text is not null),
        _ => null
    };

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Replace('\n', ' ').Trim();
        return line.Length == 0 ? null : line;
    }

    public static List<View> Children(ViewGroup group)
    {
        var children = new List<View>(group.ChildCount);
        for (int index = 0; index < group.ChildCount; index++)
            if (group.GetChildAt(index) is { } child) children.Add(child);
        return children;
    }
}

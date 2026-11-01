using System.ComponentModel;
using System.Windows.Input;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Builds an onboarding form in code from the Material pieces of board ImapSetup (outlined fields, section labels, M3
/// switches, exposed dropdowns, help text and links) and binds each piece to <typeparamref name="T"/>. Pieces are
/// appended to the current container, which <see cref="Group"/> switches for a block that shows and hides together.
/// </summary>
internal sealed class OnboardingForm<T>(Context context, LinearLayout root, ViewBinder binder, T source) where T : INotifyPropertyChanged
{
    private readonly LayoutInflater _inflater = LayoutInflater.From(context) ?? throw new InvalidOperationException("No layout inflater.");
    private LinearLayout _container = root;

    public ViewBinder Binder => binder;

    public Context Context => context;

    /// <summary>Appends a vertical block and adds the pieces <paramref name="build"/> creates to it.</summary>
    public LinearLayout Group(Action build, string? visibleProperty = null, Func<T, bool>? visible = null)
    {
        var group = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _container.AddView(group, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var previous = _container;
        _container = group;
        try { build(); }
        finally { _container = previous; }
        if (visibleProperty is not null && visible is not null) binder.Visible(group, source, visibleProperty, visible);
        return group;
    }

    /// <summary>Appends a horizontal row (host and port) and adds the pieces <paramref name="build"/> creates to it.</summary>
    public LinearLayout Row(Action build, int topDp = 0)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        _container.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Wino.Presentation.Android.WinoUnits.Dp(context, topDp)
        });
        var previous = _container;
        _container = row;
        try { build(); }
        finally { _container = previous; }
        return row;
    }

    /// <summary>An outlined text field, two-way with the property.</summary>
    public TextInputLayout Field(string label, string property, Func<T, string?> read, Action<T, string> write,
        InputTypes inputType = InputTypes.ClassText, string? helper = null, int? fixedWidthDimension = null)
    {
        var layout = Inflate<TextInputLayout>(Resource.Layout.onboarding_text_field);
        layout.Hint = label;
        if (!string.IsNullOrEmpty(helper)) layout.HelperText = helper;
        if (layout.EditText is { } edit)
        {
            edit.InputType = inputType;
            if ((inputType & InputTypes.TextVariationPassword) == InputTypes.TextVariationPassword)
                layout.EndIconMode = TextInputLayout.EndIconPasswordToggle;
            binder.TwoWayText(edit, source, property, read, write);
        }
        Add(layout, fixedWidthDimension);
        return layout;
    }

    /// <summary>An exposed dropdown over <paramref name="items"/>, two-way with an index property.</summary>
    public TextInputLayout Dropdown(string label, IReadOnlyList<string> items, string property, Func<T, int> read, Action<T, int> write)
    {
        var layout = Inflate<TextInputLayout>(Resource.Layout.onboarding_dropdown);
        layout.Hint = label;
        if (layout.EditText is MaterialAutoCompleteTextView field)
        {
            field.SetSimpleItems(items.ToArray());
            binder.Bind(source, property, read, index =>
            {
                var text = index >= 0 && index < items.Count ? items[index] : string.Empty;
                if (field.Text != text) field.SetText(text, false);
            });
            EventHandler<AdapterView.ItemClickEventArgs> handler = (_, args) =>
            {
                try { write(source, args.Position); }
                catch (Exception exception) { binder.Error(exception); }
            };
            field.ItemClick += handler;
            binder.Scope.Own(new ActionDisposable(() => field.ItemClick -= handler));
        }
        Add(layout);
        return layout;
    }

    /// <summary>A label with an M3 switch, two-way with the property.</summary>
    public View Switch(string label, string property, Func<T, bool> read, Action<T, bool> write)
    {
        var row = Inflate<View>(Resource.Layout.onboarding_switch_row);
        if (row.FindViewById<TextView>(Resource.Id.switch_label) is { } text) text.Text = label;
        if (row.FindViewById<MaterialSwitch>(Resource.Id.switch_toggle) is { } toggle)
        {
            toggle.ContentDescription = label;
            binder.Checked(toggle, source, property, read, write);
        }
        Add(row);
        return row;
    }

    /// <summary>A section label in the accent colour (board ImapSetup "Incoming server").</summary>
    public TextView Section(string text)
    {
        var label = Text(text, Resource.Style.TextAppearance_Wino_SectionLabel, topDp: 24);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(label, true);
        return label;
    }

    /// <summary>Supporting text; with a property it follows the ViewModel and hides while empty.</summary>
    public TextView Help(string? text = null, string? property = null, Func<T, string?>? read = null, int topDp = 8)
    {
        var label = Text(text ?? string.Empty, Resource.Style.TextAppearance_Wino_Caption, topDp);
        if (property is not null && read is not null) binder.Bind(source, property, read, value => OnboardingViews.Caption(label, value));
        return label;
    }

    /// <summary>A text button that runs <paramref name="command"/> (help links).</summary>
    public MaterialButton Link(string text, ICommand command)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = text, Gravity = GravityFlags.Start | GravityFlags.CenterVertical };
        button.SetTextAppearance(Resource.Style.TextAppearance_Wino_SectionLabel);
        button.SetPadding(0, button.PaddingTop, 0, button.PaddingBottom);
        binder.Command(button, command);
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        _container.AddView(button, parameters);
        return button;
    }

    /// <summary>A Wino info bar (page errors, discovery results).</summary>
    public WinoInfoBar InfoBar(WinoInfoBarSeverity severity, string? title = null)
    {
        var bar = new WinoInfoBar(context) { Severity = severity, Title = title, IsClosable = false, IsOpen = false };
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Wino.Presentation.Android.WinoUnits.Dp(context, 12)
        };
        _container.AddView(bar, parameters);
        return bar;
    }

    /// <summary>
    /// A vertical block shown while <paramref name="visible"/> is true. The value is re-read after any change, for
    /// computed properties the ViewModel raises only through the properties they derive from.
    /// </summary>
    public LinearLayout GroupWhen(Func<T, bool> visible, Action build)
    {
        var group = Group(build);
        ShowWhen(group, visible);
        return group;
    }

    public TView ShowWhen<TView>(TView view, Func<T, bool> visible) where TView : View
    {
        binder.BindAny(source, visible, value => OnboardingViews.Show(view, value));
        return view;
    }

    /// <summary>A filled (primary) or outlined button that runs <paramref name="command"/>.</summary>
    public MaterialButton Button(string text, ICommand command, bool primary = false)
    {
        var button = primary
            ? new MaterialButton(context) { Text = text }
            : new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = text };
        button.SetMinHeight(Wino.Presentation.Android.WinoUnits.Dp(context, 48));
        binder.Command(button, command);
        Add(button, wrap: true);
        return button;
    }

    /// <summary>A small indeterminate spinner shown while <paramref name="active"/> is true.</summary>
    public View Spinner(string property, Func<T, bool> active)
    {
        var spinner = new Google.Android.Material.ProgressIndicator.CircularProgressIndicator(context)
        {
            Indeterminate = true,
            IndicatorSize = Wino.Presentation.Android.WinoUnits.Dp(context, 20)
        };
        binder.Visible(spinner, source, property, active);
        Add(spinner, wrap: true);
        return spinner;
    }

    /// <summary>
    /// A rounded, tinted card with a leading glyph (the Windows secure sign-in and signed-in cards); the pieces
    /// <paramref name="build"/> creates go to its content column.
    /// </summary>
    public LinearLayout Card(Wino.Core.Domain.Enums.WinoIconGlyph glyph, global::Android.Graphics.Color tint, Action build)
    {
        int Dp(double value) => Wino.Presentation.Android.WinoUnits.Dp(context, value);
        var card = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        var background = new global::Android.Graphics.Drawables.GradientDrawable();
        background.SetCornerRadius(Wino.Presentation.Android.WinoUnits.DpF(context, 12));
        background.SetColor(Wino.Presentation.Android.WinoColors.WithAlpha(tint, 0.08));
        background.SetStroke(Dp(1), tint);
        card.Background = background;
        card.SetPadding(Dp(14), Dp(14), Dp(14), Dp(14));
        var icon = new Wino.Presentation.Android.WinoIconView(context)
        {
            Icon = glyph,
            GlyphSize = 20,
            Colorful = false,
            ImportantForAccessibility = ImportantForAccessibility.No
        };
        icon.SetTextColor(tint);
        card.AddView(icon, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { MarginEnd = Dp(12) });
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        card.AddView(column, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) };
        _container.AddView(card, parameters);
        var previous = _container;
        _container = column;
        try { build(); }
        finally { _container = previous; }
        return card;
    }

    /// <summary>Body text that follows <paramref name="read"/>.</summary>
    public TextView Body(string property, Func<T, string?> read, bool strong = false, int topDp = 0)
    {
        var label = Text(string.Empty, Resource.Style.TextAppearance_Wino_Body, topDp);
        if (strong)
        {
            label.SetTypeface(label.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
            label.SetTextColor(Wino.Presentation.Android.WinoColors.PrimaryText(context));
        }
        binder.Bind(source, property, read, value => label.Text = value ?? string.Empty);
        return label;
    }

    /// <summary>A selectable monospaced line (a redirect address an administrator copies).</summary>
    public TextView Mono(string text)
    {
        var label = Text(text, Resource.Style.TextAppearance_Wino_Body, 8);
        label.Typeface = global::Android.Graphics.Typeface.Monospace;
        label.SetTextIsSelectable(true);
        return label;
    }

    public void Add(View view, int? fixedWidthDimension = null, bool wrap = false)
    {
        LinearLayout.LayoutParams parameters;
        var horizontal = _container.Orientation == Orientation.Horizontal;
        if (wrap)
        {
            parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            if (horizontal) parameters.Gravity = GravityFlags.CenterVertical;
            else parameters.TopMargin = Wino.Presentation.Android.WinoUnits.Dp(context, 8);
        }
        else if (horizontal)
        {
            parameters = fixedWidthDimension is { } dimension
                ? new LinearLayout.LayoutParams(context.Resources?.GetDimensionPixelSize(dimension) ?? ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                : new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        }
        else parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        if (horizontal && _container.ChildCount > 0) parameters.MarginStart = Wino.Presentation.Android.WinoUnits.Dp(context, 12);
        if (!wrap && view.LayoutParameters is ViewGroup.MarginLayoutParams margins) parameters.TopMargin = margins.TopMargin;
        _container.AddView(view, parameters);
    }

    private TView Inflate<TView>(int layout) where TView : View
        => _inflater.Inflate(layout, _container, false) as TView ?? throw new InvalidOperationException($"Layout {layout} did not inflate a {typeof(TView).Name}.");

    private TextView Text(string text, int appearance, int topDp)
    {
        var label = new TextView(context) { Text = text };
        label.SetTextAppearance(appearance);
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Wino.Presentation.Android.WinoUnits.Dp(context, topDp)
        };
        _container.AddView(label, parameters);
        return label;
    }
}

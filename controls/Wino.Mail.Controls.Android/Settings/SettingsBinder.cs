using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.CheckBox;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.Slider;
using Google.Android.Material.TextField;
using Google.Android.Material.TimePicker;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.Settings;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// Creates settings controls already bound to a source (UIKit and AppKit <c>SettingsBinder</c>, same method names), so
/// settings pages stay declarative: MaterialSwitch for toggles, an outlined dropdown button with a popup menu for
/// pickers (a modal bottom sheet of choices), a numeric field with minus and plus buttons for steppers, a Material toggle group for segments, outlined
/// text fields, a button that opens the MaterialTimePicker for times, a Slider, and buttons bound to commands. Every
/// subscription is owned by the supplied <see cref="BindingScope"/>; disposing the scope detaches the controls from their
/// sources. General bindings (text, visibility, enabled) are on <see cref="Views"/>.
/// </summary>
public sealed class SettingsBinder
{
    private readonly Context _context;

    public SettingsBinder(Context context, BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    {
        _context = context;
        Views = new ViewBinder(scope, dispatcher, error);
    }

    /// <summary>The general view binder over the same scope.</summary>
    public ViewBinder Views { get; }

    public BindingScope Scope => Views.Scope;

    public IDispatcher Dispatcher => Views.Dispatcher;

    public Action<Exception> Error => Views.Error;

    public Context Context => _context;

    /// <summary>A binder for a child scope, for rows rebuilt when a collection changes.</summary>
    public SettingsBinder Child(BindingScope scope) => new(_context, scope, Views.Dispatcher, Views.Error);

    /// <summary>Two-way switch.</summary>
    public MaterialSwitch Switch<T>(T source, string property, Func<T, bool> read, Action<T, bool> write, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var control = WinoMaterialAccent.Tint(new MaterialSwitch(_context) { ContentDescription = accessibilityLabel });
        return Views.Checked(control, source, property, read, write);
    }

    /// <summary>A switch that shows a value one way and runs a command when toggled (Windows ToggleButton + Command).</summary>
    public MaterialSwitch CommandSwitch<T>(T source, string property, Func<T, bool> read, ICommand command, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var control = WinoMaterialAccent.Tint(new MaterialSwitch(_context) { ContentDescription = accessibilityLabel });
        var binding = Views.Bind(source, property, read, value => { if (control.Checked != value) control.Checked = value; });
        var commandBinding = Scope.Own(new CommandBinding(command, () => null, enabled => control.Enabled = enabled, Views.Dispatcher, Views.Error));
        EventHandler handler = (_, _) =>
        {
            commandBinding.Execute();
            binding.Refresh();
        };
        control.Click += handler;
        Scope.Own(new ActionDisposable(() => control.Click -= handler));
        return control;
    }

    /// <summary>Two-way check box with its title (the AppKit checkbox).</summary>
    public MaterialCheckBox Checkbox<T>(T source, string title, string property, Func<T, bool> read, Action<T, bool> write)
        where T : INotifyPropertyChanged
    {
        var control = new MaterialCheckBox(_context) { Text = title };
        control.SetTextColor(WinoColors.PrimaryText(_context));
        WinoMaterialAccent.Tint(control);
        return WinoSettingsControls.Register(Views.Checked(control, source, property, read, write), WinoSettingsControlKind.Toggle);
    }

    /// <summary>A dropdown over fixed items; the selected index is two-way. <c>width</c> is a minimum-width dimension token (0: none).</summary>
    public MaterialButton PopUp<T>(T source, IReadOnlyList<string> items, string property, Func<T, int> read, Action<T, int> write, int width = 0)
        where T : INotifyPropertyChanged
    {
        var button = CreateDropdown(_context, width);
        var binding = Scope.Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            int index = SettingsValueRules.SelectableIndex(value, items.Count);
            button.Text = index >= 0 ? items[index] : string.Empty;
        }, Views.Dispatcher, Views.Error, write));
        void Choose(int selected)
        {
            binding.UpdateSource(selected);
            binding.Refresh();
        }
        Views.Click(button, () => ShowMenu(button, items, SettingsValueRules.SelectableIndex(read(source), items.Count), Choose));
        return WinoSettingsControls.Register(button, WinoSettingsControlKind.Choice, () => button.Text,
            new WinoSettingsChoice(() => items, () => SettingsValueRules.SelectableIndex(read(source), items.Count), Choose));
    }

    /// <summary>
    /// A dropdown over the source's items; the selected item is two-way. When <paramref name="itemsProperty"/> is given
    /// the items are read again whenever that property changes; <paramref name="itemsCollection"/> refreshes the text
    /// after collection changes. The menu always lists the current items.
    /// </summary>
    public MaterialButton PopUp<T, TItem>(T source, Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write, string? itemsProperty = null, int width = 0,
        INotifyCollectionChanged? itemsCollection = null)
        where T : INotifyPropertyChanged
    {
        var button = CreateDropdown(_context, width);
        void Render(TItem? value) => button.Text = value is null ? string.Empty : SafeText(text, value);
        var selection = Scope.Own(new PropertyBinding<T, TItem?>(source, property, read, Render, Views.Dispatcher, Views.Error,
            (target, value) => { if (value is not null) write(target, value); }));
        if (itemsProperty is not null) Views.Bind(source, itemsProperty, items, _ => Render(read(source)));
        if (itemsCollection is not null) Views.Collection(itemsCollection, () => Render(read(source)));
        int SelectedIndex(IReadOnlyList<TItem> list)
        {
            var current = read(source);
            for (int index = 0; index < list.Count; index++)
                if (current is not null && Equals(list[index], current)) return index;
            return -1;
        }
        void Choose(int index)
        {
            if (items(source) is not { } list || index < 0 || index >= list.Count) return;
            selection.UpdateSource(list[index]);
            selection.Refresh();
        }
        Views.Click(button, () =>
        {
            if (items(source) is not { Count: > 0 } list) return;
            ShowMenu(button, list.Select(item => SafeText(text, item)).ToList(), SelectedIndex(list), Choose);
        });
        return WinoSettingsControls.Register(button, WinoSettingsControlKind.Choice, () => button.Text, new WinoSettingsChoice(
            () => items(source) is { } list ? list.Select(item => SafeText(text, item)).ToList() : [],
            () => items(source) is { } list ? SelectedIndex(list) : -1,
            Choose));
    }

    /// <summary>A numeric field between minus and plus buttons with bounds, bound to an integer property (Windows NumberBox).</summary>
    public LinearLayout Stepper<T>(T source, string property, Func<T, int> read, Action<T, int> write,
        int minimum, int maximum, int increment = 1, string? suffix = null, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var minus = IconButton(WinoIconGlyph.SubtractCircle, Translator.Android_Settings_Decrease);
        var field = new EditText(_context)
        {
            InputType = InputTypes.ClassNumber | InputTypes.NumberFlagSigned,
            Gravity = GravityFlags.Center,
            ContentDescription = accessibilityLabel,
            ImeOptions = global::Android.Views.InputMethods.ImeAction.Done
        };
        field.SetSingleLine(true);
        field.SetTextColor(WinoColors.PrimaryText(_context));
        field.SetMinWidth(WinoTokens.Px(_context, Resource.Dimension.wino_size_row));
        var plus = IconButton(WinoIconGlyph.Add, Translator.Android_Settings_Increase);
        row.AddView(minus);
        row.AddView(field, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        row.AddView(plus);
        if (!string.IsNullOrEmpty(suffix))
        {
            var label = new TextView(_context) { Text = suffix };
            label.SetTextColor(WinoColors.SecondaryText(_context));
            WinoTokens.Pad(label, Resource.Dimension.wino_space_2xs, 0, 0, 0);
            row.AddView(label);
        }

        var binding = Scope.Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            var text = value.ToString(CultureInfo.CurrentCulture);
            if (field.Text != text) field.Text = text;
            minus.Enabled = value > minimum;
            plus.Enabled = value < maximum;
        }, Views.Dispatcher, Views.Error, write));

        void Commit(int value)
        {
            binding.UpdateSource(SettingsValueRules.ClampStepper(value, minimum, maximum));
            binding.Refresh();
        }

        Views.Click(minus, () => Commit(read(source) - increment));
        Views.Click(plus, () => Commit(read(source) + increment));
        EventHandler<View.FocusChangeEventArgs> focus = (_, args) =>
        {
            if (!args.HasFocus) Commit(SettingsValueRules.ParseStepper(field.Text, read(source), minimum, maximum));
        };
        EventHandler<TextView.EditorActionEventArgs> done = (_, args) =>
        {
            Commit(SettingsValueRules.ParseStepper(field.Text, read(source), minimum, maximum));
            args.Handled = false;
        };
        field.FocusChange += focus;
        field.EditorAction += done;
        Scope.Own(new ActionDisposable(() =>
        {
            field.FocusChange -= focus;
            field.EditorAction -= done;
        }));
        return WinoSettingsControls.Register(row, WinoSettingsControlKind.Stepper,
            () => string.IsNullOrEmpty(suffix) ? field.Text : $"{field.Text} {suffix}");
    }

    /// <summary>
    /// A segmented control (Material toggle group, one selected) bound to an index property. Segments share the width
    /// equally unless <paramref name="fillWidth"/> is false (a group that scrolls sideways).
    /// </summary>
    public MaterialButtonToggleGroup Segmented<T>(T source, IReadOnlyList<string> labels, string property, Func<T, int> read, Action<T, int> write, bool fillWidth = true)
        where T : INotifyPropertyChanged
    {
        var group = new MaterialButtonToggleGroup(_context) { SingleSelection = true, SelectionRequired = true };
        var ids = new int[labels.Count];
        for (int index = 0; index < labels.Count; index++)
        {
            // Material 3 segmented button: 40 dp outlined segments, the checked one in the secondary container with a check.
            var button = new MaterialButton(_context, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = labels[index] ?? string.Empty };
            button.SetAllCaps(false);
            button.Id = View.GenerateViewId();
            button.InsetTop = 0;
            button.InsetBottom = 0;
            button.SetMinHeight(WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_height));
            button.SetPadding(WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_padding), 0, WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_padding), 0);
            button.IconSize = WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_icon);
            button.IconPadding = WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_icon_padding);
            button.IconGravity = MaterialButton.IconGravityTextStart;
            button.CornerRadius = WinoTokens.Px(_context, Resource.Dimension.wino_m3_segment_radius);
            button.StrokeWidth = WinoTokens.Px(_context, Resource.Dimension.wino_m3_stroke);
            WinoTokens.Style(button, WinoTextStyle.Label);
            WinoMaterialAccent.TintM3Segment(button);
            button.SetSingleLine(true);
            button.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            ids[index] = button.Id;
            // Equal segments across the group's width (the group fills the card under its text).
            group.AddView(button, fillWidth
                ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                : new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
        bool applying = false;
        var binding = Scope.Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            int index = SettingsValueRules.SelectableIndex(value, labels.Count);
            applying = true;
            try
            {
                if (index >= 0) group.Check(ids[index]);
                else group.ClearChecked();
            }
            finally { applying = false; }
        }, Views.Dispatcher, Views.Error, write));
        var listener = new ToggleListener((checkedId, isChecked) =>
        {
            if (group.FindViewById<MaterialButton>(checkedId) is { } segment)
                segment.Icon = isChecked ? AndroidX.Core.Content.ContextCompat.GetDrawable(_context, Resource.Drawable.wino_ic_check) : null;
            if (applying || !isChecked) return;
            int index = Array.IndexOf(ids, checkedId);
            if (index < 0) return;
            binding.UpdateSource(index);
            binding.Refresh();
        });
        group.AddOnButtonCheckedListener(listener);
        Scope.Own(new ActionDisposable(() => group.RemoveOnButtonCheckedListener(listener)));
        // The first value was checked before the listener existed.
        foreach (var id in ids)
            if (group.FindViewById<MaterialButton>(id) is { } segment && segment.Checked)
                segment.Icon = AndroidX.Core.Content.ContextCompat.GetDrawable(_context, Resource.Drawable.wino_ic_check);
        return WinoSettingsControls.Register(group, WinoSettingsControlKind.Choice, null, new WinoSettingsChoice(
            () => labels, () => SettingsValueRules.SelectableIndex(read(source), labels.Count), index =>
            {
                if (index < 0 || index >= ids.Length) return;
                binding.UpdateSource(index);
                binding.Refresh();
            }));
    }

    /// <summary>Two-way outlined text field that writes on every edit.</summary>
    public TextInputLayout TextField<T>(T source, string property, Func<T, string?> read, Action<T, string> write, string? placeholder = null, int width = 0, bool multiline = false)
        where T : INotifyPropertyChanged
    {
        var layout = CreateTextField(_context, placeholder, multiline);
        if (width != 0) layout.SetMinimumWidth(WinoTokens.Px(_context, width));
        if (layout.EditText is { } field) Views.TwoWayText(field, source, property, read, write);
        return WinoSettingsControls.Register(layout, WinoSettingsControlKind.Text);
    }

    /// <summary>An outlined Material text field with an optional hint; read its EditText.</summary>
    public static TextInputLayout CreateTextField(Context context, string? hint = null, bool multiline = false, bool password = false)
    {
        var layout = new TextInputLayout(context, null, Resource.Attribute.textInputOutlinedStyle) { Hint = hint };
        layout.BoxBackgroundMode = TextInputLayout.BoxBackgroundOutline;
        var field = new TextInputEditText(layout.Context ?? context);
        if (password)
        {
            field.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            layout.EndIconMode = TextInputLayout.EndIconPasswordToggle;
        }
        else if (multiline)
        {
            field.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences;
            field.SetMinLines(3);
            field.Gravity = GravityFlags.Top | GravityFlags.Start;
        }
        else field.SetSingleLine(true);
        layout.AddView(field, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        WinoMaterialAccent.Tint(layout);
        return layout;
    }

    /// <summary>A button showing the time of day that opens the MaterialTimePicker, bound to a TimeSpan property (Windows TimePicker).</summary>
    public MaterialButton TimePicker<T>(T source, string property, Func<T, TimeSpan> read, Action<T, TimeSpan> write, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var button = CreateDropdown(_context, 0);
        button.ContentDescription = accessibilityLabel;
        button.Icon = WinoIcons.Drawable(_context, WinoIconGlyph.Clock, WinoTokens.Dp(_context, Resource.Dimension.wino_glyph_s), WinoColors.SecondaryText(_context));
        button.IconGravity = MaterialButton.IconGravityEnd;
        var binding = Scope.Own(new PropertyBinding<T, TimeSpan>(source, property, read,
            value => button.Text = DateTime.Today.Add(value).ToString("t", CultureInfo.CurrentCulture), Views.Dispatcher, Views.Error, write));
        Views.Click(button, () =>
        {
            if (WinoMaterialAccent.FragmentManager(_context) is not { IsStateSaved: false } manager) return;
            var current = read(source);
            bool is24 = global::Android.Text.Format.DateFormat.Is24HourFormat(_context);
            var picker = new MaterialTimePicker.Builder()
                .SetTimeFormat(is24 ? TimeFormat.Clock24h : TimeFormat.Clock12h)
                .SetHour(current.Hours)
                .SetMinute(current.Minutes)
                .SetTitleText(accessibilityLabel ?? string.Empty)
                .Build();
            picker.AddOnPositiveButtonClickListener(new ClickListener(() =>
            {
                binding.UpdateSource(SettingsValueRules.TimeOfDay(new TimeSpan(picker.Hour, picker.Minute, 0)));
                binding.Refresh();
            }));
            picker.Show(manager, "wino_time_picker");
        });
        return WinoSettingsControls.Register(button, WinoSettingsControlKind.Time, () => button.Text);
    }

    /// <summary>A continuous or stepped slider bound to a double property.</summary>
    public Slider Slider<T>(T source, string property, Func<T, double> read, Action<T, double> write, double minimum, double maximum, double step = 0, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var slider = new Slider(_context) { ValueFrom = (float)minimum, ValueTo = (float)maximum, ContentDescription = accessibilityLabel };
        if (step > 0) slider.StepSize = (float)step;
        WinoMaterialAccent.Tint(slider);
        bool applying = false;
        var binding = Scope.Own(new PropertyBinding<T, double>(source, property, read, value =>
        {
            applying = true;
            try
            {
                var clamped = (float)Math.Clamp(value, minimum, maximum);
                if (step > 0) clamped = (float)(minimum + Math.Round((clamped - minimum) / step) * step);
                slider.Value = Math.Clamp(clamped, (float)minimum, (float)maximum);
            }
            finally { applying = false; }
        }, Views.Dispatcher, Views.Error, write));
        var listener = new SliderListener(() =>
        {
            if (applying) return;
            binding.UpdateSource(slider.Value);
        });
        slider.AddOnChangeListener(listener);
        Scope.Own(new ActionDisposable(() => slider.RemoveOnChangeListener(listener)));
        return WinoSettingsControls.Register(slider, WinoSettingsControlKind.Slider,
            () => slider.Value.ToString(step >= 1 ? "0" : "0.##", CultureInfo.CurrentCulture));
    }

    /// <summary>A button bound to a command (enabled while CanExecute).</summary>
    public MaterialButton Button(string title, ICommand command, Func<object?>? parameter = null, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
        => Views.Command(CreateButton(_context, title, outlined: !primary, icon), command, parameter);

    /// <summary>A button that runs an action.</summary>
    public MaterialButton Button(string title, Action action, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
        => Views.Click(CreateButton(_context, title, outlined: !primary, icon), action);

    /// <summary>A secondary-text label bound to a value (the "Clouds", "Per account" values on settings rows).</summary>
    public TextView Label<T, TValue>(T source, string property, Func<T, TValue> read, WinoTextStyle style = WinoTextStyle.Description, int maximumLines = 1)
        where T : INotifyPropertyChanged
    {
        var label = WinoTokens.Style(new TextView(_context), style);
        if (maximumLines > 0) label.SetMaxLines(maximumLines);
        Views.Bind(source, property, read, value => label.Text = value?.ToString() ?? string.Empty);
        return WinoSettingsControls.Register(label, WinoSettingsControlKind.Label);
    }

    /// <summary>Generic one-way binding.</summary>
    public void Bind<T, TValue>(T source, string property, Func<T, TValue> read, Action<TValue> apply)
        where T : INotifyPropertyChanged
        => Views.Bind(source, property, read, apply);

    /// <summary>One-way enabled state for a card, an expander or any control tree.</summary>
    public TView Enabled<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : View where T : INotifyPropertyChanged
    {
        Views.Bind(source, property, read, value => SetEnabled(view, value));
        return view;
    }

    /// <summary>One-way visibility (Gone while false).</summary>
    public TView Visible<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : View where T : INotifyPropertyChanged
        => Views.Visible(view, source, property, read);

    /// <summary>Runs <paramref name="rebuild"/> on the UI thread now and after every collection change.</summary>
    public void Collection(INotifyCollectionChanged collection, Action rebuild) => Views.Collection(collection, () =>
    {
        try { rebuild(); }
        catch (Exception exception) { Views.Error(exception); }
    });

    /// <summary>Enables or disables a card, an expander, or every control in a tree.</summary>
    public static void SetEnabled(View view, bool enabled)
    {
        switch (view)
        {
            case WinoSettingsCard card:
                card.IsEnabled = enabled;
                return;
            case WinoSettingsExpander expander:
                expander.IsEnabled = enabled;
                return;
        }
        view.Enabled = enabled;
        if (view is ViewGroup group)
            for (int index = 0; index < group.ChildCount; index++)
                if (group.GetChildAt(index) is { } child) SetEnabled(child, enabled);
    }

    /// <summary>A filled (primary) or outlined button, 40 dp high with radius 4 (canvas Components "Buttons").</summary>
    public static MaterialButton CreateButton(Context context, string title, bool outlined = true, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = outlined
            ? new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle)
            : new MaterialButton(context);
        button.Text = title;
        button.SetAllCaps(false);
        button.CornerRadius = WinoTokens.Px(context, Resource.Dimension.wino_radius_small);
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_size_control));
        button.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_size_control));
        if (icon != WinoIconGlyph.None)
        {
            button.Icon = WinoIcons.Drawable(context, icon, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m), outlined ? WinoColors.PrimaryText(context) : WinoColors.OnAccent(context));
            // Material tints icons with colorPrimary (the static resource accent); keep the glyph's own colour.
            button.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(outlined ? WinoColors.AccentText(context) : WinoColors.OnAccent(context));
            button.IconPadding = WinoTokens.Px(context, Resource.Dimension.wino_space_xs);
        }
        WinoMaterialAccent.Tint(button, filled: !outlined);
        return button;
    }

    /// <summary>The outlined dropdown button (canvas Components "Dropdown"): text and a trailing chevron.</summary>
    public static MaterialButton CreateDropdown(Context context, int width)
    {
        var button = CreateButton(context, string.Empty, outlined: true);
        button.SetTextColor(WinoColors.PrimaryText(context));
        button.Icon = WinoIcons.Drawable(context, WinoIconGlyph.ChevronDown, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_2xs), WinoColors.SecondaryText(context));
        button.IconGravity = MaterialButton.IconGravityEnd;
        button.SetMaxLines(1);
        button.Ellipsize = TextUtils.TruncateAt.End;
        button.SetMaxWidth(WinoTokens.Px(context, Resource.Dimension.wino_size_dropdown_max));
        if (width != 0) button.SetMinWidth(WinoTokens.Px(context, width));
        return button;
    }

    /// <summary>A 40 dp borderless glyph button.</summary>
    public MaterialButton IconButton(WinoIconGlyph glyph, string label)
    {
        var button = new MaterialButton(_context, null, Resource.Attribute.materialIconButtonStyle)
        {
            Icon = WinoIcons.Drawable(_context, glyph, WinoTokens.Dp(_context, Resource.Dimension.wino_glyph_m), WinoColors.PrimaryText(_context)),
            ContentDescription = label,
            IconPadding = 0,
            IconGravity = MaterialButton.IconGravityTextStart
        };
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetMinWidth(WinoTokens.Px(_context, Resource.Dimension.wino_size_control));
        button.SetMinimumWidth(WinoTokens.Px(_context, Resource.Dimension.wino_size_control));
        return button;
    }

    /// <summary>The pickers open a Material 3 modal bottom sheet titled with the control's accessible name.</summary>
    private void ShowMenu(View anchor, IReadOnlyList<string> items, int selected, Action<int> chosen)
        => WinoChoiceSheet.Show(_context, anchor.ContentDescription, items, selected, index =>
        {
            try { chosen(index); }
            catch (Exception exception) { Views.Error(exception); }
        });

    private static string SafeText<TItem>(Func<TItem, string> text, TItem item)
    {
        try { return text(item) ?? string.Empty; }
        catch { return item?.ToString() ?? string.Empty; }
    }

    private sealed class ClickListener(Action action) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => action();
    }

    private sealed class ToggleListener(Action<int, bool> changed) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => changed(checkedId, isChecked);
    }

    private sealed class SliderListener(Action changed) : Java.Lang.Object, Slider.IOnChangeListener
    {
        public void OnValueChange(Slider slider, float value, bool fromUser)
        {
            if (fromUser) changed();
        }
    }
}

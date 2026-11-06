using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.Settings;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// Creates native settings controls that are already bound to a source through
/// <see cref="PropertyBinding{TSource,TValue}"/> and <see cref="CommandBinding"/> (UIKit twin of the AppKit
/// <c>SettingsBinder</c>, same method names), so settings pages stay declarative. Pop-ups become pull-down menu buttons,
/// checkboxes become check buttons and time pickers compact date pickers. Every subscription is owned by the supplied
/// <see cref="BindingScope"/>; disposing the scope detaches the controls from their sources.
/// </summary>
public sealed class SettingsBinder
{
    private readonly BindingScope _scope;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;

    public SettingsBinder(BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    {
        _scope = scope;
        _dispatcher = dispatcher;
        _error = error;
    }

    public BindingScope Scope => _scope;
    public IDispatcher Dispatcher => _dispatcher;
    public Action<Exception> Error => _error;

    /// <summary>A binder for a child scope, for rows that are rebuilt when a collection changes.</summary>
    public SettingsBinder Child(BindingScope scope) => new(scope, _dispatcher, _error);

    /// <summary>Two-way switch with the Windows On/Off label.</summary>
    public WinoLabeledSwitch Switch<T>(T source, string property, Func<T, bool> read, Action<T, bool> write, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var control = new WinoLabeledSwitch();
        if (accessibilityLabel is not null) WinoAccessibility.Label(control.Switch, accessibilityLabel);
        var binding = Own(new PropertyBinding<T, bool>(source, property, read, value => control.IsOn = value, _dispatcher, _error, write));
        OnActivated(control.Switch, () => { binding.UpdateSource(control.IsOn); binding.Refresh(); }, UIControlEvent.ValueChanged);
        return control;
    }

    /// <summary>Switch that shows a value one way and runs a command when toggled (Windows ToggleButton + Command).</summary>
    public WinoLabeledSwitch CommandSwitch<T>(T source, string property, Func<T, bool> read, ICommand command, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var control = new WinoLabeledSwitch();
        if (accessibilityLabel is not null) WinoAccessibility.Label(control.Switch, accessibilityLabel);
        var binding = Own(new PropertyBinding<T, bool>(source, property, read, value => control.IsOn = value, _dispatcher, _error));
        var commandBinding = Own(new CommandBinding(command, () => null, enabled => control.IsEnabled = enabled, _dispatcher, _error));
        OnActivated(control.Switch, () => { commandBinding.Execute(); binding.Refresh(); }, UIControlEvent.ValueChanged);
        return control;
    }

    /// <summary>Two-way check button (the AppKit checkbox): a round check glyph and the title.</summary>
    public UIButton Checkbox<T>(T source, string title, string property, Func<T, bool> read, Action<T, bool> write)
        where T : INotifyPropertyChanged
    {
        var control = CreateCheckButton(title);
        var binding = Own(new PropertyBinding<T, bool>(source, property, read, value => SetChecked(control, value), _dispatcher, _error, write));
        OnActivated(control, () => { binding.UpdateSource(!control.Selected); binding.Refresh(); });
        return control;
    }

    /// <summary>A pull-down menu button over fixed display strings, bound to an index property.</summary>
    public UIButton PopUp<T>(T source, IReadOnlyList<string> items, string property, Func<T, int> read, Action<T, int> write, double width = 0)
        where T : INotifyPropertyChanged
    {
        var button = CreatePopUp(width);
        PropertyBinding<T, int>? binding = null;
        void Rebuild(int selected)
        {
            var actions = new UIMenuElement[items.Count];
            for (int index = 0; index < items.Count; index++)
            {
                int captured = index;
                var action = UIAction.Create(items[index] ?? string.Empty, null, null, _ => Guard(() =>
                {
                    if (binding is null) return;
                    binding.UpdateSource(captured);
                    binding.Refresh();
                }));
                action.State = index == selected ? UIMenuElementState.On : UIMenuElementState.Off;
                actions[index] = action;
            }
            button.Menu = UIMenu.Create(actions);
            SetTitle(button, selected >= 0 ? items[selected] : string.Empty);
        }
        binding = Own(new PropertyBinding<T, int>(source, property, read,
            value => Rebuild(SettingsValueRules.SelectableIndex(value, items.Count)), _dispatcher, _error, write));
        return button;
    }

    /// <summary>
    /// A pull-down menu button over items with stable identity, bound to a selected-item property. When
    /// <paramref name="itemsProperty"/> is given the menu is rebuilt whenever that property changes.
    /// </summary>
    public UIButton PopUp<T, TItem>(T source, Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write, string? itemsProperty = null, double width = 0,
        INotifyCollectionChanged? itemsCollection = null)
        where T : INotifyPropertyChanged where TItem : class
    {
        var button = CreatePopUp(width);
        var current = new List<TItem>();
        PropertyBinding<T, TItem?>? selection = null;

        void Render(TItem? selected)
        {
            var actions = new UIMenuElement[current.Count];
            for (int index = 0; index < current.Count; index++)
            {
                var item = current[index];
                var action = UIAction.Create(SafeText(text, item), null, null, _ => Guard(() =>
                {
                    if (selection is null) return;
                    selection.UpdateSource(item);
                    selection.Refresh();
                }));
                action.State = selected is not null && Equals(item, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
                actions[index] = action;
            }
            button.Menu = UIMenu.Create(actions);
            var shown = selected is null ? -1 : current.FindIndex(item => Equals(item, selected));
            SetTitle(button, shown >= 0 ? SafeText(text, current[shown]) : string.Empty);
        }

        void Rebuild(IReadOnlyList<TItem>? values)
        {
            current.Clear();
            if (values is not null) current.AddRange(values);
            Render(read(source));
        }

        if (itemsProperty is not null)
            Own(new PropertyBinding<T, IReadOnlyList<TItem>?>(source, itemsProperty, items, Rebuild, _dispatcher, _error));
        else if (itemsCollection is null)
            Rebuild(items(source));
        if (itemsCollection is not null) Collection(itemsCollection, () => Rebuild(items(source)));

        selection = Own(new PropertyBinding<T, TItem?>(source, property, read, Render, _dispatcher, _error,
            (target, value) => { if (value is not null) write(target, value); }));
        return button;
    }

    /// <summary>Numeric field and <see cref="UIStepper"/> with bounds, bound to an integer property.</summary>
    public UIStackView Stepper<T>(T source, string property, Func<T, int> read, Action<T, int> write,
        int minimum, int maximum, int increment = 1, string? suffix = null, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var field = new UITextField
        {
            TextAlignment = UITextAlignment.Right,
            Font = WinoStyle.Body,
            KeyboardType = UIKeyboardType.NumberPad,
            BorderStyle = UITextBorderStyle.RoundedRect,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.WidthAnchor.ConstraintEqualTo(60).Active = true;
        var stepper = new UIStepper
        {
            MinimumValue = minimum,
            MaximumValue = maximum,
            StepValue = increment,
            Wraps = false,
            AutoRepeat = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (accessibilityLabel is not null)
        {
            WinoAccessibility.Label(field, accessibilityLabel);
            WinoAccessibility.Label(stepper, accessibilityLabel);
        }

        var binding = Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            field.Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
            stepper.Value = value;
            WinoSettingsCard.NotifyValueChanged(stepper);
        }, _dispatcher, _error, write));

        void Commit(int value)
        {
            binding.UpdateSource(SettingsValueRules.ClampStepper(value, minimum, maximum));
            binding.Refresh();
        }

        OnActivated(stepper, () => Commit((int)stepper.Value), UIControlEvent.ValueChanged);
        OnActivated(field, () => Commit(SettingsValueRules.ParseStepper(field.Text, read(source), minimum, maximum)), UIControlEvent.EditingDidEnd);

        var stack = WinoLayout.HStack(8, field, stepper);
        if (!string.IsNullOrEmpty(suffix)) stack.AddArrangedSubview(WinoStyle.Label(suffix, WinoStyle.Body, WinoStyle.SecondaryText));
        return stack;
    }

    /// <summary>Segmented control with one selected segment, bound to an index property.</summary>
    public UISegmentedControl Segmented<T>(T source, IReadOnlyList<string> labels, string property, Func<T, int> read, Action<T, int> write)
        where T : INotifyPropertyChanged
    {
        // Segments size to their labels so a long label ("Use system setting") is not cut next to short ones.
        var control = new UISegmentedControl { TranslatesAutoresizingMaskIntoConstraints = false, ApportionsSegmentWidthsByContent = true };
        for (int index = 0; index < labels.Count; index++) control.InsertSegment(labels[index] ?? string.Empty, index, false);
        var binding = Own(new PropertyBinding<T, int>(source, property, read,
            value =>
            {
                control.SelectedSegment = SettingsValueRules.SelectableIndex(value, labels.Count);
                WinoSettingsCard.NotifyValueChanged(control);
            }, _dispatcher, _error, write));
        OnActivated(control, () => { binding.UpdateSource((int)control.SelectedSegment); binding.Refresh(); }, UIControlEvent.ValueChanged);
        return control;
    }

    /// <summary>Two-way editable text field that writes on every edit.</summary>
    public UITextField TextField<T>(T source, string property, Func<T, string?> read, Action<T, string> write, string? placeholder = null, double width = 220)
        where T : INotifyPropertyChanged
    {
        var field = new UITextField
        {
            Placeholder = placeholder ?? string.Empty,
            Font = WinoStyle.Body,
            BorderStyle = UITextBorderStyle.RoundedRect,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        // The preferred width yields when the card puts the field in its own full-width row.
        if (width > 0) { var preferred = field.WidthAnchor.ConstraintEqualTo((nfloat)width); preferred.Priority = 500; preferred.Active = true; }
        var binding = Own(new PropertyBinding<T, string?>(source, property, read, value =>
        {
            var text = value ?? string.Empty;
            if (field.Text != text) field.Text = text;
            WinoSettingsCard.NotifyValueChanged(field);
        }, _dispatcher, _error, (target, value) => write(target, value ?? string.Empty)));
        OnActivated(field, () => binding.UpdateSource(field.Text ?? string.Empty), UIControlEvent.EditingChanged);
        return field;
    }

    /// <summary>Button bound to a command, enabled from CanExecute.</summary>
    public UIButton Button(string title, ICommand command, Func<object?>? parameter = null, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, primary, icon);
        var binding = Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, _dispatcher, _error));
        OnActivated(button, binding.Execute);
        return button;
    }

    /// <summary>Button that runs an action.</summary>
    public UIButton Button(string title, Action action, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, primary, icon);
        OnActivated(button, action);
        return button;
    }

    /// <summary>A button with an optional Wino glyph (icon-only when the title is empty); primary buttons are filled in the accent.</summary>
    public static UIButton CreateButton(string title, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var configuration = primary ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title ?? string.Empty;
        if (primary) configuration.BaseBackgroundColor = WinoStyle.Accent;
        if (icon != WinoIconGlyph.None)
        {
            configuration.Image = WinoIcons.Image(icon, 14, null, title);
            configuration.ImagePadding = 6;
        }
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        return button;
    }

    /// <summary>Hour and minute picker bound to a TimeSpan property (Windows TimePicker).</summary>
    public UIDatePicker TimePicker<T>(T source, string property, Func<T, TimeSpan> read, Action<T, TimeSpan> write, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var picker = new UIDatePicker
        {
            Mode = UIDatePickerMode.Time,
            PreferredDatePickerStyle = UIDatePickerStyle.Compact,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (accessibilityLabel is not null) WinoAccessibility.Label(picker, accessibilityLabel);
        var binding = Own(new PropertyBinding<T, TimeSpan>(source, property, read,
            value =>
            {
                picker.Date = (NSDate)DateTime.SpecifyKind(DateTime.Today.Add(value), DateTimeKind.Local).ToUniversalTime();
                WinoSettingsCard.NotifyValueChanged(picker);
            }, _dispatcher, _error, write));
        OnActivated(picker, () =>
        {
            binding.UpdateSource(SettingsValueRules.TimeOfDay(((DateTime)picker.Date).ToLocalTime().TimeOfDay));
            binding.Refresh();
        }, UIControlEvent.ValueChanged);
        return picker;
    }

    /// <summary>One-way label text.</summary>
    public UILabel Label<T, TValue>(T source, string property, Func<T, TValue> read, UIFont? font = null, UIColor? color = null, int maximumLines = 1)
        where T : INotifyPropertyChanged
    {
        var label = WinoStyle.Label(string.Empty, font, color, maximumLines);
        Own(new PropertyBinding<T, TValue>(source, property, read, value => label.Text = value?.ToString() ?? string.Empty, _dispatcher, _error));
        return label;
    }

    /// <summary>Generic one-way binding.</summary>
    public void Bind<T, TValue>(T source, string property, Func<T, TValue> read, Action<TValue> apply)
        where T : INotifyPropertyChanged
        => Own(new PropertyBinding<T, TValue>(source, property, read, apply, _dispatcher, _error));

    /// <summary>One-way enabled state for a card, an expander or any control tree.</summary>
    public TView Enabled<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : UIView where T : INotifyPropertyChanged
    {
        Own(new PropertyBinding<T, bool>(source, property, read, value => SetEnabled(view, value), _dispatcher, _error));
        return view;
    }

    /// <summary>One-way visibility.</summary>
    public TView Visible<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : UIView where T : INotifyPropertyChanged
    {
        Own(new PropertyBinding<T, bool>(source, property, read, value => view.Hidden = !value, _dispatcher, _error));
        return view;
    }

    /// <summary>Runs <paramref name="rebuild"/> on the UI thread now and after every collection change.</summary>
    public void Collection(INotifyCollectionChanged collection, Action rebuild)
    {
        var disposed = false;
        NotifyCollectionChangedEventHandler handler = async (_, _) =>
        {
            try { await _dispatcher.ExecuteOnUIThread(() => { if (!disposed) rebuild(); }); }
            catch (Exception error) { if (!disposed) _error(error); }
        };
        collection.CollectionChanged += handler;
        Own(new SettingsDisposable(() => { disposed = true; collection.CollectionChanged -= handler; }));
        Guard(rebuild);
    }

    /// <summary>
    /// Subscribes to a control event (the primary action by default: a button tap, Return in a field) and detaches with
    /// the scope. Value controls pass <see cref="UIControlEvent.ValueChanged"/>.
    /// </summary>
    public void OnActivated(UIControl control, Action action, UIControlEvent events = UIControlEvent.PrimaryActionTriggered)
    {
        EventHandler handler = (_, _) => Guard(action);
        control.AddTarget(handler, events);
        Own(new SettingsDisposable(() => control.RemoveTarget(handler, events)));
    }

    /// <summary>Enables or disables a card, an expander, a labeled switch, a control, or every control in a tree.</summary>
    public static void SetEnabled(UIView view, bool enabled)
    {
        switch (view)
        {
            case WinoSettingsCard card:
                card.IsEnabled = enabled;
                return;
            case WinoSettingsExpander expander:
                expander.IsEnabled = enabled;
                return;
            case WinoLabeledSwitch toggle:
                toggle.IsEnabled = enabled;
                return;
            case UIControl control:
                control.Enabled = enabled;
                return;
        }
        foreach (var child in view.Subviews) SetEnabled(child, enabled);
    }

    /// <summary>Enables or disables the controls inside a card's content (not nested cards).</summary>
    internal static void SetControlsEnabled(UIView view, bool enabled)
    {
        switch (view)
        {
            case WinoLabeledSwitch toggle: toggle.IsEnabled = enabled; return;
            case UIControl control: control.Enabled = enabled; return;
        }
        foreach (var child in view.Subviews) SetControlsEnabled(child, enabled);
    }

    private static UIButton CreateCheckButton(string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.ImagePadding = 8;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 0, 6, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, Tag = WinoSettingsRowContent.CheckButtonTag };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        // The glyph shows the checked state; the plain style's tinted "selected" pill would read as a pressed button.
        button.ConfigurationUpdateHandler = target =>
        {
            if (target.Configuration is not { } current) return;
            current.Background.BackgroundColor = UIColor.Clear;
            target.Configuration = current;
        };
        SetChecked(button, false);
        return button;
    }

    private static void SetChecked(UIButton button, bool value)
    {
        button.Selected = value;
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.Image(value ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle, 20, value ? WinoStyle.Accent : WinoStyle.SecondaryText);
        button.Configuration = configuration;
        button.AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    private static void SetTitle(UIButton button, string title)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title;
        button.Configuration = configuration;
        WinoSettingsCard.NotifyValueChanged(button);
    }

    private T Own<T>(T disposable) where T : IDisposable => _scope.Own(disposable);

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { _error(error); }
    }

    private static string SafeText<TItem>(Func<TItem, string> text, TItem item)
    {
        try { return text(item) ?? string.Empty; }
        catch { return item?.ToString() ?? string.Empty; }
    }

    private static UIButton CreatePopUp(double width)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        // Pop-ups keep a minimum width unless the caller asks for a narrower one.
        button.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)(width > 0 ? width : WinoSettingsStyle.PopUpMinWidth)).Active = true;
        return button;
    }
}

/// <summary>Disposes once; used for event detachment inside binding scopes.</summary>
public sealed class SettingsDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

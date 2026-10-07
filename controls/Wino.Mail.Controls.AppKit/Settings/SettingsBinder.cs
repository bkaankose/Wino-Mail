using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// Creates native settings controls that are already bound to a source through
/// <see cref="PropertyBinding{TSource,TValue}"/> and <see cref="CommandBinding"/>, so settings pages stay
/// declarative. Every subscription is owned by the supplied <see cref="BindingScope"/>; disposing the
/// scope detaches the controls from their sources.
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
        OnActivated(control.Switch, () => { binding.UpdateSource(control.IsOn); binding.Refresh(); });
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
        OnActivated(control.Switch, () => { commandBinding.Execute(); binding.Refresh(); });
        return control;
    }

    /// <summary>Two-way checkbox.</summary>
    public NSButton Checkbox<T>(T source, string title, string property, Func<T, bool> read, Action<T, bool> write)
        where T : INotifyPropertyChanged
    {
        var control = WinoCheckbox.Create(title);
        var binding = Own(new PropertyBinding<T, bool>(source, property, read,
            value => control.State = value ? NSCellStateValue.On : NSCellStateValue.Off, _dispatcher, _error, write));
        OnActivated(control, () => { binding.UpdateSource(control.State == NSCellStateValue.On); binding.Refresh(); });
        return control;
    }

    /// <summary>NSPopUpButton over fixed display strings, bound to an index property.</summary>
    public NSPopUpButton PopUp<T>(T source, IReadOnlyList<string> items, string property, Func<T, int> read, Action<T, int> write, double width = 0)
        where T : INotifyPropertyChanged
    {
        var popup = CreatePopUp(width);
        foreach (var item in items) popup.Menu!.AddItem(new NSMenuItem(item ?? string.Empty));
        var binding = Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            if (value >= 0 && value < popup.ItemCount) popup.SelectItem((nint)value);
            else popup.SelectItem((nint)(-1));
        }, _dispatcher, _error, write));
        OnActivated(popup, () => { binding.UpdateSource((int)popup.IndexOfSelectedItem); binding.Refresh(); });
        return popup;
    }

    /// <summary>
    /// NSPopUpButton over a list of items with stable identity, bound to a selected-item property.
    /// When <paramref name="itemsProperty"/> is given the menu is rebuilt whenever that property changes.
    /// </summary>
    public NSPopUpButton PopUp<T, TItem>(T source, Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write, string? itemsProperty = null, double width = 0,
        INotifyCollectionChanged? itemsCollection = null)
        where T : INotifyPropertyChanged where TItem : class
    {
        var popup = CreatePopUp(width);
        var current = new List<TItem>();
        PropertyBinding<T, TItem?>? selection = null;

        void Rebuild(IReadOnlyList<TItem>? values)
        {
            current.Clear();
            popup.Menu!.RemoveAllItems();
            if (values is not null)
            {
                foreach (var value in values)
                {
                    current.Add(value);
                    popup.Menu.AddItem(new NSMenuItem(SafeText(text, value)));
                }
            }
            Select(read(source));
        }

        void Select(TItem? value)
        {
            var index = value is null ? -1 : current.FindIndex(item => Equals(item, value));
            popup.SelectItem((nint)index);
        }

        if (itemsProperty is not null)
            Own(new PropertyBinding<T, IReadOnlyList<TItem>?>(source, itemsProperty, items, Rebuild, _dispatcher, _error));
        else if (itemsCollection is null)
            Rebuild(items(source));
        if (itemsCollection is not null) Collection(itemsCollection, () => Rebuild(items(source)));

        selection = Own(new PropertyBinding<T, TItem?>(source, property, read, Select, _dispatcher, _error,
            (target, value) => { if (value is not null) write(target, value); }));
        OnActivated(popup, () =>
        {
            var index = (int)popup.IndexOfSelectedItem;
            if (index >= 0 && index < current.Count) selection.UpdateSource(current[index]);
            selection.Refresh();
        });
        return popup;
    }

    /// <summary>Numeric field and NSStepper with bounds, bound to an integer property.</summary>
    public NSStackView Stepper<T>(T source, string property, Func<T, int> read, Action<T, int> write,
        int minimum, int maximum, int increment = 1, string? suffix = null, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var field = new NSTextField
        {
            Alignment = NSTextAlignment.Right,
            Font = WinoStyle.Body,
            TranslatesAutoresizingMaskIntoConstraints = false,
            ControlSize = NSControlSize.Small
        };
        field.Cell.SetSendsActionOnEndEditing(true);
        field.WidthAnchor.ConstraintEqualTo(52).Active = true;
        var stepper = new NSStepper
        {
            MinValue = minimum,
            MaxValue = maximum,
            Increment = increment,
            ValueWraps = false,
            Autorepeat = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (accessibilityLabel is not null)
        {
            WinoAccessibility.Label(field, accessibilityLabel);
            WinoAccessibility.Label(stepper, accessibilityLabel);
        }

        var binding = Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            field.IntValue = value;
            stepper.IntValue = value;
        }, _dispatcher, _error, write));

        void Commit(int value)
        {
            binding.UpdateSource(Math.Clamp(value, minimum, maximum));
            binding.Refresh();
        }

        OnActivated(stepper, () => Commit(stepper.IntValue));
        OnActivated(field, () => Commit(int.TryParse(field.StringValue, out var parsed) ? parsed : read(source)));

        var stack = WinoLayout.HStack(4, field, stepper);
        if (!string.IsNullOrEmpty(suffix)) stack.AddArrangedSubview(WinoStyle.Label(suffix, WinoStyle.Body, WinoStyle.SecondaryText));
        return stack;
    }

    /// <summary>Segmented control with one selected segment, bound to an index property.</summary>
    public NSSegmentedControl Segmented<T>(T source, IReadOnlyList<string> labels, string property, Func<T, int> read, Action<T, int> write)
        where T : INotifyPropertyChanged
    {
        var control = new NSSegmentedControl
        {
            SegmentCount = labels.Count,
            TrackingMode = NSSegmentSwitchTracking.SelectOne,
            TranslatesAutoresizingMaskIntoConstraints = false,
            ControlSize = NSControlSize.Small
        };
        for (int index = 0; index < labels.Count; index++) control.SetLabel(labels[index] ?? string.Empty, index);
        var binding = Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            if (value >= 0 && value < labels.Count) control.SelectedSegment = value;
            else control.UnselectAllSegments();
        }, _dispatcher, _error, write));
        OnActivated(control, () => { binding.UpdateSource((int)control.SelectedSegment); binding.Refresh(); });
        return control;
    }

    /// <summary>Two-way editable text field that writes on every edit.</summary>
    public NSTextField TextField<T>(T source, string property, Func<T, string?> read, Action<T, string> write, string? placeholder = null, double width = 220)
        where T : INotifyPropertyChanged
    {
        var field = new NSTextField
        {
            PlaceholderString = placeholder ?? string.Empty,
            Font = WinoStyle.Body,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (width > 0) field.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        var binding = Own(new PropertyBinding<T, string?>(source, property, read, value =>
        {
            var text = value ?? string.Empty;
            if (field.StringValue != text) field.StringValue = text;
        }, _dispatcher, _error, (target, value) => write(target, value ?? string.Empty)));
        EventHandler changed = (_, _) => Guard(() => binding.UpdateSource(field.StringValue));
        field.Changed += changed;
        Own(new SettingsDisposable(() => field.Changed -= changed));
        return field;
    }

    /// <summary>Push button bound to a command, enabled from CanExecute.</summary>
    public NSButton Button(string title, ICommand command, Func<object?>? parameter = null, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, primary, icon);
        var binding = Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, _dispatcher, _error));
        OnActivated(button, binding.Execute);
        return button;
    }

    /// <summary>Push button that runs an action.</summary>
    public NSButton Button(string title, Action action, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, primary, icon);
        OnActivated(button, action);
        return button;
    }

    /// <summary>A push button with an optional Wino glyph (icon-only when the title is empty).</summary>
    public static NSButton CreateButton(string title, bool primary = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = new NSButton
        {
            Title = title ?? string.Empty,
            BezelStyle = NSBezelStyle.Rounded,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (icon != WinoIconGlyph.None)
        {
            button.Image = WinoIcons.Image(icon, 14, accessibilityDescription: title);
            button.ImagePosition = string.IsNullOrEmpty(title) ? NSCellImagePosition.ImageOnly : NSCellImagePosition.ImageLeading;
        }
        if (primary) button.BezelColor = WinoStyle.Accent;
        return button;
    }

    /// <summary>Hour and minute picker bound to a TimeSpan property (Windows TimePicker).</summary>
    public NSDatePicker TimePicker<T>(T source, string property, Func<T, TimeSpan> read, Action<T, TimeSpan> write, string? accessibilityLabel = null)
        where T : INotifyPropertyChanged
    {
        var picker = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.TextFieldAndStepper,
            DatePickerElements = NSDatePickerElementFlags.HourMinute,
            Bezeled = true,
            DrawsBackground = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (accessibilityLabel is not null) WinoAccessibility.Label(picker, accessibilityLabel);
        var binding = Own(new PropertyBinding<T, TimeSpan>(source, property, read,
            value => picker.DateValue = (Foundation.NSDate)DateTime.SpecifyKind(DateTime.Today.Add(value), DateTimeKind.Local).ToUniversalTime(),
            _dispatcher, _error, write));
        OnActivated(picker, () =>
        {
            var time = ((DateTime)picker.DateValue).ToLocalTime().TimeOfDay;
            binding.UpdateSource(new TimeSpan(time.Hours, time.Minutes, 0));
            binding.Refresh();
        });
        return picker;
    }

    /// <summary>One-way label text.</summary>
    public NSTextField Label<T, TValue>(T source, string property, Func<T, TValue> read, NSFont? font = null, NSColor? color = null, int maximumLines = 1)
        where T : INotifyPropertyChanged
    {
        var label = WinoStyle.Label(string.Empty, font, color, maximumLines);
        Own(new PropertyBinding<T, TValue>(source, property, read, value => label.StringValue = value?.ToString() ?? string.Empty, _dispatcher, _error));
        return label;
    }

    /// <summary>Generic one-way binding.</summary>
    public void Bind<T, TValue>(T source, string property, Func<T, TValue> read, Action<TValue> apply)
        where T : INotifyPropertyChanged
        => Own(new PropertyBinding<T, TValue>(source, property, read, apply, _dispatcher, _error));

    /// <summary>One-way enabled state for a card, an expander or any control tree.</summary>
    public TView Enabled<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : NSView where T : INotifyPropertyChanged
    {
        Own(new PropertyBinding<T, bool>(source, property, read, value => SetEnabled(view, value), _dispatcher, _error));
        return view;
    }

    /// <summary>One-way visibility.</summary>
    public TView Visible<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : NSView where T : INotifyPropertyChanged
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

    /// <summary>Subscribes to a control's action and detaches with the scope.</summary>
    public void OnActivated(NSControl control, Action action)
    {
        EventHandler handler = (_, _) => Guard(action);
        control.Activated += handler;
        Own(new SettingsDisposable(() => control.Activated -= handler));
    }

    public static void SetEnabled(NSView view, bool enabled)
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
            case NSControl control:
                control.Enabled = enabled;
                return;
        }
        foreach (var child in view.Subviews) SetEnabled(child, enabled);
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

    private static NSPopUpButton CreatePopUp(double width)
    {
        var popup = new NSPopUpButton(new CoreGraphics.CGRect(0, 0, 160, 24), false)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ControlSize = NSControlSize.Small,
            Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)
        };
        popup.AutoEnablesItems = false;
        // Pop-ups keep the board's minimum width unless the caller asks for a narrower one.
        popup.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)(width > 0 ? width : WinoSettingsStyle.PopUpMinWidth)).Active = true;
        return popup;
    }
}

/// <summary>Disposes once; used for event detachment inside binding scopes.</summary>
public sealed class SettingsDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

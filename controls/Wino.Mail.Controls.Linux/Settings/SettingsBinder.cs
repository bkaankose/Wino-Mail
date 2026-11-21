using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.Settings;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// Builds settings rows already bound to a source (a ViewModel or <c>IPreferencesService</c>): the Linux twin of the
/// AppKit <c>SettingsBinder</c>. Every binding and signal belongs to <see cref="Scope"/>. A two-way row writes the
/// widget's value and then re-reads the source, so a value the source rejects or clamps snaps back in the control.
/// </summary>
public sealed class SettingsBinder(BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
{
    public BindingScope Scope { get; } = scope;

    /// <summary>A binder over a new scope owned by this one, for a section that is rebuilt: dispose its scope before the rebuild.</summary>
    public SettingsBinder Child() => new(Scope.Own(new BindingScope()), dispatcher, error);

    public Adw.SwitchRow Switch<T>(T source, string title, string? description, WinoIconGlyph icon,
        string property, Func<T, bool> read, Action<T, bool> write) where T : INotifyPropertyChanged
    {
        var row = WinoSettingsRows.Switch(title, description, icon);
        TwoWay(row, "active", row.GetActive, row.SetActive, source, property, read, write);
        return row;
    }

    /// <summary>The switch shows the source; toggling it runs <paramref name="command"/>, which is expected to change the source.</summary>
    public Adw.SwitchRow CommandSwitch<T>(T source, string title, string? description, WinoIconGlyph icon,
        string property, Func<T, bool> read, ICommand command) where T : INotifyPropertyChanged
    {
        var row = WinoSettingsRows.Switch(title, description, icon);
        var binding = Scope.Own(new PropertyBinding<T, bool>(source, property, read, row.SetActive, dispatcher, error));
        var commandBinding = Scope.Own(new CommandBinding(command, static () => null, row.SetSensitive, dispatcher, error));
        Scope.Own(GtkSignals.OnNotify(row, "active", () =>
        {
            // Without the comparison the snap-back below would run the command a second time.
            if (row.GetActive() == read(source)) return;
            commandBinding.Execute();
            binding.Refresh();
        }));
        return row;
    }

    public Gtk.Switch SwitchSuffix<T>(T source, string accessibleName, string property, Func<T, bool> read, Action<T, bool> write)
        where T : INotifyPropertyChanged
    {
        var toggle = Gtk.Switch.New();
        toggle.SetValign(Gtk.Align.Center);
        WinoA11y.Label(toggle, accessibleName);
        TwoWay(toggle, "active", toggle.GetActive, toggle.SetActive, source, property, read, write);
        return toggle;
    }

    public Adw.ComboRow Choice<T>(T source, string title, string? description, WinoIconGlyph icon, string[] items,
        string property, Func<T, int> read, Action<T, int> write) where T : INotifyPropertyChanged
    {
        var row = WinoSettingsRows.Combo(title, description, icon);
        row.SetModel(Gtk.StringList.New(items));
        BindIndex(row, row.GetSelected, row.SetSelected, items.Length, source, property, read, write);
        return row;
    }

    public Adw.ComboRow Choice<T, TItem>(T source, string title, string? description, WinoIconGlyph icon,
        Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write,
        string? itemsProperty = null, INotifyCollectionChanged? itemsCollection = null)
        where T : INotifyPropertyChanged where TItem : class
    {
        var row = WinoSettingsRows.Combo(title, description, icon);
        BindItems(row, row.SetModel, row.GetSelected, row.SetSelected, source, items, text, property, read, write, itemsProperty, itemsCollection);
        return row;
    }

    public Gtk.DropDown ChoiceSuffix<T>(T source, string accessibleName, string[] items,
        string property, Func<T, int> read, Action<T, int> write) where T : INotifyPropertyChanged
    {
        var dropDown = DropDown(accessibleName);
        dropDown.SetModel(Gtk.StringList.New(items));
        BindIndex(dropDown, dropDown.GetSelected, dropDown.SetSelected, items.Length, source, property, read, write);
        return dropDown;
    }

    public Gtk.DropDown ChoiceSuffix<T, TItem>(T source, string accessibleName,
        Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write,
        string? itemsProperty = null, INotifyCollectionChanged? itemsCollection = null)
        where T : INotifyPropertyChanged where TItem : class
    {
        var dropDown = DropDown(accessibleName);
        BindItems(dropDown, dropDown.SetModel, dropDown.GetSelected, dropDown.SetSelected, source, items, text, property, read, write, itemsProperty, itemsCollection);
        return dropDown;
    }

    /// <summary>
    /// A row with a spin button suffix named after the title. <c>Adw.SpinRow</c> is not used: GTK 4.22 leaves it out of the
    /// AT-SPI tree, so neither screen readers nor agents could reach the value.
    /// </summary>
    public Adw.ActionRow Number<T>(T source, string title, string? description, WinoIconGlyph icon,
        string property, Func<T, int> read, Action<T, int> write, int minimum, int maximum, int step = 1) where T : INotifyPropertyChanged
    {
        var row = WinoSettingsRows.Action(title, description, icon);
        var spin = WinoSettingsRows.Suffix(row, NumberSuffix(source, title, property, read, write, minimum, maximum, step));
        row.SetActivatableWidget(spin);
        return row;
    }

    public Gtk.SpinButton NumberSuffix<T>(T source, string accessibleName,
        string property, Func<T, int> read, Action<T, int> write, int minimum, int maximum, int step = 1) where T : INotifyPropertyChanged
    {
        var spin = WinoSettingsRows.SpinButton(accessibleName, minimum, maximum, step);
        TwoWay(spin, "value", () => SettingsValueRules.ClampStepper((int)Math.Round(spin.GetValue()), minimum, maximum),
            value => spin.SetValue(value), source, property, read, write);
        return spin;
    }

    /// <summary>Hour and minute spin buttons, always 24-hour (as GNOME Settings); both carry <paramref name="accessibleName"/>.</summary>
    public Gtk.Box TimeSuffix<T>(T source, string accessibleName,
        string property, Func<T, TimeSpan> read, Action<T, TimeSpan> write) where T : INotifyPropertyChanged
    {
        var hours = TimePart(23, accessibleName);
        var minutes = TimePart(59, accessibleName);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        box.SetValign(Gtk.Align.Center);
        box.Append(hours);
        box.Append(Gtk.Label.New(":"));
        box.Append(minutes);

        TimeSpan Value() => SettingsValueRules.TimeOfDay(new TimeSpan((int)Math.Round(hours.GetValue()), (int)Math.Round(minutes.GetValue()), 0));
        void Apply(TimeSpan value)
        {
            hours.SetValue(value.Hours);
            minutes.SetValue(value.Minutes);
        }

        var binding = Scope.Own(new PropertyBinding<T, TimeSpan>(source, property, read, Apply, dispatcher, error, write));
        void Changed()
        {
            binding.UpdateSource(Value());
            binding.Refresh();
        }
        Scope.Own(GtkSignals.OnNotify(hours, "value", Changed));
        Scope.Own(GtkSignals.OnNotify(minutes, "value", Changed));
        return box;
    }

    public Adw.EntryRow Text<T>(T source, string title, string property, Func<T, string?> read, Action<T, string> write)
        where T : INotifyPropertyChanged
    {
        var row = WinoSettingsRows.Entry(title);
        BindText(row, source, property, read, write);
        return row;
    }

    public Gtk.Entry TextSuffix<T>(T source, string accessibleName, string? placeholder,
        string property, Func<T, string?> read, Action<T, string> write, int widthChars = 20) where T : INotifyPropertyChanged
    {
        var entry = Gtk.Entry.New();
        entry.SetValign(Gtk.Align.Center);
        entry.SetWidthChars(widthChars);
        if (!string.IsNullOrEmpty(placeholder)) entry.SetPlaceholderText(placeholder);
        WinoA11y.Label(entry, accessibleName);
        BindText(entry, source, property, read, write);
        return entry;
    }

    public Gtk.Label LabelSuffix<T, TValue>(T source, string property, Func<T, TValue> read) where T : INotifyPropertyChanged
    {
        var label = Gtk.Label.New(string.Empty);
        label.AddCssClass("dim-label");
        label.SetValign(Gtk.Align.Center);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetSingleLineMode(true);
        Bind(source, property, read, value => label.SetText(value?.ToString() ?? string.Empty));
        return label;
    }

    /// <summary>An activatable row whose chevron (or, for <paramref name="external"/>, the Open glyph) button runs the command.</summary>
    public Adw.ActionRow Command(string title, string? description, WinoIconGlyph icon, ICommand command,
        Func<object?>? parameter = null, bool external = false)
    {
        var row = WinoSettingsRows.Action(title, description, icon);
        var button = WinoSettingsRows.Suffix(row, external ? WinoSettingsRows.GlyphButton(WinoIconGlyph.Open, title) : WinoSettingsRows.ChevronButton(title));
        row.SetActivatableWidget(button);
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), row.SetSensitive, dispatcher, error));
        Scope.Own(GtkSignals.OnClicked(button, binding.Execute));
        return row;
    }

    public Adw.ActionRow Navigation(string title, string? description, WinoIconGlyph icon, Action activated)
    {
        var row = WinoSettingsRows.Action(title, description, icon);
        var button = WinoSettingsRows.Suffix(row, WinoSettingsRows.ChevronButton(title));
        row.SetActivatableWidget(button);
        Scope.Own(GtkSignals.OnClicked(button, () => Run(activated)));
        return row;
    }

    public Gtk.Button Button(string title, ICommand command, Func<object?>? parameter = null,
        bool suggested = false, bool destructive = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, suggested, destructive, icon);
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), button.SetSensitive, dispatcher, error));
        Scope.Own(GtkSignals.OnClicked(button, binding.Execute));
        return button;
    }

    public Gtk.Button Button(string title, Action action, bool suggested = false, bool destructive = false, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = CreateButton(title, suggested, destructive, icon);
        Scope.Own(GtkSignals.OnClicked(button, () => Run(action)));
        return button;
    }

    public TWidget Visible<TWidget, T>(TWidget widget, T source, string property, Func<T, bool> read)
        where TWidget : Gtk.Widget where T : INotifyPropertyChanged
    {
        Bind(source, property, read, widget.SetVisible);
        return widget;
    }

    /// <summary>Sensitivity; on a row it dims the title and every suffix together, like <c>IsEnabled</c> on a Windows card.</summary>
    public TWidget Enabled<TWidget, T>(TWidget widget, T source, string property, Func<T, bool> read)
        where TWidget : Gtk.Widget where T : INotifyPropertyChanged
    {
        Bind(source, property, read, widget.SetSensitive);
        return widget;
    }

    public TRow Subtitle<TRow, T>(TRow row, T source, string property, Func<T, string?> read)
        where TRow : Adw.ActionRow where T : INotifyPropertyChanged
    {
        Bind(source, property, read, value => row.SetSubtitle(value ?? string.Empty));
        return row;
    }

    public Adw.ExpanderRow Subtitle<T>(Adw.ExpanderRow row, T source, string property, Func<T, string?> read) where T : INotifyPropertyChanged
    {
        Bind(source, property, read, value => row.SetSubtitle(value ?? string.Empty));
        return row;
    }

    public Adw.EntryRow Invalid<T>(Adw.EntryRow row, T source, string property, Func<T, bool> read) where T : INotifyPropertyChanged
    {
        Bind(source, property, read, invalid =>
        {
            if (invalid) row.AddCssClass("error");
            else row.RemoveCssClass("error");
        });
        return row;
    }

    public void Bind<T, TValue>(T source, string property, Func<T, TValue> read, Action<TValue> apply) where T : INotifyPropertyChanged
        => Scope.Own(new PropertyBinding<T, TValue>(source, property, read, apply, dispatcher, error));

    /// <summary>Runs <paramref name="rebuild"/> now and once per main-loop turn after the collection changed.</summary>
    public void Collection(INotifyCollectionChanged collection, Action rebuild)
    {
        void Refresh() => Run(rebuild);

        var coalescer = Scope.Own(new MainLoop.Coalescer(Refresh));
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = dispatcher.ExecuteOnUIThread(coalescer.Request);
        collection.CollectionChanged += handler;
        Scope.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        Refresh();
    }

    public void Own(IDisposable disposable) => Scope.Own(disposable);

    private void TwoWay<T, TValue>(GObject.Object widget, string notify, Func<TValue> value, Action<TValue> apply,
        T source, string property, Func<T, TValue> read, Action<T, TValue> write) where T : INotifyPropertyChanged
    {
        var binding = Scope.Own(new PropertyBinding<T, TValue>(source, property, read, apply, dispatcher, error, write));
        Scope.Own(GtkSignals.OnNotify(widget, notify, () =>
        {
            binding.UpdateSource(value());
            binding.Refresh();
        }));
    }

    private void BindIndex<T>(GObject.Object widget, Func<uint> selected, Action<uint> select, int count,
        T source, string property, Func<T, int> read, Action<T, int> write) where T : INotifyPropertyChanged
    {
        var binding = Scope.Own(new PropertyBinding<T, int>(source, property, read, value =>
        {
            var index = SettingsValueRules.SelectableIndex(value, count);
            select(index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index);
        }, dispatcher, error, write));
        Scope.Own(GtkSignals.OnNotify(widget, "selected", () =>
        {
            var index = selected();
            if (index >= count) return;
            binding.UpdateSource((int)index);
            binding.Refresh();
        }));
    }

    private void BindItems<T, TItem>(GObject.Object widget, Action<Gio.ListModel> setModel, Func<uint> selected, Action<uint> select,
        T source, Func<T, IReadOnlyList<TItem>?> items, Func<TItem, string> text,
        string property, Func<T, TItem?> read, Action<T, TItem> write,
        string? itemsProperty, INotifyCollectionChanged? itemsCollection)
        where T : INotifyPropertyChanged where TItem : class
    {
        var state = new ItemChoiceState<TItem>();

        void Apply(TItem? value)
        {
            var index = value is null ? -1 : state.Items.FindIndex(item => Equals(item, value));
            select(index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index);
        }

        // Replacing the model selects position 0 and notifies; the flag keeps that from writing the first item to the source.
        void Rebuild(IReadOnlyList<TItem>? values)
        {
            state.Rebuilding = true;
            try
            {
                state.Items.Clear();
                if (values is not null) state.Items.AddRange(values);
                var strings = new string[state.Items.Count];
                for (var index = 0; index < strings.Length; index++) strings[index] = SafeText(text, state.Items[index]);
                setModel(Gtk.StringList.New(strings));
                Apply(read(source));
            }
            finally { state.Rebuilding = false; }
        }

        if (itemsProperty is not null)
            Scope.Own(new PropertyBinding<T, IReadOnlyList<TItem>?>(source, itemsProperty, items, Rebuild, dispatcher, error));
        else if (itemsCollection is null)
            Rebuild(items(source));

        var binding = Scope.Own(new PropertyBinding<T, TItem?>(source, property, read, Apply, dispatcher, error,
            (target, value) =>
            {
                if (value is not null) write(target, value);
            }));
        Scope.Own(GtkSignals.OnNotify(widget, "selected", () =>
        {
            if (state.Rebuilding) return;
            var index = selected();
            if (index >= state.Items.Count) return;
            binding.UpdateSource(state.Items[(int)index]);
            binding.Refresh();
        }));

        if (itemsCollection is not null) Collection(itemsCollection, () => Rebuild(items(source)));
    }

    private void BindText<T>(Gtk.Editable editable, T source, string property, Func<T, string?> read, Action<T, string> write)
        where T : INotifyPropertyChanged
    {
        void Apply(string? value)
        {
            // A redundant SetText moves the cursor to the end.
            var text = value ?? string.Empty;
            if (editable.GetText() != text) editable.SetText(text);
        }

        var binding = Scope.Own(new PropertyBinding<T, string?>(source, property, read, Apply, dispatcher, error,
            (target, value) => write(target, value ?? string.Empty)));
        Scope.Own(GtkSignals.OnChanged(editable, () => binding.UpdateSource(editable.GetText())));
    }

    private Gtk.DropDown DropDown(string accessibleName)
    {
        var dropDown = Gtk.DropDown.NewFromStrings([]);
        dropDown.SetValign(Gtk.Align.Center);
        dropDown.SetEnableSearch(false);
        // GTK labels a drop-down by its selected item (labelled-by beats the label property); the setting's name has to win.
        void Name()
        {
            dropDown.ResetRelation(Gtk.AccessibleRelation.LabelledBy);
            WinoA11y.Label(dropDown, accessibleName);
        }
        Name();
        Scope.Own(GtkSignals.OnNotify(dropDown, "selected", Name));
        return dropDown;
    }

    private static Gtk.SpinButton TimePart(int maximum, string accessibleName)
    {
        var spin = Gtk.SpinButton.NewWithRange(0, maximum, 1);
        spin.SetDigits(0);
        spin.SetNumeric(true);
        spin.SetWrap(true);
        spin.SetWidthChars(2);
        WinoA11y.Label(spin, accessibleName);
        return spin;
    }

    private static Gtk.Button CreateButton(string title, bool suggested, bool destructive, WinoIconGlyph icon)
    {
        Gtk.Button button;
        if (icon == WinoIconGlyph.None) button = Gtk.Button.NewWithLabel(title);
        else
        {
            button = Gtk.Button.New();
            button.SetChild(WinoIcons.ButtonContent(icon, title));
            WinoA11y.Label(button, title);
        }
        button.SetValign(Gtk.Align.Center);
        if (suggested) button.AddCssClass("suggested-action");
        if (destructive) button.AddCssClass("destructive-action");
        return button;
    }

    private static string SafeText<TItem>(Func<TItem, string> text, TItem item) where TItem : class
    {
        try { return text(item) ?? string.Empty; }
        catch (Exception) { return item.ToString() ?? string.Empty; }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception) { error(exception); }
    }

    private sealed class ItemChoiceState<TItem>
    {
        public readonly List<TItem> Items = [];
        public bool Rebuilding;
    }
}

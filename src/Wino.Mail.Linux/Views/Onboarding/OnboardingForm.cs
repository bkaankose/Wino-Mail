using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Widget factories shared by the setup pages (the GTK twin of the macOS <c>AccountSetupUi</c> and <c>OnboardingForm</c>).</summary>
internal static class OnboardingUi
{
    public static Gtk.Label Title(string? text = null)
    {
        var label = Wrapping(text);
        label.AddCssClass("title-1");
        label.SetJustify(Gtk.Justification.Center);
        label.SetXalign(0.5f);
        return label;
    }

    public static Gtk.Label Heading(string? text = null)
    {
        var label = Wrapping(text);
        label.AddCssClass("heading");
        return label;
    }

    public static Gtk.Label Subtitle(string? text = null)
    {
        var label = Wrapping(text);
        label.AddCssClass("dim-label");
        label.SetJustify(Gtk.Justification.Center);
        label.SetXalign(0.5f);
        return label;
    }

    public static Gtk.Label Body(string? text = null) => Wrapping(text);

    public static Gtk.Label Caption(string? text = null)
    {
        var label = Wrapping(text);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        return label;
    }

    public static Gtk.Label Error(string? text = null)
    {
        var label = Wrapping(text);
        label.AddCssClass("caption");
        label.AddCssClass("error");
        return label;
    }

    public static Gtk.Label Wrapping(string? text)
    {
        var label = Gtk.Label.New(text ?? string.Empty);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.SetXalign(0);
        return label;
    }

    public static Gtk.Box Column(int spacing, params Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, spacing);
        foreach (var child in children) box.Append(child);
        return box;
    }

    /// <summary>A row of buttons at the leading edge (or centred) that keeps the buttons' natural width.</summary>
    public static Gtk.Box Buttons(params Gtk.Widget[] children) => Row(Gtk.Align.Start, children);

    public static Gtk.Box Centered(params Gtk.Widget[] children) => Row(Gtk.Align.Center, children);

    private static Gtk.Box Row(Gtk.Align align, Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        box.SetHalign(align);
        foreach (var child in children) box.Append(child);
        return box;
    }

    public static Adw.PreferencesGroup Group(string? title = null, string? description = null)
    {
        var group = Adw.PreferencesGroup.New();
        if (!string.IsNullOrEmpty(title)) group.SetTitle(System.Security.SecurityElement.Escape(title));
        if (!string.IsNullOrEmpty(description)) group.SetDescription(System.Security.SecurityElement.Escape(description));
        return group;
    }

    public static Adw.Spinner Spinner()
    {
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(16, 16);
        spinner.SetValign(Gtk.Align.Center);
        return spinner;
    }

    public static Gtk.Button Button(string text, bool suggested = false)
    {
        var button = Gtk.Button.NewWithLabel(text);
        if (suggested) button.AddCssClass("suggested-action");
        return button;
    }

    /// <summary>A flat accent text button; it runs a command, so it is not a <c>Gtk.LinkButton</c>.</summary>
    public static Gtk.Button Link(string text)
    {
        var button = Gtk.Button.NewWithLabel(text);
        button.AddCssClass("flat");
        button.AddCssClass("link");
        button.SetHalign(Gtk.Align.Start);
        return button;
    }

    public static void SetLabel(Gtk.Button button, string? text)
    {
        var value = text ?? string.Empty;
        if (button.GetLabel() != value) button.SetLabel(value);
    }

    public static void SetText(Gtk.Label label, string? text)
    {
        var value = text ?? string.Empty;
        if (label.GetText() != value) label.SetText(value);
    }

    public static void SetVisible(Gtk.Widget widget, bool visible)
    {
        if (widget.GetVisible() != visible) widget.SetVisible(visible);
    }

    public static void SetSensitive(Gtk.Widget widget, bool sensitive)
    {
        if (widget.GetSensitive() != sensitive) widget.SetSensitive(sensitive);
    }

    public static WinoInfoBarSeverity ToSeverity(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational,
    };

    /// <summary>The page's own header bar is shown only where no window header is above it (the main window before the shell).</summary>
    public static bool HasWindowHeader(Gtk.Widget widget)
    {
        for (var parent = widget.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is Adw.ToolbarView) return true;
        return false;
    }
}

/// <summary>
/// The refresh model of the setup pages (macOS <c>QueueRefresh</c>/<c>Refresh</c>): the shared ViewModels raise dozens of
/// computed properties per state change, so the page re-reads its whole state once per main-loop turn through a list of
/// appliers instead of binding each property. Text entries, switches and drop-downs are the exception: they are bound
/// two-way through <see cref="Bind"/> and <see cref="Rows"/>, so refreshing never moves a caret.
/// </summary>
internal sealed class OnboardingForm<T> where T : class, INotifyPropertyChanged
{
    private readonly List<Action> _appliers = [];
    private readonly MainLoop.Coalescer _coalescer;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;

    public OnboardingForm(T source, BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    {
        Source = source;
        Scope = scope;
        _dispatcher = dispatcher;
        _error = error;
        Bind = new GtkBinder<T>(source, scope, dispatcher, error);
        Rows = new SettingsBinder(scope, dispatcher, error);
        _coalescer = scope.Own(new MainLoop.Coalescer(Refresh));
        Track(source);
    }

    public T Source { get; }
    public BindingScope Scope { get; }
    public GtkBinder<T> Bind { get; }
    public SettingsBinder Rows { get; }

    /// <summary>Runs after every refresh; the page uses it for state that is not a widget property (the shown step).</summary>
    public event Action? Refreshed;

    public void Track(INotifyPropertyChanged other)
    {
        PropertyChangedEventHandler changed = (_, _) => Request();
        other.PropertyChanged += changed;
        Scope.Own(new ActionDisposable(() => other.PropertyChanged -= changed));
    }

    public void TrackCollection(INotifyCollectionChanged collection)
    {
        NotifyCollectionChangedEventHandler changed = (_, _) => Request();
        collection.CollectionChanged += changed;
        Scope.Own(new ActionDisposable(() => collection.CollectionChanged -= changed));
    }

    public void Request()
    {
        if (Scope.IsDisposed) return;
        Observe(_dispatcher.ExecuteOnUIThread(() =>
        {
            if (!Scope.IsDisposed) _coalescer.Request();
        }));
    }

    public void Refresh()
    {
        if (Scope.IsDisposed) return;
        foreach (var apply in _appliers)
        {
            try { apply(); }
            catch (Exception exception) { _error(exception); }
        }
        try { Refreshed?.Invoke(); }
        catch (Exception exception) { _error(exception); }
    }

    public void Apply(Action apply) => _appliers.Add(apply);

    public TWidget When<TWidget>(TWidget widget, Func<T, bool> visible) where TWidget : Gtk.Widget
    {
        _appliers.Add(() => OnboardingUi.SetVisible(widget, visible(Source)));
        return widget;
    }

    public TWidget Enabled<TWidget>(TWidget widget, Func<T, bool> enabled) where TWidget : Gtk.Widget
    {
        _appliers.Add(() => OnboardingUi.SetSensitive(widget, enabled(Source)));
        return widget;
    }

    public Gtk.Label Text(Gtk.Label label, Func<T, string?> read)
    {
        _appliers.Add(() => OnboardingUi.SetText(label, read(Source)));
        return label;
    }

    /// <summary>A label that hides itself while its text is empty.</summary>
    public Gtk.Label Optional(Gtk.Label label, Func<T, string?> read)
    {
        _appliers.Add(() =>
        {
            var text = read(Source);
            OnboardingUi.SetText(label, text);
            OnboardingUi.SetVisible(label, !string.IsNullOrWhiteSpace(text));
        });
        return label;
    }

    public Gtk.Button Command(Gtk.Button button, ICommand command, Func<object?>? parameter = null)
    {
        Bind.BindCommand(button, command, parameter);
        return button;
    }

    public Gtk.Button Button(string text, ICommand command, bool suggested = false, Func<object?>? parameter = null)
        => Command(OnboardingUi.Button(text, suggested), command, parameter);

    public Gtk.Button Link(string text, ICommand command, Func<object?>? parameter = null)
        => Command(OnboardingUi.Link(text), command, parameter);

    public Adw.EntryRow Entry(string title, string property, Func<T, string?> read, Action<T, string> write, Gtk.InputPurpose purpose = Gtk.InputPurpose.FreeForm)
    {
        var row = WinoSettingsRows.Entry(title);
        row.SetInputPurpose(purpose);
        Bind.BindText(row, property, read, write);
        return row;
    }

    public Adw.PasswordEntryRow Password(string title, string property, Func<T, string?> read, Action<T, string> write)
    {
        var row = WinoSettingsRows.Password(title);
        Bind.BindText(row, property, read, write);
        return row;
    }

    public Adw.SwitchRow Switch(string title, string? description, string property, Func<T, bool> read, Action<T, bool> write)
        => Rows.Switch(Source, title, description, WinoIconGlyph.None, property, read, write);

    public Gtk.CheckButton Check(string label, string property, Func<T, bool> read, Action<T, bool> write)
    {
        var check = Gtk.CheckButton.NewWithLabel(label);
        Bind.BindActive(check, property, read, write);
        return check;
    }

    /// <summary>A drop-down row over a list the ViewModel may replace; the selection is read and written as an index.</summary>
    public Adw.ComboRow Combo(string title, Func<T, IReadOnlyList<string>> items, string property, Func<T, int> read, Action<T, int> write)
    {
        var row = WinoSettingsRows.Combo(title);
        var applying = false;
        string[] shown = [];
        void Apply()
        {
            var current = items(Source);
            applying = true;
            try
            {
                if (!current.SequenceEqual(shown))
                {
                    shown = [.. current];
                    row.SetModel(Gtk.StringList.New(shown));
                }
                var index = read(Source);
                var position = index >= 0 && index < shown.Length ? (uint)index : Gtk.Constants.INVALID_LIST_POSITION;
                if (row.GetSelected() != position) row.SetSelected(position);
            }
            finally { applying = false; }
        }
        _appliers.Add(Apply);
        Scope.Own(GtkSignals.OnNotify(row, "selected", () =>
        {
            if (applying) return;
            var selected = row.GetSelected();
            if (selected < shown.Length && selected != read(Source)) write(Source, (int)selected);
        }));
        Apply();
        return row;
    }

    /// <summary>Enter in an entry row runs <paramref name="command"/> (an <c>Adw.EntryRow</c> raises <c>entry-activated</c>).</summary>
    public void OnEnter(Adw.EntryRow row, ICommand command)
    {
        row.SetActivatesDefault(false);
        Scope.Own(GtkSignals.Connect<Adw.EntryRow>(h => row.OnEntryActivated += h, h => row.OnEntryActivated -= h, (_, _) => Run(command)));
    }

    public void Run(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter)) return;
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand) Observe(asyncCommand.ExecuteAsync(parameter));
        else command.Execute(parameter);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { if (!Scope.IsDisposed) _error(exception); }
    }
}

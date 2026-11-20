using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Bindings;

namespace Wino.Presentation.Linux.Bindings;

/// <summary>
/// Binds GTK widgets to one source. Every binding and every signal handler belongs to <see cref="Scope"/>, so disposing
/// the scope disconnects all of them. Two-way bindings rely on <see cref="PropertyBinding{TSource, TValue}"/> ignoring
/// writes while it applies, which holds because the GTK dispatcher runs main-thread work inline.
/// </summary>
public sealed class GtkBinder<TSource>(TSource source, BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
    where TSource : INotifyPropertyChanged
{
    public TSource Source { get; } = source;
    public BindingScope Scope { get; } = scope;

    public GtkBinder<TOther> For<TOther>(TOther other) where TOther : INotifyPropertyChanged
        => new(other, Scope, dispatcher, error);

    /// <summary>A binder over a new scope owned by this one, for a section that is rebuilt: dispose its scope before the rebuild.</summary>
    public GtkBinder<TSource> Child() => new(Source, Scope.Own(new BindingScope()), dispatcher, error);

    public void Bind<TValue>(string property, Func<TSource, TValue> read, Action<TValue> apply)
        => Scope.Own(new PropertyBinding<TSource, TValue>(Source, property, read, apply, dispatcher, error));

    public void BindAny<TValue>(Func<TSource, TValue> read, Action<TValue> apply)
        => Scope.Own(new AnyPropertyBinding<TSource, TValue>(Source, read, apply, dispatcher, error));

    /// <summary>Runs <paramref name="refresh"/> now and once per main-loop turn after the collection changed.</summary>
    public void BindCollection(INotifyCollectionChanged collection, Action refresh)
    {
        void Refresh()
        {
            try { refresh(); }
            catch (Exception exception) { error(exception); }
        }

        var coalescer = Scope.Own(new MainLoop.Coalescer(Refresh));
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = dispatcher.ExecuteOnUIThread(coalescer.Request);
        collection.CollectionChanged += handler;
        Scope.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        Refresh();
    }

    public void BindText(Gtk.Label label, string property, Func<TSource, string?> read)
        => Bind(property, read, value => label.SetText(value ?? string.Empty));

    public void BindText(Gtk.Editable editable, string property, Func<TSource, string?> read, Action<TSource, string>? write = null)
    {
        void Apply(string? value)
        {
            // A redundant SetText moves the cursor to the end.
            var text = value ?? string.Empty;
            if (editable.GetText() != text) editable.SetText(text);
        }

        if (write is null)
        {
            Bind(property, read, Apply);
            return;
        }

        var binding = Scope.Own(new PropertyBinding<TSource, string?>(Source, property, read, Apply, dispatcher, error,
            (target, value) => write(target, value ?? string.Empty)));
        GObject.SignalHandler<Gtk.Editable> handler = (_, _) => binding.UpdateSource(editable.GetText());
        editable.OnChanged += handler;
        Scope.Own(new ActionDisposable(() => editable.OnChanged -= handler));
    }

    public void BindVisible(Gtk.Widget widget, string property, Func<TSource, bool> read)
        => Bind(property, read, widget.SetVisible);

    public void BindSensitive(Gtk.Widget widget, string property, Func<TSource, bool> read)
        => Bind(property, read, widget.SetSensitive);

    public void BindCssClass(Gtk.Widget widget, string cssClass, string property, Func<TSource, bool> read)
        => Bind(property, read, on =>
        {
            if (on) widget.AddCssClass(cssClass);
            else widget.RemoveCssClass(cssClass);
        });

    public void BindActive(Gtk.Switch toggle, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var binding = Scope.Own(new PropertyBinding<TSource, bool>(Source, property, read, toggle.SetActive, dispatcher, error, write));
        Scope.Own(GtkSignals.OnNotify(toggle, "active", () => binding.UpdateSource(toggle.GetActive())));
    }

    public void BindActive(Gtk.CheckButton toggle, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var binding = Scope.Own(new PropertyBinding<TSource, bool>(Source, property, read, toggle.SetActive, dispatcher, error, write));
        GObject.SignalHandler<Gtk.CheckButton> handler = (_, _) => binding.UpdateSource(toggle.GetActive());
        toggle.OnToggled += handler;
        Scope.Own(new ActionDisposable(() => toggle.OnToggled -= handler));
    }

    public void BindActive(Gtk.ToggleButton toggle, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var binding = Scope.Own(new PropertyBinding<TSource, bool>(Source, property, read, toggle.SetActive, dispatcher, error, write));
        GObject.SignalHandler<Gtk.ToggleButton> handler = (_, _) => binding.UpdateSource(toggle.GetActive());
        toggle.OnToggled += handler;
        Scope.Own(new ActionDisposable(() => toggle.OnToggled -= handler));
    }

    public void BindCommand(Gtk.Button button, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), button.SetSensitive, dispatcher, error));
        GObject.SignalHandler<Gtk.Button> handler = (_, _) => binding.Execute();
        button.OnClicked += handler;
        Scope.Own(new ActionDisposable(() => button.OnClicked -= handler));
    }

    public void BindCommand(Gio.SimpleAction action, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), action.SetEnabled, dispatcher, error));
        GObject.SignalHandler<Gio.SimpleAction, Gio.SimpleAction.ActivateSignalArgs> handler = (_, _) => binding.Execute();
        action.OnActivate += handler;
        Scope.Own(new ActionDisposable(() => action.OnActivate -= handler));
    }

    /// <summary><paramref name="ids"/> holds the id of each model position in order; the caller builds the model.</summary>
    public void BindSelected<TId>(Gtk.DropDown dropDown, TId[] ids, string property, Func<TSource, TId> read, Action<TSource, TId> write)
        => BindSelected(dropDown, dropDown.GetSelected, dropDown.SetSelected, ids, property, read, write);

    /// <summary><paramref name="ids"/> holds the id of each model position in order; the caller builds the model.</summary>
    public void BindSelected<TId>(Adw.ComboRow row, TId[] ids, string property, Func<TSource, TId> read, Action<TSource, TId> write)
        => BindSelected(row, row.GetSelected, row.SetSelected, ids, property, read, write);

    private void BindSelected<TId>(GObject.Object widget, Func<uint> selected, Action<uint> select, TId[] ids, string property,
        Func<TSource, TId> read, Action<TSource, TId> write)
    {
        var binding = Scope.Own(new PropertyBinding<TSource, TId>(Source, property, read, value =>
        {
            var index = Array.IndexOf(ids, value);
            select(index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index);
        }, dispatcher, error, write));
        Scope.Own(GtkSignals.OnNotify(widget, "selected", () =>
        {
            var index = selected();
            if (index < ids.Length) binding.UpdateSource(ids[index]);
        }));
    }
}

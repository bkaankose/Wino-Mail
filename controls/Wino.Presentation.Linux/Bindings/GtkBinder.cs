using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Bindings;

namespace Wino.Presentation.Linux.Bindings;

/// <summary>
/// Binds GTK widgets to one source. Every binding and every signal handler belongs to <see cref="Scope"/>, so disposing
/// the scope disconnects all of them; signal handlers go through <see cref="GtkSignals"/>, so a disposed binding no
/// longer reaches its source or its view. Two-way bindings rely on <see cref="PropertyBinding{TSource, TValue}"/> ignoring
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

    /// <param name="coalesceWrites">
    /// Writes the text once per main-loop turn. GTK reports replacing a text (select all and type, or SetText) as a delete
    /// and an insert, so the source sees an empty value in between; sources that derive other fields from the previous
    /// value (a shared password copied to every server) need the one change.
    /// </param>
    public void BindText(Gtk.Editable editable, string property, Func<TSource, string?> read, Action<TSource, string>? write = null,
        bool coalesceWrites = false)
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
        if (!coalesceWrites)
        {
            Scope.Own(GtkSignals.OnChanged(editable, () => binding.UpdateSource(editable.GetText())));
            return;
        }
        // A high-priority idle runs before the next input event, so a key that submits the form never reads a stale value.
        uint source = 0;
        var released = false;
        Scope.Own(new ActionDisposable(() =>
        {
            released = true;
            if (source != 0) GLib.Functions.SourceRemove(source);
            source = 0;
        }));
        Scope.Own(GtkSignals.OnChanged(editable, () =>
        {
            if (source != 0) return;
            source = GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_HIGH, () =>
            {
                source = 0;
                if (!released) binding.UpdateSource(editable.GetText());
                return false;
            });
        }));
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
        Scope.Own(GtkSignals.OnToggled(toggle, () => binding.UpdateSource(toggle.GetActive())));
    }

    public void BindActive(Gtk.ToggleButton toggle, string property, Func<TSource, bool> read, Action<TSource, bool> write)
    {
        var binding = Scope.Own(new PropertyBinding<TSource, bool>(Source, property, read, toggle.SetActive, dispatcher, error, write));
        Scope.Own(GtkSignals.OnToggled(toggle, () => binding.UpdateSource(toggle.GetActive())));
    }

    public void BindCommand(Gtk.Button button, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), button.SetSensitive, dispatcher, error));
        Scope.Own(GtkSignals.OnClicked(button, binding.Execute));
    }

    public void BindCommand(Gio.SimpleAction action, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (static () => null), action.SetEnabled, dispatcher, error));
        Scope.Own(GtkSignals.OnActivate(action, _ => binding.Execute()));
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

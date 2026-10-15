using System.ComponentModel;
using System.Windows.Input;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Bindings;

namespace Wino.Presentation.Android.Bindings;

/// <summary>
/// Binds Android views to a source through the shared <see cref="PropertyBinding{TSource,TValue}"/> and
/// <see cref="CommandBinding"/>. Every binding and event handler is owned by <see cref="Scope"/>: disposing the scope
/// (see <see cref="BindingLifetime"/>) detaches the views from their sources and rejects queued UI callbacks.
/// </summary>
public sealed class ViewBinder(BindingScope scope, IDispatcher dispatcher, Action<Exception> error)
{
    public BindingScope Scope { get; } = scope;
    public IDispatcher Dispatcher { get; } = dispatcher;
    public Action<Exception> Error { get; } = error;

    /// <summary>A binder over another scope (rows rebuilt when a collection changes).</summary>
    public ViewBinder Child(BindingScope scope) => new(scope, Dispatcher, Error);

    /// <summary>Applies the property now and whenever it changes.</summary>
    public PropertyBinding<T, TValue> Bind<T, TValue>(T source, string property, Func<T, TValue> read, Action<TValue> apply)
        where T : INotifyPropertyChanged
        => Scope.Own(new PropertyBinding<T, TValue>(source, property, read, apply, Dispatcher, Error));

    /// <summary>Re-reads a computed value after any change of the source.</summary>
    public void BindAny<T, TValue>(T source, Func<T, TValue> read, Action<TValue> apply) where T : INotifyPropertyChanged
        => Scope.Own(new AnyPropertyBinding<T, TValue>(source, read, apply, Dispatcher, Error));

    public TextView Text<T>(TextView view, T source, string property, Func<T, string?> read) where T : INotifyPropertyChanged
    {
        Bind(source, property, read, value => view.Text = value ?? string.Empty);
        return view;
    }

    /// <summary>Visible (or Gone) while the property is true.</summary>
    public TView Visible<TView, T>(TView view, T source, string property, Func<T, bool> read, bool collapse = true)
        where TView : View where T : INotifyPropertyChanged
    {
        Bind(source, property, read, visible => view.Visibility = visible ? ViewStates.Visible : collapse ? ViewStates.Gone : ViewStates.Invisible);
        return view;
    }

    public TView Enabled<TView, T>(TView view, T source, string property, Func<T, bool> read)
        where TView : View where T : INotifyPropertyChanged
    {
        Bind(source, property, read, enabled => view.Enabled = enabled);
        return view;
    }

    public ImageView Image<T>(ImageView view, T source, string property, Func<T, Bitmap?> read) where T : INotifyPropertyChanged
    {
        Bind(source, property, read, view.SetImageBitmap);
        return view;
    }

    /// <summary>Two-way checked state for a CheckBox, Switch or MaterialSwitch.</summary>
    public TButton Checked<TButton, T>(TButton button, T source, string property, Func<T, bool> read, Action<T, bool> write)
        where TButton : CompoundButton where T : INotifyPropertyChanged
    {
        var binding = Scope.Own(new PropertyBinding<T, bool>(source, property, read, value => { if (button.Checked != value) button.Checked = value; }, Dispatcher, Error, write));
        EventHandler<CompoundButton.CheckedChangeEventArgs> handler = (_, args) =>
        {
            binding.UpdateSource(args.IsChecked);
            // A source that rejects the value (validation, pending command) puts the switch back.
            binding.Refresh();
        };
        button.CheckedChange += handler;
        Scope.Own(new ActionDisposable(() => button.CheckedChange -= handler));
        return button;
    }

    /// <summary>Two-way text for an EditText; the source is written on every edit.</summary>
    public EditText TwoWayText<T>(EditText view, T source, string property, Func<T, string?> read, Action<T, string> write)
        where T : INotifyPropertyChanged
    {
        var binding = Scope.Own(new PropertyBinding<T, string?>(source, property, read, value =>
        {
            if (view.Text != (value ?? string.Empty)) view.Text = value ?? string.Empty;
        }, Dispatcher, Error, (target, value) => write(target, value ?? string.Empty)));
        EventHandler<global::Android.Text.TextChangedEventArgs> handler = (_, _) => binding.UpdateSource(view.Text ?? string.Empty);
        view.TextChanged += handler;
        Scope.Own(new ActionDisposable(() => view.TextChanged -= handler));
        return view;
    }

    /// <summary>Runs <paramref name="command"/> on click; the view is enabled while CanExecute is true.</summary>
    public TView Command<TView>(TView view, ICommand command, Func<object?>? parameter = null) where TView : View
    {
        var binding = Scope.Own(new CommandBinding(command, parameter ?? (() => null), enabled => view.Enabled = enabled, Dispatcher, Error));
        EventHandler handler = (_, _) => binding.Execute();
        view.Click += handler;
        Scope.Own(new ActionDisposable(() => view.Click -= handler));
        return view;
    }

    /// <summary>Runs <paramref name="action"/> on click while the scope lives.</summary>
    public TView Click<TView>(TView view, Action action) where TView : View
    {
        EventHandler handler = (_, _) =>
        {
            try { action(); }
            catch (Exception exception) { Error(exception); }
        };
        view.Click += handler;
        Scope.Own(new ActionDisposable(() => view.Click -= handler));
        return view;
    }

    /// <summary>Calls <paramref name="changed"/> after every collection change while the scope lives.</summary>
    public void Collection(System.Collections.Specialized.INotifyCollectionChanged collection, Action changed)
    {
        System.Collections.Specialized.NotifyCollectionChangedEventHandler handler = async (_, _) =>
        {
            try { await Dispatcher.ExecuteOnUIThread(() => { if (!Scope.IsDisposed) changed(); }); }
            catch (Exception exception) { Error(exception); }
        };
        collection.CollectionChanged += handler;
        Scope.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        changed();
    }
}

/// <summary>Runs an action once when disposed; detaches event handlers inside a <see cref="BindingScope"/>.</summary>
public sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

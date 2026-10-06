using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Wino.Core.Domain.Interfaces;

namespace Wino.Presentation.AppKit;

/// <summary>One view or reused row owns one scope; disposal rejects queued UI callbacks.</summary>
public sealed class BindingScope : IDisposable
{
    private readonly List<IDisposable> _bindings = new();
    private bool _disposed;
    public bool IsDisposed => _disposed;
    public T Own<T>(T binding) where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bindings.Add(binding);
        return binding;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int index = _bindings.Count - 1; index >= 0; index--) _bindings[index].Dispose();
        _bindings.Clear();
    }
}

/// <summary>Typed one/two-way property binding; no reflection or expression compilation.</summary>
public sealed class PropertyBinding<TSource, TValue> : IDisposable where TSource : INotifyPropertyChanged
{
    private readonly TSource _source;
    private readonly string _property;
    private readonly Func<TSource, TValue> _read;
    private readonly Action<TValue> _apply;
    private readonly Action<TSource, TValue>? _write;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private bool _disposed;
    private bool _applying;
    private int _version;
    public PropertyBinding(TSource source, string property, Func<TSource, TValue> read, Action<TValue> apply,
        IDispatcher dispatcher, Action<Exception> error, Action<TSource, TValue>? write = null)
    {
        _source = source; _property = property; _read = read; _apply = apply;
        _dispatcher = dispatcher; _error = error; _write = write;
        source.PropertyChanged += Changed;
        Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == _property) Refresh();
    }
    public async void Refresh()
    {
        int version = Interlocked.Increment(ref _version);
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_disposed || version != _version) return;
                _applying = true;
                try { _apply(_read(_source)); }
                finally { _applying = false; }
            });
        }
        catch (Exception error) { if (!_disposed) _error(error); }
    }
    public void UpdateSource(TValue value)
    {
        if (_disposed || _applying) return;
        if (_write is null) throw new InvalidOperationException("This binding is one-way.");
        if (!EqualityComparer<TValue>.Default.Equals(_read(_source), value)) _write(_source, value);
    }
    public void Dispose() { _disposed = true; Interlocked.Increment(ref _version); _source.PropertyChanged -= Changed; }
}

public sealed class CommandBinding : IDisposable
{
    private readonly ICommand _command;
    private readonly Func<object?> _parameter;
    private readonly Action<bool> _enabled;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private bool _disposed;
    public CommandBinding(ICommand command, Func<object?> parameter, Action<bool> enabled, IDispatcher dispatcher, Action<Exception> error)
    {
        _command = command; _parameter = parameter; _enabled = enabled; _dispatcher = dispatcher; _error = error;
        command.CanExecuteChanged += Changed;
        Changed(this, EventArgs.Empty);
    }
    private async void Changed(object? sender, EventArgs args)
    {
        try { await _dispatcher.ExecuteOnUIThread(() => { if (!_disposed) _enabled(_command.CanExecute(_parameter())); }); }
        catch (Exception error) { if (!_disposed) _error(error); }
    }
    public async void Execute()
    {
        if (_disposed) return;
        try
        {
            var parameter = _parameter();
            if (!_command.CanExecute(parameter)) return;
            if (_command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else _command.Execute(parameter);
        }
        catch (Exception error) { if (!_disposed) _error(error); }
    }
    public void Dispose() { _disposed = true; _command.CanExecuteChanged -= Changed; }
}

/// <summary>Replaces child subscriptions whenever the owning property changes.</summary>
public sealed class NestedPropertyBinding<TSource, TChild, TValue> : IDisposable
    where TSource : INotifyPropertyChanged where TChild : class, INotifyPropertyChanged
{
    private readonly TSource _source;
    private readonly string _ownerProperty;
    private readonly Func<TSource, TChild?> _child;
    private readonly Func<TChild, IDisposable> _bind;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private IDisposable? _current;
    private bool _disposed;
    private int _version;
    public NestedPropertyBinding(TSource source, string ownerProperty, Func<TSource, TChild?> child,
        Func<TChild, IDisposable> bind, IDispatcher dispatcher, Action<Exception> error)
    {
        _source = source; _ownerProperty = ownerProperty; _child = child; _bind = bind; _dispatcher = dispatcher; _error = error;
        source.PropertyChanged += Changed;
        Changed(this, new(ownerProperty));
    }
    private async void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != _ownerProperty) return;
        int version = Interlocked.Increment(ref _version);
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_disposed || version != _version) return;
                _current?.Dispose(); _current = null;
                if (_child(_source) is { } child) _current = _bind(child);
            });
        }
        catch (Exception error) { if (!_disposed) _error(error); }
    }
    public void Dispose() { _disposed = true; Interlocked.Increment(ref _version); _source.PropertyChanged -= Changed; _current?.Dispose(); _current = null; }
}

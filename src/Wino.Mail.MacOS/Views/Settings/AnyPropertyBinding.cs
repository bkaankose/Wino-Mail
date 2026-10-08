using System.ComponentModel;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// One-way binding for computed ViewModel properties that are not individually notified: the value is
/// re-read after every property change of the source and applied on the UI thread when it differs.
/// </summary>
internal sealed class AnyPropertyBinding<TSource, TValue> : IDisposable where TSource : INotifyPropertyChanged
{
    private readonly TSource _source;
    private readonly Func<TSource, TValue> _read;
    private readonly Action<TValue> _apply;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private bool _disposed;
    private bool _hasValue;
    private TValue _last = default!;

    public AnyPropertyBinding(TSource source, Func<TSource, TValue> read, Action<TValue> apply, IDispatcher dispatcher, Action<Exception> error)
    {
        _source = source;
        _read = read;
        _apply = apply;
        _dispatcher = dispatcher;
        _error = error;
        _source.PropertyChanged += Changed;
        Refresh();
    }

    private void Changed(object? sender, PropertyChangedEventArgs args) => Refresh();

    private async void Refresh()
    {
        try
        {
            if (NSThread.IsMain) Apply();
            else await _dispatcher.ExecuteOnUIThread(Apply);
        }
        catch (Exception exception) { _error(exception); }
    }

    private void Apply()
    {
        if (_disposed) return;
        var value = _read(_source);
        if (_hasValue && EqualityComparer<TValue>.Default.Equals(value, _last)) return;
        _last = value;
        _hasValue = true;
        _apply(value);
    }

    public void Dispose()
    {
        _disposed = true;
        _source.PropertyChanged -= Changed;
    }
}

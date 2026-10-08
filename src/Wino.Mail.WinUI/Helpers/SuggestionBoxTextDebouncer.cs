using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Dispatcher-based debounce subscription for recipient and attendee suggestions.
/// </summary>
public sealed partial class SuggestionBoxTextDebouncer : IDisposable
{
    private readonly Action _unsubscribe;
    private readonly Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> _onTextChanged;
    private readonly DispatcherQueueTimer _timer;
    private AutoSuggestBoxTextChangedEventArgs _pendingArgs;
    private AutoSuggestBox _pendingSender;

    public SuggestionBoxTextDebouncer(
        TokenizingTextBox box,
        TimeSpan dueTime,
        Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> onTextChanged)
        : this(box.DispatcherQueue, dueTime, onTextChanged)
    {
        box.TextChanged += OnBoxTextChanged;
        _unsubscribe = () => box.TextChanged -= OnBoxTextChanged;
    }

    public SuggestionBoxTextDebouncer(
        AutoSuggestBox box,
        TimeSpan dueTime,
        Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> onTextChanged)
        : this(box.DispatcherQueue, dueTime, onTextChanged)
    {
        box.TextChanged += OnBoxTextChanged;
        _unsubscribe = () => box.TextChanged -= OnBoxTextChanged;
    }

    private SuggestionBoxTextDebouncer(
        DispatcherQueue dispatcherQueue,
        TimeSpan dueTime,
        Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> onTextChanged)
    {
        _onTextChanged = onTextChanged;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = dueTime;
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
    }

    private void OnBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _pendingSender = sender;
        _pendingArgs = args;
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        var pendingSender = _pendingSender;
        var pendingArgs = _pendingArgs;
        _pendingSender = null;
        _pendingArgs = null;

        if (pendingSender != null && pendingArgs != null)
            _onTextChanged(pendingSender, pendingArgs);
    }

    public void Dispose()
    {
        _unsubscribe?.Invoke();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _pendingSender = null;
        _pendingArgs = null;
    }
}

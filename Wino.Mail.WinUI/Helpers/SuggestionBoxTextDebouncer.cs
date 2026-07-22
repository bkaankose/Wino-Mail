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
    private readonly TokenizingTextBox _box;
    private readonly Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> _onTextChanged;
    private readonly DispatcherQueueTimer _timer;
    private AutoSuggestBoxTextChangedEventArgs _pendingArgs;
    private AutoSuggestBox _pendingSender;

    public SuggestionBoxTextDebouncer(
        TokenizingTextBox box,
        TimeSpan dueTime,
        Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> onTextChanged)
    {
        _box = box;
        _onTextChanged = onTextChanged;
        _timer = box.DispatcherQueue.CreateTimer();
        _timer.Interval = dueTime;
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
        box.TextChanged += OnBoxTextChanged;
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
        _box.TextChanged -= OnBoxTextChanged;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _pendingSender = null;
        _pendingArgs = null;
    }
}

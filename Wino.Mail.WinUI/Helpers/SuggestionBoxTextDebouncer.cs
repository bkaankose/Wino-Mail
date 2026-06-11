using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Debounced TextChanged subscription for the recipient/attendee TokenizingTextBoxes.
/// Replaces the System.Reactive throttle so the UI process doesn't ship Rx; the
/// callback always runs on the box's dispatcher thread with the latest event args.
/// </summary>
public sealed partial class SuggestionBoxTextDebouncer : IDisposable
{
    private readonly TokenizingTextBox _box;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs> _onTextChanged;

    private AutoSuggestBox _pendingSender;
    private AutoSuggestBoxTextChangedEventArgs _pendingArgs;

    public SuggestionBoxTextDebouncer(TokenizingTextBox box,
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

        // Restart the due time on every keystroke; only the last event fires.
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
        {
            _onTextChanged(pendingSender, pendingArgs);
        }
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

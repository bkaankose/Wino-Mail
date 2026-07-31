using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.ViewModels;

public class CoreBaseViewModel : ObservableRecipient, INavigationAware
{
    private IDispatcher _dispatcher;
    public IDispatcher Dispatcher
    {
        get
        {
            return _dispatcher;
        }
        set
        {
            _dispatcher = value;

            if (value != null)
            {
                OnDispatcherAssigned();
            }
        }
    }

    public virtual void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        UnregisterRecipients();
        RegisterRecipients();
    }

    public virtual void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        UnregisterRecipients();
    }

    public virtual void OnPageLoaded() { }

    public virtual Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args) => Task.CompletedTask;

    public Task ExecuteUIThread(Action action)
    {
        if (action == null) return Task.CompletedTask;

        if (Dispatcher == null)
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.ExecuteOnUIThread(action);
    }

    public async Task ExecuteUIThreadAsync(Func<Task> action)
    {
        if (action == null)
            return;

        if (Dispatcher == null)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var completionSource = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.ExecuteOnUIThread(() => _ = ExecuteAndCaptureAsync())
            .ConfigureAwait(false);
        await completionSource.Task.ConfigureAwait(false);

        async Task ExecuteAndCaptureAsync()
        {
            try
            {
                // Deliberately preserve the UI context for continuations inside the callback.
                await action();
                completionSource.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
        }
    }

    public async Task<T> ExecuteUIThreadAsync<T>(Func<Task<T>> action)
    {
        if (action == null)
            return default;

        if (Dispatcher == null)
            return await action().ConfigureAwait(false);

        var completionSource = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.ExecuteOnUIThread(() => _ = ExecuteAndCaptureAsync())
            .ConfigureAwait(false);
        return await completionSource.Task.ConfigureAwait(false);

        async Task ExecuteAndCaptureAsync()
        {
            try
            {
                // Deliberately preserve the UI context for continuations inside the callback.
                var result = await action();
                completionSource.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
        }
    }

    public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage => Messenger.Send(message);

    protected virtual void OnDispatcherAssigned() { }

    /// <summary>
    /// Register message recipients for this view model. Override to register specific message types.
    /// </summary>
    protected virtual void RegisterRecipients() { }

    /// <summary>
    /// Unregister message recipients for this view model. Override to unregister specific message types.
    /// </summary>
    protected virtual void UnregisterRecipients() { }
}

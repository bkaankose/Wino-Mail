using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed record WinoViewServices(IDispatcher Dispatcher, IWinoLogger Logger, IMessenger Messenger);

internal interface IWinoView
{
    Gtk.Widget Root { get; }
    object AssociatedViewModel { get; }
    bool HasPendingWork { get; }
    Task ActivateAsync(NavigationMode mode, object? parameter);
    Task ReleaseAsync();
}

/// <summary>A view that stays in its host and takes the next parameter instead of being rebuilt (the reader).</summary>
internal interface IRenavigableView
{
    Task RenavigateAsync(object? parameter);
}

/// <summary>
/// The base of a routed view: it owns its ViewModel (created in the view's scope by <see cref="PageScopes"/>), its
/// bindings and its message subscriptions, and releases all of them in <see cref="ReleaseAsync"/>. Widgets are touched
/// on the main thread only and never after release; a derived view checks <see cref="IsReleased"/> after each await.
/// </summary>
internal abstract class WinoView<TViewModel> : IWinoView where TViewModel : CoreBaseViewModel
{
    private Task _initialization = Task.CompletedTask;

    protected WinoView(TViewModel viewModel, WinoViewServices services)
    {
        WinoLiveObjects.Track(this);
        ViewModel = viewModel;
        Dispatcher = services.Dispatcher;
        Logger = services.Logger;
        Messenger = services.Messenger;
        Bindings = new BindingScope();
        Bind = new GtkBinder<TViewModel>(viewModel, Bindings, Dispatcher, ReportError);
        ViewModel.Dispatcher = Dispatcher;
    }

    public abstract Gtk.Widget Root { get; }
    public TViewModel ViewModel { get; }
    object IWinoView.AssociatedViewModel => ViewModel;
    public virtual bool HasPendingWork => !_initialization.IsCompleted;

    protected IDispatcher Dispatcher { get; }
    protected IWinoLogger Logger { get; }
    protected IMessenger Messenger { get; }
    protected BindingScope Bindings { get; }
    protected GtkBinder<TViewModel> Bind { get; }
    protected bool IsActive { get; private set; }
    protected bool IsReleased { get; private set; }

    public async Task ActivateAsync(NavigationMode mode, object? parameter)
    {
        var initialization = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            ObjectDisposedException.ThrowIf(IsReleased, this);
            if (IsActive) return;
            IsActive = true;
            initialization = _initialization = InitializeAsync(mode, parameter);
        });
        await initialization;

        var loaded = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            ViewModel.OnPageLoaded();
            loaded = LoadedAsync();
        });
        await loaded;
    }

    public async Task ReleaseAsync()
    {
        var wasActive = false;
        var release = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            IsReleased = true;
            release = true;
            wasActive = IsActive;
            IsActive = false;
        });
        if (!release) return;

        try { await _initialization; }
        catch (Exception exception) { ReportError(exception); }

        Messenger.UnregisterAll(this);
        var deactivation = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (wasActive) deactivation = DeactivateAsync();
        });
        try { await deactivation; }
        catch (Exception exception) { ReportError(exception); }

        await Dispatcher.ExecuteOnUIThread(Bindings.Dispose);
        // ViewModel.Dispatcher stays assigned: a late background continuation of the ViewModel would dereference it.
        PageScopes.Release(this);
    }

    protected virtual Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        return Task.CompletedTask;
    }

    protected virtual Task LoadedAsync() => Task.CompletedTask;

    protected virtual Task DeactivateAsync()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        return Task.CompletedTask;
    }

    // CaptureException sends nothing while telemetry is off, which it always is on Linux, so the error is also logged here.
    protected void ReportError(Exception exception)
    {
        Serilog.Log.Error(exception, "{View} failed.", GetType().Name);
        Logger.CaptureException(exception, GetType().Name);
    }

    protected async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { if (!IsReleased) ReportError(exception); }
    }

    /// <summary>Messenger handlers run on background threads; the handler is moved to the main thread and dropped after release.</summary>
    protected void Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class
        => Messenger.Register<WinoView<TViewModel>, TMessage>(this, (recipient, message) => recipient.RunOnUI(() => handler(message)));

    protected void RunOnUI(Action action) => Observe(Dispatcher.ExecuteOnUIThread(() =>
    {
        if (!IsReleased) action();
    }));
}

using AppKit;
using Wino.Core.ViewModels;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

public interface IWinoViewController
{
    bool HasPendingWork { get; }
    Task ActivateAsync(NavigationMode mode, object? parameter);
    Task ReleaseAsync();
}

public abstract class WinoViewController<TViewModel> : NSViewController, IWinoViewController where TViewModel : CoreBaseViewModel
{
    protected TViewModel ViewModel { get; }
    protected IDispatcher Dispatcher { get; }
    protected IWinoLogger Logger { get; }
    protected BindingScope Bindings { get; } = new();
    private bool _active;
    private bool _released;
    private Task _initialization = Task.CompletedTask;
    public virtual bool HasPendingWork => !_initialization.IsCompleted;

    protected WinoViewController(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    {
        ViewModel = viewModel;
        Dispatcher = dispatcher;
        Logger = logger;
        ViewModel.Dispatcher = dispatcher;
    }

    public async Task ActivateAsync(NavigationMode mode, object? parameter)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_active) return;
        _active = true;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _ = View;
            _initialization = InitializeAsync(mode, parameter);
        });
        await _initialization;
        await Dispatcher.ExecuteOnUIThread(() => { if (!_released) ViewModel.OnPageLoaded(); });
    }

    protected virtual Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        return Task.CompletedTask;
    }

    protected virtual Task DeactivateAsync()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
        return Task.CompletedTask;
    }

    protected void ReportError(Exception exception) => Logger.CaptureException(exception, GetType().Name);

    protected NSButton CommandButton(string title, System.Windows.Input.ICommand command, Func<object?>? parameter = null)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded };
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.Activated += handler;
        Bindings.Own(new ActionDisposable(() => button.Activated -= handler));
        return button;
    }

    protected void BindText<TValue>(NSTextField field, string property, Func<TViewModel, TValue> read)
        => Bindings.Own(new PropertyBinding<TViewModel, TValue>(ViewModel, property, read, value => field.StringValue = value?.ToString() ?? string.Empty, Dispatcher, ReportError));

    public virtual async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        try { await _initialization; }
        catch (Exception exception) { ReportError(exception); }
        Task deactivation = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_active) deactivation = DeactivateAsync();
            _active = false;
            Bindings.Dispose();
        });
        try { await deactivation; }
        finally { await Dispatcher.ExecuteOnUIThread(() => ViewModel.Dispatcher = null!); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Router-owned asynchronous release precedes native disposal.
            Bindings.Dispose();
            if (_active) ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
            _active = false;
            _released = true;
            ViewModel.Dispatcher = null!;
        }
        base.Dispose(disposing);
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

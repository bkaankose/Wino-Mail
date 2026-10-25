using AppKit;
using Wino.Core.ViewModels;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Infrastructure;

public interface IWinoViewController
{
    bool HasPendingWork { get; }
    Task ActivateAsync(NavigationMode mode, object? parameter);
    Task ReleaseAsync();
}

/// <summary>Exposes a page's ViewModel to the router (back confirmation and results).</summary>
public interface IViewModelHost
{
    object AssociatedViewModel { get; }
}

public abstract class WinoViewController<TViewModel> : NSViewController, IWinoViewController, IViewModelHost where TViewModel : CoreBaseViewModel
{
    protected TViewModel ViewModel { get; }
    object IViewModelHost.AssociatedViewModel => ViewModel;
    protected IDispatcher Dispatcher { get; }
    protected IWinoLogger Logger { get; }
    protected BindingScope Bindings { get; } = new();
    private bool _active;
    private bool _released;
    private Task _initialization = Task.CompletedTask;
    public virtual bool HasPendingWork => !_initialization.IsCompleted;

    protected WinoViewController(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    {
        WinoLiveObjects.Track(this);
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
        ViewModel.OnNavigatedFrom(NavigationMode.New, null);
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
            if (_active) ViewModel.OnNavigatedFrom(NavigationMode.New, null);
            _active = false;
            _released = true;
            ViewModel.Dispatcher = null!;
            ViewTreeDisposal.DisposeController(this);
        }
        base.Dispose(disposing);
        if (disposing) PageScopes.Release(this);
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

/// <summary>
/// Releases the managed peers of a page's view tree once the tree has left the window. A framework
/// view (an NSButton, an NSTextField) with a managed event handler keeps a strong handle on its
/// managed wrapper while its native object lives; the handler references the parent view, whose
/// managed wrapper retains the native parent, which retains the button. That cycle crosses the
/// managed/native boundary, so the collector never breaks it and a detached page stays in memory
/// with every cell, card and ViewModel it holds. Disposing the wrappers bottom-up lets the native
/// objects reach a zero retain count and the cycle unwinds.
/// </summary>
public static class ViewTreeDisposal
{
    /// <summary>
    /// A disposed page is gone for good: its child controllers go first (a split view controller's
    /// wrapper otherwise keeps the native tree alive through the page), then its view leaves the
    /// hierarchy and every peer in the tree is released.
    /// </summary>
    public static void DisposeController(NSViewController controller)
    {
        if (controller.Handle == IntPtr.Zero) return;
        foreach (var child in controller.ChildViewControllers)
        {
            DisposeController(child);
            if (child.Handle != IntPtr.Zero) { child.RemoveFromParentViewController(); child.Dispose(); }
        }
        if (controller.ViewLoaded) DisposeTree(controller.View);
    }

    public static void DisposeTree(NSView root)
    {
        if (root.Handle == IntPtr.Zero) return;
        if (root.Superview is not null) root.RemoveFromSuperview();
        Dispose(root);
    }

    private static void Dispose(NSView view)
    {
        if (view.Handle == IntPtr.Zero) return;
        // A context menu's items keep handlers that reference the page; text views share the system
        // menu, so theirs stay.
        if (view is not (NSText or NSTextField or WebKit.WKWebView) && view.Menu is { } menu)
        {
            view.Menu = null;
            WinoMenus.DisposeTree(menu);
        }
        switch (view)
        {
            case NSTableView table:
                table.DataSource = null!;
                table.Delegate = null!;
                break;
            case NSScrollView scroll when scroll.DocumentView is NSView document:
                Dispose(document);
                break;
        }
        foreach (var child in view.Subviews) Dispose(child);
        view.Dispose();
    }
}

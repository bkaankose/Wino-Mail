using System.Windows.Input;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>A routed page: the navigation host activates it once it is on screen and releases it when it leaves.</summary>
public interface IWinoViewController
{
    bool HasPendingWork { get; }
    Task ActivateAsync(NavigationMode mode, object? parameter);
    Task ReleaseAsync();
}

/// <summary>
/// A shell-content page that takes a new navigation to its own route in place: the shell keeps the live page (its bar,
/// sheets and view state) and hands it the new parameter instead of building and placing a fresh page.
/// </summary>
public interface IReusableShellPage
{
    /// <summary>True when a navigation to <paramref name="page"/> can reuse this live page.</summary>
    bool CanReuseFor(WinoPage page);

    /// <summary>Applies the new navigation parameter.</summary>
    Task ReactivateAsync(object? parameter);
}

/// <summary>Exposes a page's ViewModel to the router (back confirmation and results).</summary>
public interface IViewModelHost
{
    object AssociatedViewModel { get; }
}

/// <summary>
/// Base of every UIKit page, with the Mac WinoViewController lifecycle: the router hosts the controller, then
/// <see cref="ActivateAsync"/> loads the view and runs <see cref="InitializeAsync"/> (OnNavigatedTo), and
/// <see cref="ReleaseAsync"/> deactivates the ViewModel and disposes the <see cref="Bindings"/>. A page popped from a
/// navigation stack or dismissed with its sheet releases itself, so interactive back gestures need no router call.
/// </summary>
public abstract class WinoViewController<TViewModel> : UIViewController, IWinoViewController, IViewModelHost where TViewModel : CoreBaseViewModel
{
    private bool _active;
    private bool _released;
    private Task? _releasing;
    private Task _initialization = Task.CompletedTask;

    protected WinoViewController(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    {
        ViewModel = viewModel;
        Dispatcher = dispatcher;
        Logger = logger;
        ViewModel.Dispatcher = dispatcher;
#if DEBUG
        Wino.Presentation.UIKit.WinoLiveObjects.Track(this);
#endif
    }

    protected TViewModel ViewModel { get; }
    object IViewModelHost.AssociatedViewModel => ViewModel;
    protected IDispatcher Dispatcher { get; }
    protected IWinoLogger Logger { get; }
    protected BindingScope Bindings { get; } = new();
    public virtual bool HasPendingWork => !_initialization.IsCompleted;

    /// <summary>True between activation and release.</summary>
    protected bool IsActive => _active && !_released;

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
        ViewModel.OnNavigatedTo(mode, parameter);
        return Task.CompletedTask;
    }

    protected virtual Task DeactivateAsync()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        return Task.CompletedTask;
    }

    /// <summary>Logs a page error locally and reports it to telemetry (which only sends when the user opted in).</summary>
    protected void ReportError(Exception exception)
    {
        Serilog.Log.Error(exception, "{Page} failed.", GetType().Name);
        Logger.CaptureException(exception, GetType().Name);
    }

    /// <summary>A system button that runs <paramref name="command"/> and follows its CanExecute.</summary>
    protected UIButton CommandButton(string title, ICommand command, Func<object?>? parameter = null, bool primary = false)
    {
        var button = SettingsBinder.CreateButton(title, primary);
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.PrimaryActionTriggered += handler;
        Bindings.Own(new ActionDisposable(() => button.PrimaryActionTriggered -= handler));
        return button;
    }

    protected void BindText<TValue>(UILabel label, string property, Func<TViewModel, TValue> read)
        => Bindings.Own(new PropertyBinding<TViewModel, TValue>(ViewModel, property, read, value => label.Text = value?.ToString() ?? string.Empty, Dispatcher, ReportError));

    protected void Bind<TValue>(string property, Func<TViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<TViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        // Popped from a navigation stack (back button or swipe) or dismissed with its sheet: nothing routes here again.
        bool removed = IsMovingFromParentViewController || IsBeingDismissed
            || (NavigationController is { } navigation
                && (navigation.IsBeingDismissed || navigation.IsMovingFromParentViewController));
        if (removed && _active && !WinoPageMoves.IsMoving(this)) _ = ReleaseQuietlyAsync();
    }

    private async Task ReleaseQuietlyAsync()
    {
        try { await ReleaseAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    // A second caller waits for the release in flight, so a page is never disposed while its release still runs.
    public virtual Task ReleaseAsync() => _releasing ??= ReleaseCoreAsync();

    private async Task ReleaseCoreAsync()
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
        await deactivation;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Router-owned asynchronous release precedes native disposal.
            Bindings.Dispose();
            if (_active && !_released) ViewModel.OnNavigatedFrom(NavigationMode.New, null);
            _active = false;
            _released = true;
            ViewTreeDisposal.DisposeController(this);
        }
        base.Dispose(disposing);
        if (disposing) PageScopes.Release(this);
    }
}

/// <summary>Runs an action once when disposed; used to detach event handlers inside a <see cref="BindingScope"/>.</summary>
public sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

/// <summary>
/// Releases the managed peers of a page's view tree once the tree has left the window. A framework
/// view with a managed event handler keeps a strong handle on its wrapper while its native object
/// lives; the handler references the parent view, whose wrapper retains the native parent, which
/// retains the child. The collector cannot break that managed/native cycle, so disposing the
/// wrappers bottom-up is what lets a detached page go.
/// </summary>
public static class ViewTreeDisposal
{
    /// <summary>A disposed page is gone for good: child controllers first, then its view leaves the hierarchy and every peer in the tree is released.</summary>
    public static void DisposeController(UIViewController controller)
    {
        if (controller.Handle == IntPtr.Zero) return;
        foreach (var child in controller.ChildViewControllers)
        {
            DisposeController(child);
            if (child.Handle != IntPtr.Zero) { child.RemoveFromParentViewController(); child.Dispose(); }
        }
        if (controller.IsViewLoaded && controller.View is { } view) DisposeTree(view);
    }

    public static void DisposeTree(UIView root)
    {
        if (root.Handle == IntPtr.Zero) return;
        if (root.Superview is not null) root.RemoveFromSuperview();
        Dispose(root);
    }

    private static void Dispose(UIView view)
    {
        if (view.Handle == IntPtr.Zero) return;
        switch (view)
        {
            case UITableView table:
                table.DataSource = null!;
                table.Delegate = null!;
                break;
            case UICollectionView collection:
                collection.DataSource = null!;
                collection.Delegate = null!;
                break;
        }
        foreach (var child in view.Subviews) Dispose(child);
        view.Dispose();
    }
}

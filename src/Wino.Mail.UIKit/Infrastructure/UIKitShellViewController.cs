using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Shell.ViewModels;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// The shell behaviour both idioms share, over the shared <see cref="WinoAppShellViewModel"/> (the Mac
/// WinoAppShellViewController's mode logic): a mode activates like the Windows WinoAppShell, releasing the outgoing
/// provider's menu, publishing the new provider and letting it navigate its root page into the content host.
/// PhoneShellViewController places content in the selected tab's stack, PadShellViewController in the list column.
/// </summary>
public abstract class UIKitShellViewController : WinoViewController<WinoAppShellViewModel>
{
    private readonly INavigationService _navigation;
    private readonly IStatePersistanceService _state;
    private readonly ShellModeActivator _modes;
    private UIViewController? _child;

    protected UIKitShellViewController(WinoAppShellViewModel viewModel, INavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _state = state;
        Services = services;
        _modes = new ShellModeActivator(viewModel, state, ReportError);
    }

    protected IServiceProvider Services { get; }

    /// <summary>The mode on screen.</summary>
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;

    /// <summary>The page in the content host, or null.</summary>
    public UIViewController? Content => _child;

    /// <summary>The content page's ViewModel (back confirmation, keyboard shortcuts).</summary>
    public object? ContentViewModel => (_child as IViewModelHost)?.AssociatedViewModel;

    /// <summary>The navigation stack that pushes from the shell land on (the selected tab, or the list column).</summary>
    public abstract UINavigationController? CurrentNavigationController { get; }

    /// <summary>The shell's mail client, available in every mode.</summary>
    protected IMailShellClient? MailClient => Services.GetService<IMailShellClient>();

    /// <summary>Puts <paramref name="controller"/> in the content host, or empties it. Main thread.</summary>
    protected abstract void PlaceContent(UIViewController? controller);

    /// <summary>The mode changed (select the tab or the switcher segment). Main thread.</summary>
    protected abstract void ModeChanged(WinoApplicationMode mode);

    /// <summary>The published menu changed (sidebar rows, Mailboxes). Main thread.</summary>
    protected abstract void MenuChanged(ShellMenu? menu);

    /// <summary>The selected menu item changed. Main thread.</summary>
    protected virtual void SelectedItemChanged(object? item) { }

    protected void BindShell()
    {
        Bind(nameof(ViewModel.CurrentMenu), vm => vm.CurrentMenu, MenuChanged);
        Bind(nameof(ViewModel.SelectedMenuItem), vm => vm.SelectedMenuItem, SelectedItemChanged);
        Bind(nameof(ViewModel.CurrentMode), vm => vm.CurrentMode, ModeChanged);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        await ActivateModeAsync(ShellModeActivator.StartMode(_state), new ShellModeActivationContext { IsInitialActivation = true });
    }

    /// <summary>
    /// Switches the shell to a mode the way the Windows WinoAppShell.ActivateMode does (shared
    /// <see cref="ShellModeActivator"/>). A mode whose root page is not built yet leaves its content empty rather than failing.
    /// </summary>
    public Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
        => _modes.ActivateAsync(mode, context, ClearContentAsync, Dispatcher);

    public static string ModeTitle(WinoApplicationMode mode) => ShellModeActivator.ModeTitle(mode);

    /// <summary>Hosts a shell-content page and activates it; the previous page is released first.</summary>
    public async Task SetContentAsync(UIViewController controller, object? parameter)
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child = controller;
            PlaceContent(controller);
        });
        if (controller is IWinoViewController next) await next.ActivateAsync(NavigationMode.New, parameter);
    }

    public async Task ClearContentAsync()
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child = null;
            PlaceContent(null);
        });
    }

    /// <summary>Asks the router for <paramref name="mode"/>; Settings opens the Settings sheet.</summary>
    public void RequestMode(WinoApplicationMode mode) => _navigation.ChangeApplicationMode(mode);

    /// <summary>The account avatar: Settings is a sheet, never a mode.</summary>
    public void OpenSettings() => _navigation.ChangeApplicationMode(WinoApplicationMode.Settings);

    /// <summary>A pane row was tapped (Mac WinoAppShellViewController.Select).</summary>
    public async void SelectMenuItem(IMenuItem item)
    {
        try
        {
            if (!ShellMenuItemRules.IsEnabled(item)) return;
            // Folders select themselves when their navigation starts (MailAppShellViewModel.NavigateFolderAsync).
            if (ViewModel.HandlesSelection && ShellMenuItemRules.SelectsOnInvoked(item) && item is not IBaseFolderMenuItem)
                ViewModel.SelectedMenuItem = item;
            await ViewModel.InvokeMenuItemAsync(item);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>Synchronizes the current mode (toolbar or pull to refresh).</summary>
    public async Task SynchronizeAsync()
    {
        try { if (ViewModel.CurrentProvider is { CanSynchronize: true } provider) await provider.SynchronizeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    public override async Task ReleaseAsync()
    {
        if (_child is IWinoViewController child) await child.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child = null;
            ViewModel.ShutdownProviders();
        });
        await base.ReleaseAsync();
    }
}

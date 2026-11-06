using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Shell.ViewModels;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>
/// The shell's behaviour, independent of any fragment (the UIKitShellViewController twin): it owns the shared
/// <see cref="WinoAppShellViewModel"/> for as long as the shell is up, switches modes through the shared
/// <see cref="ShellModeActivator"/> (Windows WinoAppShell.ActivateMode), and offers the actions every size class shares:
/// menu rows, New mail, synchronization (the Windows title-bar Sync button: the active provider's
/// <see cref="IShellMenuProvider.SynchronizeAsync"/>) and Settings through the avatar. <see cref="ShellFragment"/>
/// recreates its views over this object on every configuration change.
/// </summary>
public sealed class AndroidShellController
{
    private readonly AndroidNavigationService _navigation;
    private readonly ShellModeActivator _modes;
    private bool _released;

    public AndroidShellController(WinoAppShellViewModel viewModel, AndroidNavigationService navigation, IStatePersistanceService state,
        IServiceProvider services, IDispatcher dispatcher)
    {
        ViewModel = viewModel;
        _navigation = navigation;
        Services = services;
        Dispatcher = dispatcher;
        viewModel.Dispatcher = dispatcher;
        _modes = new ShellModeActivator(viewModel, state, Report);
        viewModel.PropertyChanged += ViewModelChanged;
    }

    public WinoAppShellViewModel ViewModel { get; }
    public IServiceProvider Services { get; }
    public IDispatcher Dispatcher { get; }

    /// <summary>The mode on screen.</summary>
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;

    /// <summary>The mail provider, available in every mode (drawer context menus, New mail).</summary>
    public IMailShellClient? MailClient => Services.GetService<IMailShellClient>();

    /// <summary>The active mode's provider (synchronization state and the Sync action).</summary>
    public IShellMenuProvider? CurrentProvider => ViewModel.CurrentProvider;

    /// <summary>Raised on the UI thread when the menu, the selected item, the mode or the provider's synchronization changed.</summary>
    public event EventHandler<string>? Changed;

    private IShellMenuProvider? _observedProvider;

    /// <summary>First activation: the shell ViewModel, then the persisted mode.</summary>
    public async Task ActivateAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() => ViewModel.OnNavigatedTo(NavigationMode.New, null));
        var start = ShellModeActivator.StartMode(ViewModel.StatePersistenceService, _navigation.IsModeAvailable);
        await ActivateModeAsync(start, new ShellModeActivationContext { IsInitialActivation = true });
    }

    /// <summary>Switches to <paramref name="mode"/>; its back stack becomes current first.</summary>
    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        if (_released || mode == WinoApplicationMode.Settings) return;
        if (_modes.ActiveMode != mode) await _navigation.SetActiveModeAsync(mode);
        await _modes.ActivateAsync(mode, context, _navigation.ClearShellContentAsync, Dispatcher);
        await Dispatcher.ExecuteOnUIThread(() => Changed?.Invoke(this, nameof(ActiveMode)));
    }

    /// <summary>A mode tab, rail item or switcher segment: the router activates it like the Windows shell.</summary>
    public void RequestMode(WinoApplicationMode mode)
    {
        if (mode != ActiveMode) _navigation.ChangeApplicationMode(mode);
    }

    /// <summary>The account avatar: Settings is a surface over the shell, never a mode.</summary>
    public void OpenSettings() => _navigation.ChangeApplicationMode(WinoApplicationMode.Settings);

    /// <summary>A menu row was tapped (UIKitShellViewController.SelectMenuItem).</summary>
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
        catch (Exception exception) { Report(exception); }
    }

    /// <summary>New mail: the shared Mail provider creates the draft and routes it to the composer.</summary>
    public void Compose()
    {
        if (MailClient is { } mail) SelectMenuItem(mail.CreatePrimaryMenuItem);
    }

    /// <summary>The Sync action and pull to refresh: the active provider's synchronization, as the Windows title bar runs it.</summary>
    public async Task SynchronizeAsync()
    {
        try { if (ViewModel.CurrentProvider is { CanSynchronize: true } provider) await provider.SynchronizeAsync(); }
        catch (Exception exception) { Report(exception); }
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WinoAppShellViewModel.CurrentProvider) or null) ObserveProvider(ViewModel.CurrentProvider);
        var name = args.PropertyName ?? string.Empty;
        _ = Dispatcher.ExecuteOnUIThread(() => Changed?.Invoke(this, name));
    }

    private void ObserveProvider(IShellMenuProvider? provider)
    {
        if (ReferenceEquals(provider, _observedProvider)) return;
        if (_observedProvider is not null) _observedProvider.PropertyChanged -= ProviderChanged;
        _observedProvider = provider;
        if (provider is not null) provider.PropertyChanged += ProviderChanged;
    }

    private void ProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IShellMenuProvider.SynchronizationState) or nameof(IShellMenuProvider.SynchronizationDescription)
            or nameof(IShellMenuProvider.CanSynchronize) or null)
            _ = Dispatcher.ExecuteOnUIThread(() => Changed?.Invoke(this, nameof(IShellMenuProvider.SynchronizationState)));
    }

    /// <summary>The shell left the screen for good: providers shut down, the ViewModel is navigated from.</summary>
    public async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        ViewModel.PropertyChanged -= ViewModelChanged;
        ObserveProvider(null);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            ViewModel.ShutdownProviders();
            ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        });
        Changed = null;
    }

    private static void Report(Exception exception) => Serilog.Log.Error(exception, "The shell failed.");
}

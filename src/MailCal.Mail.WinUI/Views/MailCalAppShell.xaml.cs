#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Mail.WinUI.Helpers;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Mail.WinUI.Services;
using MailCal.MenuFlyouts;
using MailCal.Messaging.Client.Shell;
using MailCal.Messaging.UI;
using MailCal.Shell.ViewModels;
using MailCal.Views.Mail;
using WinRT;

namespace MailCal.Mail.WinUI.Views;

/// <summary>
/// Hosts the navigation pane and the inner frame. It knows which mode is active and
/// nothing else: the menu it shows is published by whatever page the inner frame navigated
/// to, and every interaction is forwarded straight back to that page's mode view model.
/// </summary>
public sealed partial class MailCalAppShell : Views.Abstract.MailCalAppShellAbstract,
    IShellHost,
    IShellMenuSink,
    IMailCalFrameProvider,
    IRecipient<AccountCreatedMessage>,
    IRecipient<CreateNewMailWithMultipleAccountsRequested>
{
    private readonly WinUIDispatcher _pageDispatcher;
    private MailCalApplicationMode? _activeMode;
    private bool _isPreparedForWindowClose;
    private readonly KeyboardShortcutController _shortcutController;

    // Selection is pushed into the navigation view from code rather than a TwoWay x:Bind.
    // A nested entry can only be selected once its group container is expanded and its own
    // container realized; setting it earlier makes NavigationView index past its realized
    // vector ("invalid vector subscript"). These bound the deferred attempts.
    private const int MaxMenuSelectionAttempts = 4;
    private int _menuSelectionRequestId;
    private bool _isApplyingMenuSelection;

    public MailCalAppShell()
    {
        InitializeComponent();

        _pageDispatcher = new WinUIDispatcher(DispatcherQueue);
        _shortcutController = new KeyboardShortcutController(
            this,
            MailCalApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>(),
            MailCalApplication.Current.Services.GetRequiredService<IMailCalLogger>(),
            () => ViewModel.CurrentMode,
            () => InnerShellFrame.Content as BasePage,
            () => GetFrame(NavigationReferenceFrame.RenderingFrame)?.Content as BasePage,
            ViewModel.KeyboardShortcutHookForMode);
        ViewModel.PreferencesService.PreferenceChanged += PreferencesServiceChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
        ViewModel.PropertyChanged += ShellViewModelPropertyChanged;

        // The preference service is an application singleton. Tracking it through x:Bind kept
        // closing windows subscribed, so the pane state is applied from code and the handler is
        // detached with the rest of the window state. It is set here, before the first layout,
        // so the navigation view builds its template in the remembered state.
        navigationView.IsPaneOpen = ViewModel.PreferencesService.IsNavigationPaneOpened;
    }

    public bool HasShellContent => InnerShellFrame.Content != null;

    public Frame? GetFrame(NavigationReferenceFrame frameType)
        => frameType switch
        {
            NavigationReferenceFrame.InnerShellFrame => InnerShellFrame,
            NavigationReferenceFrame.RenderingFrame => (InnerShellFrame.Content as IMailCalFrameProvider)?.GetFrame(frameType),
            _ => null
        };

    #region Mode activation

    public void ActivateMode(MailCalApplicationMode mode, ShellModeActivationContext activationContext)
    {
        var provider = ViewModel.GetProvider(mode);

        // Re-activating the mode already on screen only forwards the parameter along.
        if (_activeMode == mode && InnerShellFrame.Content != null)
        {
            if (activationContext.Parameter != null)
            {
                provider.ActivateShellMenu(activationContext);
            }

            return;
        }

        ReleaseActiveMode();
        _activeMode = mode;
        ViewModel.SetCurrentMode(mode);
        _shortcutController.Refresh();

        if (!ReferenceEquals(provider.Dispatcher, _pageDispatcher))
        {
            provider.Dispatcher = _pageDispatcher;
        }

        provider.ActivateShellMenu(activationContext);
    }

    private void ReleaseActiveMode()
    {
        if (_activeMode == null || !ViewModel.TryGetProvider(_activeMode.Value, out var provider))
            return;

        provider?.ReleaseShellMenu();
    }

    #endregion

    #region Lifetime

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Establish the pane state before the first mode publishes its menu.
        ApplyRememberedPaneState(navigationView.DisplayMode);
        UpdatePaneLayout(navigationView.DisplayMode, navigationView.IsPaneOpen);

        if (_activeMode == null)
        {
            ActivateMode(ViewModel.StatePersistenceService.ApplicationMode, new ShellModeActivationContext
            {
                IsInitialActivation = true
            });
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (!_isPreparedForWindowClose)
        {
            PrepareForWindowClose();
        }

        Bindings.StopTracking();
    }

    public void PrepareForWindowClose()
    {
        if (_isPreparedForWindowClose)
            return;

        _isPreparedForWindowClose = true;

        ViewModel.StatePersistenceService.IsReadingMail = false;
        ViewModel.StatePersistenceService.IsEventDetailsVisible = false;

        // The NavigationView owns WinRT item containers for the provider collections. Drop
        // those references before any provider clears its collection; mutating a collection
        // still connected to a closing XamlRoot is what surfaces as E_FAIL/"Unspecified error".
        DetachShellMenuBindings();

        WindowCleanupHelper.CleanupFrame(InnerShellFrame);
        ViewModel.ShutdownProviders();
        ViewModel.PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged -= StatePersistenceServiceChanged;
        ViewModel.PropertyChanged -= ShellViewModelPropertyChanged;
        _shortcutController.Dispose();

        UnregisterRecipients();
        Bindings.StopTracking();
    }

    private void DetachShellMenuBindings()
    {
        // Drop any deferred selection attempt; it must not touch a closing navigation view.
        Interlocked.Increment(ref _menuSelectionRequestId);

        ViewModel.SetShellMenu(null);

        // x:Bind notifications are synchronous today, but assign the WinRT properties as
        // well so teardown does not depend on binding-engine timing.
        SetNavigationSelection(null);
        navigationView.MenuItemsSource = null;
        navigationView.FooterMenuItemsSource = null;
    }

    #endregion

    #region Menu hosting

    /// <summary>
    /// Called by the navigation service once the inner frame lands on a page that owns a
    /// menu, and with null right before a mode switch so the navigation view lets go of its
    /// item containers.
    /// </summary>
    public void SetShellMenu(IShellMenuProvider? provider)
    {
        if (provider != null)
        {
            if (!ReferenceEquals(provider.Dispatcher, _pageDispatcher))
            {
                provider.Dispatcher = _pageDispatcher;
            }
        }

        ViewModel.SetShellMenu(provider);

        // The title bar's synchronization button belongs to whichever mode is publishing a
        // menu, so it has to be rebound whenever that changes.
        NotifyTitleBarContentChanged();
    }

    /// <summary>
    /// The mode currently publishing a menu. Read by the shell window to bind the title
    /// bar's synchronization button.
    /// </summary>
    public IShellMenuProvider? CurrentShellMenuProvider => ViewModel.CurrentProvider;

    private async void NavigationViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Templated containers do not always carry a DataContext, so fall back to the item
        // the navigation view resolved from the source collection.
        var menuItem = (args.InvokedItemContainer as FrameworkElement)?.DataContext as IMenuItem
                       ?? args.InvokedItem as IMenuItem;

        if (menuItem != null)
        {
            await ViewModel.InvokeMenuItemAsync(menuItem);
        }
    }

    private async void MenuSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var isApplyingFromViewModel = _isApplyingMenuSelection;
        var selectionTask = ViewModel.HandlesSelection && args.SelectedItem is IMenuItem selectedMenuItem
            ? ViewModel.ChangeMenuSelectionAsync(selectedMenuItem)
            : null;

        // Mirror a selection the user made back into the mode, which the TwoWay binding used
        // to do after this handler ran. A selection the shell applied already came from there.
        if (!isApplyingFromViewModel && !_isPreparedForWindowClose)
        {
            ViewModel.SelectedMenuItem = args.SelectedItem;
        }

        if (selectionTask != null)
        {
            await selectionTask;
        }
    }

    private void ShellViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailCalAppShellViewModel.SelectedMenuItem))
        {
            RequestMenuSelectionSync(defer: false);
        }
        else if (e.PropertyName == nameof(MailCalAppShellViewModel.CurrentMenu))
        {
            // x:Bind rebinds the menu source from the same notification; let it land first.
            RequestMenuSelectionSync(defer: true);
        }
    }

    private void RequestMenuSelectionSync(bool defer)
    {
        if (_isPreparedForWindowClose)
            return;

        var requestId = Interlocked.Increment(ref _menuSelectionRequestId);

        if (defer || !DispatcherQueue.HasThreadAccess)
        {
            QueueMenuSelectionAttempt(requestId, attempt: 1);
            return;
        }

        ApplyMenuSelection(requestId, attempt: 0);
    }

    private void QueueMenuSelectionAttempt(int requestId, int attempt)
    {
        // Low priority runs after the pending layout pass, which is what realizes the
        // containers of a group that was just expanded.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyMenuSelection(requestId, attempt)))
        {
            Log.Debug("Navigation pane selection could not be queued; the window is shutting down.");
        }
    }

    private void ApplyMenuSelection(int requestId, int attempt)
    {
        if (_isPreparedForWindowClose || requestId != Volatile.Read(ref _menuSelectionRequestId))
            return;

        var target = ViewModel.SelectedMenuItem;

        if (ReferenceEquals(navigationView.SelectedItem, target))
            return;

        if (target != null && !IsMenuItemReadyForSelection(target))
        {
            if (attempt < MaxMenuSelectionAttempts)
            {
                QueueMenuSelectionAttempt(requestId, attempt + 1);
                return;
            }

            Log.Debug("Navigation pane did not realize the container of {MenuItemType}; selecting it without one.", target.GetType().Name);
        }

        // One deferred retry when the navigation view rejects the selection outright.
        if (!SetNavigationSelection(target) && attempt < MaxMenuSelectionAttempts)
        {
            QueueMenuSelectionAttempt(requestId, MaxMenuSelectionAttempts);
        }
    }

    /// <summary>
    /// Whether the navigation view can select <paramref name="target"/> right now: a menu is
    /// published and, for a nested entry in an open pane whose ancestors the mode expanded,
    /// every ancestor container is expanded and the entry's own container exists. Expands a
    /// container that lags behind its item so a later attempt finds the entry realized.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
    private bool IsMenuItemReadyForSelection(object target)
    {
        if (ViewModel.CurrentMenu == null)
            return false;

        // Top-level entries, and nested ones whose mode does not publish a parent, are left to
        // the navigation view as before. A closed pane shows children in flyouts; expanding a
        // container there would open one, so nothing is realized ahead of the selection.
        if (target is not IMenuItem { ParentMenuItem: { } parent } menuItem || !navigationView.IsPaneOpen)
            return true;

        try
        {
            var ancestors = new List<IMenuItem>();

            for (var ancestor = parent; ancestor != null; ancestor = ancestor.ParentMenuItem)
            {
                ancestors.Add(ancestor);
            }

            for (var index = ancestors.Count - 1; index >= 0; index--)
            {
                // The mode decides what is expanded. An ancestor it left collapsed is not forced
                // open here; that selection goes to the navigation view as before.
                if (!ancestors[index].IsExpanded)
                    return true;

                if (navigationView.ContainerFromMenuItem(ancestors[index]) is not NavigationViewItem container)
                    return false;

                if (!container.IsExpanded)
                {
                    container.IsExpanded = true;
                    return false;
                }
            }

            return navigationView.ContainerFromMenuItem(menuItem) != null;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            Log.Debug(ex, "Navigation pane container lookup failed for {MenuItemType}.", target.GetType().Name);
            return false;
        }
    }

    private bool SetNavigationSelection(object? target)
    {
        _isApplyingMenuSelection = true;

        try
        {
            navigationView.SelectedItem = target;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            // ArgumentOutOfRangeException ("invalid vector subscript") when the item's index
            // path is not realized yet, COMException when the pane is being torn down.
            Log.Warning(ex, "Navigation pane rejected the selection of {MenuItemType}.", target?.GetType().Name);
            return false;
        }
        finally
        {
            _isApplyingMenuSelection = false;
        }
    }

    private void ShellFrameContentNavigated(object sender, NavigationEventArgs e)
        => NotifyTitleBarContentChanged();

    /// <summary>
    /// The title bar is shell chrome, so the shell keeps it in step with the state the
    /// modes publish. It does not need to know what any of these values mean.
    /// </summary>
    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName is nameof(IStatePersistanceService.CalendarDisplayType)
            or nameof(IStatePersistanceService.DayDisplayCount)
            or nameof(IStatePersistanceService.IsEventDetailsVisible))
        {
            NotifyTitleBarContentChanged();
        }
    }

    private void PreferencesServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.IsNavigationPaneOpened))
        {
            ApplyNavigationPaneOpenPreference();
            return;
        }

        // Account items switch between the full and compact template, which the template
        // selector only re-evaluates when the item source is rebound.
        if (propertyName != nameof(IPreferencesService.IsCompactAccountMenuItemEnabled))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPreparedForWindowClose)
                return;

            var menu = ViewModel.CurrentMenu;

            // Dropping the source clears the navigation view's selection. That is not a user
            // choice, so it must not reach the mode; the mode's selection is applied again once
            // the containers are rebuilt.
            _isApplyingMenuSelection = true;

            try
            {
                navigationView.MenuItemsSource = null;
                navigationView.MenuItemsSource = menu?.Items;
            }
            finally
            {
                _isApplyingMenuSelection = false;
            }

            RequestMenuSelectionSync(defer: true);
        });
    }

    private void ApplyNavigationPaneOpenPreference()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyNavigationPaneOpenPreference);
            return;
        }

        if (_isPreparedForWindowClose)
            return;

        try
        {
            navigationView.IsPaneOpen = ViewModel.PreferencesService.IsNavigationPaneOpened;
        }
        catch (COMException ex)
        {
            // The title bar can toggle the preference while this window's content is torn down.
            Log.Debug(ex, "Navigation pane state could not be applied to a closing shell.");
        }
    }

    #endregion

    #region View level requests

    /// <summary>
    /// Choosing which mode a newly created account should land in is shell policy.
    /// </summary>
    public void Receive(AccountCreatedMessage message)
    {
        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            var targetMode = message.Account.IsMailAccessGranted
                ? MailCalApplicationMode.Mail
                : message.Account.IsCalendarAccessGranted
                    ? MailCalApplicationMode.Calendar
                    : message.Account.IsTaskAccessGranted
                        ? MailCalApplicationMode.Tasks
                        : MailCalApplicationMode.Contacts;

            if (targetMode == MailCalApplicationMode.Mail &&
                ViewModel.GetProvider(MailCalApplicationMode.Mail) is IMailShellClient mailClient)
            {
                await mailClient.HandleAccountCreatedAsync(message.Account);
            }

            ViewModel.NavigationService.ChangeApplicationMode(targetMode);
        });
    }

    /// <summary>
    /// The account picker anchors to a navigation item container, which only the shell can
    /// resolve. Everything it does afterwards belongs to the mail mode view model.
    /// </summary>
    public void Receive(CreateNewMailWithMultipleAccountsRequested message)
    {
        if (ViewModel.CurrentMode != MailCalApplicationMode.Mail ||
            ViewModel.GetProvider(MailCalApplicationMode.Mail) is not IMailShellClient mailClient)
        {
            return;
        }

        var container = navigationView.ContainerFromMenuItem(mailClient.CreatePrimaryMenuItem);
        var flyout = new AccountSelectorFlyout(message.AllAccounts, mailClient.CreateNewMailForAsync);

        flyout.ShowAt(container, new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Auto,
            Placement = FlyoutPlacementMode.Right
        });
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<CreateNewMailWithMultipleAccountsRequested>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<CreateNewMailWithMultipleAccountsRequested>(this);
    }

    #endregion

    #region Pane layout

    // Opening and Closing rather than Opened and Closed: the latter pair only arrives once
    // the pane animation has finished, which is late enough to show the wide entries being
    // squeezed into the icon strip before they are dropped. The target state is passed in
    // explicitly because IsPaneOpen has not settled yet while these fire.
    private void NavigationPaneOpening(NavigationView sender, object args)
        => UpdatePaneLayout(sender.DisplayMode, isPaneOpen: true);

    private void NavigationPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
        => UpdatePaneLayout(sender.DisplayMode, isPaneOpen: false);

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        ApplyRememberedPaneState(args.DisplayMode);
        UpdatePaneLayout(args.DisplayMode, sender.IsPaneOpen);
    }

    /// <summary>
    /// An expanded pane opens or stays closed as the user last left it, across launches and
    /// window resizes. A narrow window starts with the pane closed, because an open compact or
    /// minimal pane covers the content; the title bar button still opens it there.
    /// The display mode changes during the navigation view's own measure pass, and changing
    /// IsPaneOpen inside it leaves the pane at its open width with the compact items clipped,
    /// so a correction is applied on the next dispatcher tick.
    /// </summary>
    private void ApplyRememberedPaneState(NavigationViewDisplayMode displayMode)
    {
        var shouldBeOpen = displayMode == NavigationViewDisplayMode.Expanded
                           && ViewModel.PreferencesService.IsNavigationPaneOpened;

        if (_isPreparedForWindowClose || navigationView.IsPaneOpen == shouldBeOpen)
            return;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_isPreparedForWindowClose || navigationView.DisplayMode != displayMode)
                return;

            navigationView.IsPaneOpen = shouldBeOpen;
        });
    }

    private void UpdatePaneLayout(NavigationViewDisplayMode displayMode, bool isPaneOpen)
    {
        InnerShellFrame.Margin = displayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(7, 0, 0, 0)
            : new Thickness(0);

        // A closed pane renders its items as an icon-only strip regardless of display mode.
        ViewModel.SetPaneCompact(!isPaneOpen);
    }

    #endregion

    private static void NotifyTitleBarContentChanged()
        => WeakReferenceMessenger.Default.Send(new TitleBarShellContentUpdated());
}

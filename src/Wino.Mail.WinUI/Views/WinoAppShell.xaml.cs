#nullable enable

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.MenuFlyouts;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Mail;

namespace Wino.Mail.WinUI.Views;

/// <summary>
/// Hosts the navigation pane and the inner frame. It knows which mode is active and
/// nothing else: the menu it shows is published by whatever page the inner frame navigated
/// to, and every interaction is forwarded straight back to that page's mode view model.
/// </summary>
public sealed partial class WinoAppShell : Views.Abstract.WinoAppShellAbstract,
    IShellHost,
    IShellMenuSink,
    IWinoFrameProvider,
    IRecipient<AccountCreatedMessage>,
    IRecipient<CreateNewMailWithMultipleAccountsRequested>
{
    private readonly WinUIDispatcher _pageDispatcher;
    private WinoApplicationMode? _activeMode;
    private bool _isPreparedForWindowClose;

    public WinoAppShell()
    {
        InitializeComponent();

        _pageDispatcher = new WinUIDispatcher(DispatcherQueue);
        ViewModel.PreferencesService.PreferenceChanged += PreferencesServiceChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
    }

    public bool HasShellContent => InnerShellFrame.Content != null;

    public Frame? GetFrame(NavigationReferenceFrame frameType)
        => frameType switch
        {
            NavigationReferenceFrame.InnerShellFrame => InnerShellFrame,
            NavigationReferenceFrame.RenderingFrame => (InnerShellFrame.Content as IWinoFrameProvider)?.GetFrame(frameType),
            _ => null
        };

    #region Mode activation

    public void ActivateMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
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

        UnregisterRecipients();
        Bindings.StopTracking();
    }

    private void DetachShellMenuBindings()
    {
        ViewModel.SetShellMenu(null);

        // x:Bind notifications are synchronous today, but assign the WinRT properties as
        // well so teardown does not depend on binding-engine timing.
        navigationView.SelectedItem = null;
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
    }

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
        if (!ViewModel.HandlesSelection || args.SelectedItem is not IMenuItem selectedMenuItem)
            return;

        await ViewModel.ChangeMenuSelectionAsync(selectedMenuItem);
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
        // Account items switch between the full and compact template, which the template
        // selector only re-evaluates when the item source is rebound.
        if (propertyName != nameof(IPreferencesService.IsCompactAccountMenuItemEnabled))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var menu = ViewModel.CurrentMenu;

            navigationView.MenuItemsSource = null;
            navigationView.MenuItemsSource = menu?.Items;
        });
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
                ? WinoApplicationMode.Mail
                : message.Account.IsCalendarAccessGranted
                    ? WinoApplicationMode.Calendar
                    : message.Account.IsTaskAccessGranted
                        ? WinoApplicationMode.Tasks
                        : WinoApplicationMode.Contacts;

            if (targetMode == WinoApplicationMode.Mail &&
                ViewModel.GetProvider(WinoApplicationMode.Mail) is IMailShellClient mailClient)
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
        if (ViewModel.CurrentMode != WinoApplicationMode.Mail ||
            ViewModel.GetProvider(WinoApplicationMode.Mail) is not IMailShellClient mailClient)
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
        => UpdatePaneLayout(args.DisplayMode, sender.IsPaneOpen);

    private void UpdatePaneLayout(NavigationViewDisplayMode displayMode, bool isPaneOpen)
    {
        InnerShellFrame.Margin = displayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(7, 0, 0, 0)
            : new Thickness(0);

        // A closed pane renders its items as an icon-only strip regardless of display mode.
        ViewModel.SetPaneCompact(!isPaneOpen);
    }

    #endregion

    #region Keyboard shortcuts

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.RepeatCount > 1 || ShouldIgnoreShortcut())
            return;

        var key = NormalizeKey(e.Key);
        if (string.IsNullOrEmpty(key))
            return;

        var mode = ViewModel.CurrentMode;
        var modifierKeys = GetCurrentModifierKeys();

        if (IsReservedUndoShortcut(mode, key, modifierKeys))
            return;

        var shortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
        var shortcut = await shortcutService.GetShortcutForKeyAsync(mode, key, modifierKeys);

        if (shortcut == null)
            return;

        var details = new KeyboardShortcutTriggerDetails
        {
            ShortcutId = shortcut.Id,
            Mode = shortcut.Mode,
            Action = shortcut.Action,
            Key = shortcut.Key,
            ModifierKeys = shortcut.ModifierKeys,
            Sender = sender,
            Origin = FocusManager.GetFocusedElement(XamlRoot)
        };

        await ViewModel.KeyboardShortcutHookForMode(details);

        if (InnerShellFrame.Content is BasePage activePage && activePage.AssociatedViewModel != null)
        {
            await activePage.AssociatedViewModel.KeyboardShortcutHook(details);
        }

        if (details.Handled)
        {
            e.Handled = true;
        }
    }

    private bool ShouldIgnoreShortcut()
    {
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot);

        if (focusedElement is TextBox or AutoSuggestBox or PasswordBox or RichEditBox or ComboBox)
            return true;

        if (focusedElement is FrameworkElement frameworkElement)
        {
            var typeName = frameworkElement.GetType().Name;
            if (typeName.Contains("WebView", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ModifierKeys GetCurrentModifierKeys()
    {
        var modifiers = ModifierKeys.None;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Control;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Alt;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Shift;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
    }

    private static string NormalizeKey(Windows.System.VirtualKey key)
        => key switch
        {
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows => string.Empty,
            _ => key.ToString()
        };

    private static bool IsReservedUndoShortcut(WinoApplicationMode mode, string key, ModifierKeys modifierKeys)
        => mode == WinoApplicationMode.Mail
           && modifierKeys == ModifierKeys.Control
           && string.Equals(key, "Z", StringComparison.OrdinalIgnoreCase);

    #endregion

    private static void NotifyTitleBarContentChanged()
        => WeakReferenceMessenger.Default.Send(new TitleBarShellContentUpdated());
}

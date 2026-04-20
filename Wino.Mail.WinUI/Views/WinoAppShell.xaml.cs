using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Calendar.ViewModels;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.ViewModels;
using Wino.Controls;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Helpers;
using Wino.Helpers;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Contacts;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views;

namespace Wino.Mail.WinUI.Views;

public sealed partial class WinoAppShell : Views.Abstract.WinoAppShellAbstract,
    IShellHost,
    IRecipient<AccountMenuItemExtended>,
    IRecipient<NavigateMailFolderEvent>,
    IRecipient<CreateNewMailWithMultipleAccountsRequested>,
    IRecipient<CalendarDisplayTypeChangedMessage>,
    IRecipient<AccountCreatedMessage>
{
    private const string StateDefaultShellContent = "DefaultShellContentState";
    private const string StateEventDetailsContent = "EventDetailsContentState";
    private const int PaneCustomContentRowIndex = 4;
    private const int PaneItemsContainerRowIndex = 6;
    private WinoApplicationMode? _activeMode;
    private bool _isSyncingNavigationViewSelection;
    private bool _isSynchronizingVisibleDateRangeCalendar;
    private bool _isPreparedForWindowClose;
    private Grid? _paneContentGrid;
    private RowDefinition? _paneCustomContentRowDefinition;
    private RowDefinition? _paneItemsContainerRowDefinition;

    public WinoAppShell()
    {
        InitializeComponent();

        var pageDispatcher = new WinUIDispatcher(DispatcherQueue);
        ViewModel.MailClient.Dispatcher = pageDispatcher;
        ViewModel.CalendarClient.Dispatcher = pageDispatcher;
        ViewModel.GetClient(WinoApplicationMode.Contacts).Dispatcher = pageDispatcher;
        ViewModel.GetClient(WinoApplicationMode.Settings).Dispatcher = pageDispatcher;

        ViewModel.MailClient.PropertyChanged += MailClientPropertyChanged;
        ViewModel.CalendarClient.PropertyChanged += CalendarClientPropertyChanged;
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        ViewModel.PreferencesService.PreferenceChanged += PreferencesServiceChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;

        InitializeCalendarControls();
        UpdateEventDetailsVisualState();
    }

    public bool HasShellContent => InnerShellFrame.Content != null;

    public Frame GetShellFrame() => InnerShellFrame;

    public void ActivateMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
    {
        if (_activeMode == mode && InnerShellFrame.Content != null)
        {
            if (activationContext.Parameter != null)
            {
                ViewModel.SetCurrentMode(mode);
                ViewModel.CurrentClient.Activate(activationContext);
                NotifyTitleBarContentChanged();
            }

            return;
        }

        DeactivateCurrentMode();
        ResetShellModeNavigationState();

        _activeMode = mode;
        ViewModel.SetCurrentMode(mode);

        RefreshNavigationViewBindings(syncMailSelection: mode != WinoApplicationMode.Mail);

        ApplyModeLayout();
        UpdateTitleBarSubtitle();

        ViewModel.CurrentClient.Activate(activationContext);
        ResetShellModeNavigationState();
        NotifyTitleBarContentChanged();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (!_isPreparedForWindowClose)
        {
            DeactivateCurrentMode();
            DetachLifetimeSubscriptions();
        }

        Bindings.StopTracking();
    }

    public void PrepareForWindowClose()
    {
        if (_isPreparedForWindowClose)
            return;

        _isPreparedForWindowClose = true;

        DeactivateAllShellClients();
        WeakReferenceMessenger.Default.Unregister<LanguageChanged>(this);
        UnregisterRecipients();
        DetachLifetimeSubscriptions();
        Bindings.StopTracking();

        navigationView.MenuItemsSource = null;
        CalendarHostListView.ItemsSource = null;
        WindowCleanupHelper.CleanupFrame(InnerShellFrame);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNavigationPaneLayout(navigationView.DisplayMode);
        RefreshNavigationViewBindings();
        RefreshCalendarControls();

        if (_activeMode == null)
        {
            ActivateMode(ViewModel.StatePersistenceService.ApplicationMode, new ShellModeActivationContext
            {
                IsInitialActivation = true
            });
        }
    }

    private void ApplyModeLayout()
    {
        RefreshCalendarControls();
        UpdateEventDetailsVisualState();
        UpdateTitleBarSubtitle();
        UpdateNavigationPaneLayout(navigationView.DisplayMode);
        NotifyTitleBarContentChanged();
    }

    private void DeactivateCurrentMode()
    {
        if (_activeMode == WinoApplicationMode.Mail)
        {
            WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
            WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
            ViewModel.StatePersistenceService.IsReadingMail = false;
            ViewModel.MailClient.Deactivate();
        }
        else if (_activeMode == WinoApplicationMode.Calendar)
        {
            ViewModel.StatePersistenceService.IsEventDetailsVisible = false;
            ViewModel.CalendarClient.Deactivate();
            WindowCleanupHelper.CleanupFrame(InnerShellFrame);
            GC.Collect();
        }
        else if (_activeMode == WinoApplicationMode.Contacts)
        {
            ViewModel.CurrentClient.Deactivate();
        }
        else if (_activeMode == WinoApplicationMode.Settings)
        {
            if (InnerShellFrame.Content is SettingsPage settingsPage)
            {
                settingsPage.ResetForModeSwitch();
            }

            ViewModel.CurrentClient.Deactivate();
        }
    }

    private void DeactivateAllShellClients()
    {
        ViewModel.StatePersistenceService.IsReadingMail = false;
        ViewModel.StatePersistenceService.IsEventDetailsVisible = false;

        ViewModel.MailClient.Deactivate();
        ViewModel.CalendarClient.Deactivate();

        if (ViewModel.GetClient(WinoApplicationMode.Contacts) is ContactsShellClient contactsClient)
        {
            contactsClient.PrepareForShellShutdown();
        }

        if (ViewModel.GetClient(WinoApplicationMode.Settings) is SettingsShellClient settingsClient)
        {
            settingsClient.PrepareForShellShutdown();
        }

        if (ViewModel.MailClient is MailAppShellViewModel mailClient)
        {
            mailClient.PrepareForShellShutdown();
        }

        if (ViewModel.CalendarClient is CalendarAppShellViewModel calendarClient)
        {
            calendarClient.PrepareForShellShutdown();
        }
    }

    private void DetachLifetimeSubscriptions()
    {
        ViewModel.MailClient.PropertyChanged -= MailClientPropertyChanged;
        ViewModel.CalendarClient.PropertyChanged -= CalendarClientPropertyChanged;
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged -= StatePersistenceServiceChanged;
    }

    private void ResetShellModeNavigationState()
    {
        InnerShellFrame.BackStack.Clear();
        InnerShellFrame.ForwardStack.Clear();
    }

    private void UpdateTitleBarSubtitle()
    {
        if (ViewModel.IsContactsMode)
        {
            ViewModel.StatePersistenceService.CoreWindowTitle = string.Empty;
            return;
        }

        if (ViewModel.IsCalendarMode)
        {
            ViewModel.StatePersistenceService.CoreWindowTitle = string.Empty;
            return;
        }

        ViewModel.StatePersistenceService.CoreWindowTitle = string.Empty;
    }

    private void InitializeCalendarControls()
    {
        CalendarHostListView.ItemsSource = ViewModel.CalendarClient.GroupedAccountCalendars;

        RefreshCalendarControls();
    }

    private void RefreshCalendarControls()
    {
        CalendarHostListView.ItemsSource = ViewModel.CalendarClient.GroupedAccountCalendars;
        SynchronizeVisibleDateRangeCalendar();
    }

    private void RefreshNavigationViewBindings(bool syncMailSelection = true)
    {
        navigationView.MenuItemsSource = ViewModel.CurrentMenuItems;
        SetNavigationViewSelectedItem(ViewModel.CurrentClient.HandlesNavigationSelection && syncMailSelection
            ? ViewModel.SelectedMenuItem
            : null);
    }

    private void UpdateEventDetailsVisualState()
    {
        VisualStateManager.GoToState(this,
            ViewModel.StatePersistenceService.IsEventDetailsVisible ? StateEventDetailsContent : StateDefaultShellContent,
            false);
    }

    private async void NewCalendarEventNavigationItemTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        await InvokeNewCalendarEventAsync();
    }

    private async void AttentionIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountMenuItem accountMenuItem })
            return;

        if (ViewModel.MailClient is MailAppShellViewModel mailClient)
        {
            await mailClient.HandleAccountAttentionAsync(accountMenuItem.Parameter);
        }
    }

    private async void NewCalendarEventNavigationItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
            return;

        e.Handled = true;
        await InvokeNewCalendarEventAsync();
    }

    private Task InvokeNewCalendarEventAsync()
        => ViewModel.CalendarClient.HandleNavigationItemInvokedAsync(new NewCalendarEventMenuItem());

    private void NewContactNavigationItemTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        InvokeNewContact();
    }

    private void NewContactNavigationItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
            return;

        e.Handled = true;
        InvokeNewContact();
    }

    private static void InvokeNewContact()
        => WeakReferenceMessenger.Default.Send(new NewContactRequested());

    private async void SynchronizeCalendarsNavigationItemTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        await InvokeCalendarSynchronizationAsync();
    }

    private async void SynchronizeCalendarsNavigationItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
            return;

        e.Handled = true;
        await InvokeCalendarSynchronizationAsync();
    }

    private Task InvokeCalendarSynchronizationAsync()
    {
        if (ViewModel.CalendarClient.SyncCommand.CanExecute(null))
        {
            ViewModel.CalendarClient.SyncCommand.Execute(null);
        }

        return Task.CompletedTask;
    }

    public void Receive(CalendarDisplayTypeChangedMessage message) => NotifyTitleBarContentChanged();

    public void Receive(AccountCreatedMessage message)
    {
        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            await ViewModel.MailClient.HandleAccountCreatedAsync(message.Account);

            var targetMode = !message.Account.IsMailAccessGranted && message.Account.IsCalendarAccessGranted
                ? WinoApplicationMode.Calendar
                : WinoApplicationMode.Mail;

            ViewModel.NavigationService.ChangeApplicationMode(targetMode);
        });
    }

    private async void NavigationViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_isSyncingNavigationViewSelection)
            return;

        if (ViewModel.IsCalendarMode)
        {
            if (args.InvokedItemContainer is FrameworkElement { DataContext: IMenuItem menuItem })
            {
                await ViewModel.CalendarClient.HandleNavigationItemInvokedAsync(menuItem);
            }

            return;
        }

        if (args.InvokedItemContainer is WinoNavigationViewItem winoNavigationViewItem)
        {
            if (winoNavigationViewItem.SelectsOnInvoked)
                return;

            await ViewModel.CurrentClient.HandleNavigationItemInvokedAsync(winoNavigationViewItem.DataContext as IMenuItem);
        }
    }

    private async void MenuSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingNavigationViewSelection)
            return;

        if (!ViewModel.CurrentClient.HandlesNavigationSelection)
            return;

        if (ReferenceEquals(args.SelectedItem, ViewModel.SelectedMenuItem))
            return;

        if (args.SelectedItem is IMenuItem invokedMenuItem)
        {
            await ViewModel.CurrentClient.HandleNavigationSelectionChangedAsync(invokedMenuItem);
        }
    }

    public void Receive(AccountMenuItemExtended message)
    {
        if (!ViewModel.IsMailMode)
            return;

        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            if (message.FolderId == default)
                return;

            if (ViewModel.MailClient.MenuItems!.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem foundMenuItem))
            {
                foundMenuItem.Expand();
                await ViewModel.MailClient.NavigateFolderAsync(foundMenuItem);
                SetNavigationViewSelectedItem(foundMenuItem);

                if (message.NavigateMailItem != null)
                {
                    WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(message.NavigateMailItem.UniqueId, ScrollToItem: true));
                }

                return;
            }

            if (message.NavigateMailItem == null)
                return;

            if (ViewModel.MailClient.MenuItems!.TryGetAccountMenuItem(message.NavigateMailItem.AssignedAccount.Id, out IAccountMenuItem accountMenuItem))
            {
                await ViewModel.MailClient.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

                if (ViewModel.MailClient.MenuItems!.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem accountFolderMenuItem))
                {
                    accountFolderMenuItem.Expand();
                    await ViewModel.MailClient.NavigateFolderAsync(accountFolderMenuItem);
                    SetNavigationViewSelectedItem(accountFolderMenuItem);
                    WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(message.NavigateMailItem.UniqueId, ScrollToItem: true));
                }
            }
        });
    }

    public void Receive(NavigateMailFolderEvent message)
    {
        if (!ViewModel.IsMailMode || message.BaseFolderMenuItem == null)
            return;

        if (navigationView.SelectedItem != message.BaseFolderMenuItem)
        {
            var navigateFolderArgs = new NavigateMailFolderEventArgs(message.BaseFolderMenuItem, message.FolderInitLoadAwaitTask);

            ViewModel.NavigationService.Navigate(WinoPage.MailListPage, navigateFolderArgs, NavigationReferenceFrame.InnerShellFrame);

            SetNavigationViewSelectedItem(message.BaseFolderMenuItem);
        }
        else
        {
            message.FolderInitLoadAwaitTask?.TrySetResult(true);
        }
    }

    private void ShellFrameContentNavigated(object sender, NavigationEventArgs e)
    {
        NotifyTitleBarContentChanged();

        if (ViewModel.IsMailMode)
        {
            RefreshNavigationViewBindings();
        }
    }

    private async void MenuItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!ViewModel.IsMailMode)
            return;

        if (sender is WinoNavigationViewItem menuItem &&
            menuItem.DataContext is IBaseFolderMenuItem baseFolderMenuItem &&
            baseFolderMenuItem.IsMoveTarget &&
            args.TryGetPosition(sender, out Point p))
        {
            args.Handled = true;

            var source = new TaskCompletionSource<FolderOperationMenuItem>();
            var actions = ViewModel.MailClient.GetFolderContextMenuActions(baseFolderMenuItem);
            var flyout = new FolderOperationFlyout(actions, source);

            flyout.ShowAt(menuItem, new FlyoutShowOptions
            {
                ShowMode = FlyoutShowMode.Standard,
                Position = new Point(p.X + 30, p.Y - 20)
            });

            var operation = await source.Task;
            flyout.Dispose();

            if (operation != null)
            {
                await ViewModel.MailClient.PerformFolderOperationAsync(operation.Operation, baseFolderMenuItem);
            }
        }
    }

    private void AccountMenuItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!ViewModel.IsMailMode)
            return;

        if (sender is not AccountNavigationItem accountNavigationItem ||
            accountNavigationItem.DataContext is not AccountMenuItem accountMenuItem ||
            !args.TryGetPosition(sender, out Point p))
        {
            return;
        }

        args.Handled = true;

        var flyout = new WinoMenuFlyout();

        var manageAccountSettingsItem = new MenuFlyoutItem
        {
            Text = Translator.AccountContextMenu_ManageAccountSettings
        };
        manageAccountSettingsItem.Icon = new WinoFontIcon { Icon = WinoIconGlyph.ManageAccounts };
        manageAccountSettingsItem.Click += (_, _) => NavigateToAccountSettings(accountMenuItem);
        flyout.Items.Add(manageAccountSettingsItem);

        var createFolderItem = new MenuFlyoutItem
        {
            Text = Translator.AccountContextMenu_CreateFolder
        };
        createFolderItem.Icon = new WinoFontIcon { Icon = WinoIconGlyph.CreateFolder };
        createFolderItem.Click += async (_, _) => await ViewModel.MailClient.CreateRootFolderAsync(accountMenuItem);
        flyout.Items.Add(createFolderItem);

        flyout.ShowAt(accountNavigationItem, new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Standard,
            Position = new Point(p.X + 30, p.Y - 20)
        });
    }

    private void NavigateToAccountSettings(AccountMenuItem accountMenuItem)
    {
        ViewModel.NavigationService.ChangeApplicationMode(
            WinoApplicationMode.Settings,
            new ShellModeActivationContext
            {
                Parameter = WinoPage.ManageAccountsPage,
                SuppressStartupFlows = true
            });

        _ = DispatcherQueue.EnqueueAsync(() =>
        {
            WeakReferenceMessenger.Default.Send(new SettingsRootNavigationRequested(WinoPage.ManageAccountsPage));
            WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(
                accountMenuItem.AccountName,
                WinoPage.AccountDetailsPage,
                accountMenuItem.AccountId));
        });
    }

    public void Receive(CreateNewMailWithMultipleAccountsRequested message)
    {
        if (!ViewModel.IsMailMode)
            return;

        var container = navigationView.ContainerFromMenuItem(ViewModel.MailClient.CreatePrimaryMenuItem);
        var flyout = new AccountSelectorFlyout(message.AllAccounts, ViewModel.MailClient.CreateNewMailForAsync);

        flyout.ShowAt(container, new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Auto,
            Placement = FlyoutPlacementMode.Right
        });
    }

    private void NavigationPaneOpening(NavigationView sender, object args)
    {
        if (!ViewModel.IsMailMode)
            return;

        if (sender.DisplayMode == NavigationViewDisplayMode.Minimal && sender.SelectedItem is IFolderMenuItem selectedFolderMenuItem)
        {
            selectedFolderMenuItem.Expand();
        }
    }

    private void NavigationPaneOpened(NavigationView sender, object args)
        => UpdateNavigationPaneLayout(sender.DisplayMode);

    private void NavigationPaneClosed(NavigationView sender, object args)
        => UpdateNavigationPaneLayout(sender.DisplayMode);

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => UpdateNavigationPaneLayout(args.DisplayMode);

    private void MailClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IShellClient.SelectedMenuItem) && ViewModel.IsMailMode)
        {
            SetNavigationViewSelectedItem(ViewModel.MailClient.SelectedMenuItem);
        }
    }

    private void CalendarClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var enqueued = DispatcherQueue.TryEnqueue(() => CalendarClientPropertyChanged(sender, e));

            if (!enqueued)
                throw new InvalidOperationException("Could not marshal calendar property changes onto the UI thread.");

            return;
        }

        if (e.PropertyName == nameof(ICalendarShellClient.CurrentVisibleRange) ||
            e.PropertyName == nameof(ICalendarShellClient.VisibleDateRangeText))
        {
            SynchronizeVisibleDateRangeCalendar();
            UpdateTitleBarSubtitle();
        }
    }

    private void VisibleDateRangeCalendarViewSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_isSynchronizingVisibleDateRangeCalendar)
            return;

        DateTimeOffset? interactedDate = null;

        if (args.AddedDates.Count > 0)
        {
            interactedDate = args.AddedDates[0];
        }
        else if (args.RemovedDates.Count > 0)
        {
            interactedDate = args.RemovedDates[0];
        }

        if (interactedDate is null)
            return;

        var clickedArgs = new CalendarViewDayClickedEventArgs(interactedDate.Value.DateTime);

        if (ViewModel.CalendarClient.DateClickedCommand.CanExecute(clickedArgs))
        {
            ViewModel.CalendarClient.DateClickedCommand.Execute(clickedArgs);
        }
    }

    private void SynchronizeVisibleDateRangeCalendar()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var enqueued = DispatcherQueue.TryEnqueue(SynchronizeVisibleDateRangeCalendar);

            if (!enqueued)
                throw new InvalidOperationException("Could not marshal visible date range calendar synchronization onto the UI thread.");

            return;
        }

        _isSynchronizingVisibleDateRangeCalendar = true;

        try
        {
            VisibleDateRangeCalendarView.FirstDayOfWeek = MapFirstDayOfWeek(ViewModel.PreferencesService.FirstDayOfWeek);
            VisibleDateRangeCalendarView.SelectedDates.Clear();

            var currentRange = ViewModel.CalendarClient.CurrentVisibleRange;
            if (currentRange == null)
                return;

            foreach (var date in currentRange.Dates)
            {
                VisibleDateRangeCalendarView.SelectedDates.Add(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)));
            }

            VisibleDateRangeCalendarView.SetDisplayDate(new DateTimeOffset(currentRange.AnchorDate.ToDateTime(TimeOnly.MinValue)));
        }
        finally
        {
            _isSynchronizingVisibleDateRangeCalendar = false;
        }
    }

    private void PreferencesServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.FirstDayOfWeek))
        {
            SynchronizeVisibleDateRangeCalendar();
        }
    }

    private static Windows.Globalization.DayOfWeek MapFirstDayOfWeek(DayOfWeek dayOfWeek)
        => dayOfWeek switch
        {
            DayOfWeek.Sunday => Windows.Globalization.DayOfWeek.Sunday,
            DayOfWeek.Monday => Windows.Globalization.DayOfWeek.Monday,
            DayOfWeek.Tuesday => Windows.Globalization.DayOfWeek.Tuesday,
            DayOfWeek.Wednesday => Windows.Globalization.DayOfWeek.Wednesday,
            DayOfWeek.Thursday => Windows.Globalization.DayOfWeek.Thursday,
            DayOfWeek.Friday => Windows.Globalization.DayOfWeek.Friday,
            DayOfWeek.Saturday => Windows.Globalization.DayOfWeek.Saturday,
            _ => Windows.Globalization.DayOfWeek.Monday
        };

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModel.SelectedMenuItem) || !ViewModel.CurrentClient.HandlesNavigationSelection)
            return;

        SetNavigationViewSelectedItem(ViewModel.SelectedMenuItem);
    }

    private void SetNavigationViewSelectedItem(object? item)
    {
        if (ReferenceEquals(navigationView.SelectedItem, item))
            return;

        _isSyncingNavigationViewSelection = true;
        try
        {
            navigationView.SelectedItem = item;
        }
        finally
        {
            _isSyncingNavigationViewSelection = false;
        }
    }

    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IStatePersistanceService.CalendarDisplayType))
        {
            NotifyTitleBarContentChanged();
            return;
        }

        if (propertyName == nameof(IStatePersistanceService.DayDisplayCount))
        {
            NotifyTitleBarContentChanged();
            return;
        }

        if (propertyName == nameof(IStatePersistanceService.IsEventDetailsVisible))
        {
            UpdateEventDetailsVisualState();
            NotifyTitleBarContentChanged();
        }
    }

    private static void NotifyTitleBarContentChanged()
        => WeakReferenceMessenger.Default.Send(new TitleBarShellContentUpdated());

    private void UpdateNavigationPaneLayout(NavigationViewDisplayMode displayMode)
    {
        EnsureNavigationPaneLayoutParts();

        bool shouldStretchCustomPane = displayMode == NavigationViewDisplayMode.Expanded
                                       && navigationView.IsPaneOpen
                                       && (ViewModel.IsCalendarMode || ViewModel.IsContactsMode);

        if (_paneCustomContentRowDefinition != null && _paneItemsContainerRowDefinition != null)
        {
            _paneCustomContentRowDefinition.Height = shouldStretchCustomPane
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            _paneItemsContainerRowDefinition.Height = shouldStretchCustomPane
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
        }

        if (displayMode == NavigationViewDisplayMode.Expanded && navigationView.IsPaneOpen)
        {
            if (ViewModel.IsCalendarMode)
            {
                PaneCustomContent.Visibility = Visibility.Visible;
                CalendarPaneContent.Visibility = Visibility.Visible;
                ContactsPaneContent.Visibility = Visibility.Collapsed;
                InnerShellFrame.Margin = new Thickness(0);
                return;
            }

            if (ViewModel.IsContactsMode)
            {
                PaneCustomContent.Visibility = Visibility.Visible;
                CalendarPaneContent.Visibility = Visibility.Collapsed;
                ContactsPaneContent.Visibility = Visibility.Visible;
                InnerShellFrame.Margin = new Thickness(0);
                return;
            }
        }

        CalendarPaneContent.Visibility = Visibility.Collapsed;
        ContactsPaneContent.Visibility = Visibility.Collapsed;
        PaneCustomContent.Visibility = Visibility.Collapsed;
        InnerShellFrame.Margin = displayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(7, 0, 0, 0)
            : new Thickness(0);
    }

    private void EnsureNavigationPaneLayoutParts()
    {
        _paneContentGrid ??= WinoVisualTreeHelper.GetChildObject<Grid>(navigationView, "PaneContentGrid");

        if (_paneContentGrid == null || _paneContentGrid.RowDefinitions.Count <= PaneItemsContainerRowIndex)
            return;

        _paneCustomContentRowDefinition ??= _paneContentGrid.RowDefinitions[PaneCustomContentRowIndex];
        _paneItemsContainerRowDefinition ??= _paneContentGrid.RowDefinitions[PaneItemsContainerRowIndex];
    }

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.RepeatCount > 1 || ShouldIgnoreShortcut())
            return;

        var key = NormalizeKey(e.Key);
        if (string.IsNullOrEmpty(key))
            return;

        var mode = ViewModel.CurrentMode;
        var shortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
        var shortcut = await shortcutService.GetShortcutForKeyAsync(mode, key, GetCurrentModifierKeys());

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

        await ViewModel.CurrentClient.KeyboardShortcutHook(details);

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

    private async void ItemDroppedOnFolder(object sender, DragEventArgs e)
    {
        if (sender is WinoNavigationViewItem droppedContainer)
        {
            droppedContainer.IsDraggingItemOver = false;

            if (CanContinueDragDrop(droppedContainer, e) && droppedContainer.DataContext is IBaseFolderMenuItem draggingFolder)
            {
                var dragPackage = e.DataView.Properties[nameof(MailDragPackage)] as MailDragPackage;
                if (dragPackage == null)
                    return;

                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                var mailCopies = ExtractMailCopies(dragPackage).ToList();
                await ViewModel.MailClient.PerformMoveOperationAsync(mailCopies, draggingFolder);
            }
        }
    }

    private void ItemDragLeaveFromFolder(object sender, DragEventArgs e)
    {
        if (sender is WinoNavigationViewItem leavingContainer)
        {
            leavingContainer.IsDraggingItemOver = false;
        }
    }

    private bool CanContinueDragDrop(WinoNavigationViewItem interactingContainer, DragEventArgs args)
    {
        if (!ViewModel.IsMailMode || !args.DataView.Properties.ContainsKey(nameof(MailDragPackage)))
            return false;

        var dragPackage = args.DataView.Properties[nameof(MailDragPackage)] as MailDragPackage;
        if (dragPackage == null || !dragPackage.DraggingMails.Any())
            return false;

        if (interactingContainer.IsSelected)
            return false;

        if (interactingContainer.DataContext is not IBaseFolderMenuItem folderMenuItem || !folderMenuItem.IsMoveTarget)
            return false;

        var draggedAccountIds = folderMenuItem.HandlingFolders.Select(a => a.MailAccountId);
        var draggedMails = ExtractMailCopies(dragPackage).ToList();

        return draggedMails.Any() && draggedMails.Any(a => draggedAccountIds.Contains(a.AssignedAccount.Id));
    }

    private static IEnumerable<MailCopy> ExtractMailCopies(MailDragPackage dragPackage)
    {
        foreach (var item in dragPackage.DraggingMails)
        {
            if (item is MailCopy mailCopy)
            {
                yield return mailCopy;
            }
            else if (item is MailItemViewModel singleMailItemViewModel)
            {
                yield return singleMailItemViewModel.MailCopy;
            }
            else if (item is ThreadMailItemViewModel threadViewModel)
            {
                foreach (var threadMail in threadViewModel.ThreadEmails)
                {
                    yield return threadMail.MailCopy;
                }
            }
        }
    }

    private void ItemDragEnterOnFolder(object sender, DragEventArgs e)
    {
        if (sender is WinoNavigationViewItem droppedContainer && CanContinueDragDrop(droppedContainer, e))
        {
            droppedContainer.IsDraggingItemOver = true;

            if (droppedContainer.DataContext is IBaseFolderMenuItem draggingFolder)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.Caption = string.Format(Translator.DragMoveToFolderCaption, draggingFolder.FolderName);
            }
        }
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
    {
        return key switch
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
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<AccountMenuItemExtended>(this);
        WeakReferenceMessenger.Default.Register<CreateNewMailWithMultipleAccountsRequested>(this);
        WeakReferenceMessenger.Default.Register<NavigateMailFolderEvent>(this);
        WeakReferenceMessenger.Default.Register<CalendarDisplayTypeChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<AccountMenuItemExtended>(this);
        WeakReferenceMessenger.Default.Unregister<CreateNewMailWithMultipleAccountsRequested>(this);
        WeakReferenceMessenger.Default.Unregister<NavigateMailFolderEvent>(this);
        WeakReferenceMessenger.Default.Unregister<CalendarDisplayTypeChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AccountCreatedMessage>(this);
    }
}

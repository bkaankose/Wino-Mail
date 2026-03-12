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
using Wino.Calendar.Controls;
using Wino.Calendar.Views;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Mail;
using Wino.Views;
using Wino.Views.Settings;

namespace Wino.Mail.WinUI.Views;

public sealed partial class WinoAppShell : Views.Abstract.WinoAppShellAbstract,
    IShellHost,
    IRecipient<AccountMenuItemExtended>,
    IRecipient<NavigateMailFolderEvent>,
    IRecipient<CreateNewMailWithMultipleAccountsRequested>,
    IRecipient<CalendarDisplayTypeChangedMessage>
{
    private const string StateHorizontalCalendar = "HorizontalCalendar";
    private const string StateVerticalCalendar = "VerticalCalendar";
    private const string StateDefaultShellContent = "DefaultShellContentState";
    private const string StateEventDetailsContent = "EventDetailsContentState";
    private WinoApplicationMode? _activeMode;

    public WinoAppShell()
    {
        InitializeComponent();

        var pageDispatcher = new WinUIDispatcher(DispatcherQueue);
        ViewModel.MailClient.Dispatcher = pageDispatcher;
        ViewModel.CalendarClient.Dispatcher = pageDispatcher;
        ViewModel.GetClient(WinoApplicationMode.Contacts).Dispatcher = pageDispatcher;

        ViewModel.MailClient.PropertyChanged += MailClientPropertyChanged;
        ViewModel.CalendarClient.PropertyChanged += CalendarClientPropertyChanged;
        ViewModel.StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
        CalendarTypeSelector.RegisterPropertyChangedCallback(WinoCalendarTypeSelectorControl.SelectedTypeProperty, CalendarTypeSelectorSelectedTypeChanged);

        InitializeCalendarControls();
        ManageCalendarDisplayType(ViewModel.CalendarClient.StatePersistenceService.CalendarDisplayType);
        UpdateEventDetailsVisualState();
        ApplyTitleBarContent();
    }

    public bool HasShellContent => InnerShellFrame.Content != null;

    public Frame GetShellFrame() => InnerShellFrame;

    public void ActivateMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
    {
        if (_activeMode == mode && InnerShellFrame.Content != null)
            return;

        DeactivateCurrentMode();
        ResetShellModeNavigationState();

        _activeMode = mode;
        ViewModel.SetCurrentMode(mode);

        RefreshNavigationViewBindings(syncMailSelection: mode != WinoApplicationMode.Mail);

        ApplyModeLayout();
        UpdateTitleBarSubtitle();

        ViewModel.CurrentClient.Activate(activationContext);

        ApplyTitleBarContent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DeactivateCurrentMode();
        Bindings.StopTracking();
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
        var isCalendarMode = ViewModel.IsCalendarMode;

        CalendarShellContentRoot.Visibility = isCalendarMode ? Visibility.Visible : Visibility.Collapsed;
        DynamicPageShellContentPresenter.Visibility = isCalendarMode ? Visibility.Collapsed : Visibility.Visible;

        RefreshCalendarControls();
        ManageCalendarDisplayType(ViewModel.CalendarClient.StatePersistenceService.CalendarDisplayType);
        UpdateEventDetailsVisualState();
        UpdateTitleBarSubtitle();
        UpdateNavigationPaneLayout(navigationView.DisplayMode);
        ApplyTitleBarContent();
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
        }
        else if (_activeMode == WinoApplicationMode.Contacts)
        {
            ViewModel.CurrentClient.Deactivate();
        }

        DynamicPageShellContentPresenter.Content = null;
    }

    private void ResetShellModeNavigationState()
    {
        ViewModel.StatePersistenceService.IsManageAccountsNavigating = false;
        ViewModel.StatePersistenceService.IsSettingsNavigating = false;
        InnerShellFrame.BackStack.Clear();
        InnerShellFrame.ForwardStack.Clear();
    }

    private void ApplyTitleBarContent()
    {
        if (ViewModel.IsCalendarMode)
        {
            CalendarShellContentRoot.Visibility = Visibility.Visible;
            DynamicPageShellContentPresenter.Visibility = Visibility.Collapsed;
            return;
        }

        CalendarShellContentRoot.Visibility = Visibility.Collapsed;
        DynamicPageShellContentPresenter.Visibility = Visibility.Visible;
        DynamicPageShellContentPresenter.Content = InnerShellFrame.Content is BasePage page ? page.ShellContent : null;
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
            ViewModel.StatePersistenceService.CoreWindowTitle = ViewModel.CalendarClient.HighlightedDateRange?.ToString() ?? string.Empty;
            return;
        }

        ViewModel.StatePersistenceService.CoreWindowTitle = string.Empty;
    }

    private void ManageCalendarDisplayType(Core.Domain.Enums.CalendarDisplayType displayType)
    {
        DayHeaderNavigationItemsFlipView.DisplayType = displayType;

        if (CalendarTypeSelector.SelectedType != displayType)
        {
            CalendarTypeSelector.SelectedType = displayType;
        }

        VisualStateManager.GoToState(this, displayType == Core.Domain.Enums.CalendarDisplayType.Month
            ? StateVerticalCalendar
            : StateHorizontalCalendar, false);
    }

    private void InitializeCalendarControls()
    {
        CalendarTypeSelector.TodayClickedCommand = ViewModel.CalendarClient.TodayClickedCommand;
        CalendarView.DateClickedCommand = ViewModel.CalendarClient.DateClickedCommand;
        DayHeaderNavigationItemsFlipView.ItemsSource = ViewModel.CalendarClient.DateNavigationHeaderItems;
        CalendarHostListView.ItemsSource = ViewModel.CalendarClient.GroupedAccountCalendars;

        RefreshCalendarControls();
    }

    private void RefreshCalendarControls()
    {
        DayHeaderNavigationItemsFlipView.ItemsSource = ViewModel.CalendarClient.DateNavigationHeaderItems;
        DayHeaderNavigationItemsFlipView.SelectedIndex = ViewModel.CalendarClient.SelectedDateNavigationHeaderIndex;
        CalendarTypeSelector.DisplayDayCount = ViewModel.CalendarClient.StatePersistenceService.DayDisplayCount;
        CalendarView.HighlightedDateRange = ViewModel.CalendarClient.HighlightedDateRange;
        CalendarHostListView.ItemsSource = ViewModel.CalendarClient.GroupedAccountCalendars;
    }

    private void RefreshNavigationViewBindings(bool syncMailSelection = true)
    {
        navigationView.MenuItemsSource = ViewModel.CurrentMenuItems;

        navigationView.SelectionChanged -= MenuSelectionChanged;
        navigationView.SelectedItem = ViewModel.CurrentClient.HandlesNavigationSelection && syncMailSelection
            ? ViewModel.SelectedMenuItem
            : null;
        navigationView.SelectionChanged += MenuSelectionChanged;
    }

    private void UpdateEventDetailsVisualState()
    {
        VisualStateManager.GoToState(this,
            ViewModel.StatePersistenceService.IsEventDetailsVisible ? StateEventDetailsContent : StateDefaultShellContent,
            false);
    }

    private void CalendarTypeSelectorSelectedTypeChanged(DependencyObject sender, DependencyProperty dp)
    {
        var selectedType = CalendarTypeSelector.SelectedType;

        if (ViewModel.CalendarClient.StatePersistenceService.CalendarDisplayType != selectedType)
        {
            ViewModel.CalendarClient.StatePersistenceService.CalendarDisplayType = selectedType;
        }
    }

    private void PreviousDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoPreviousDateRequestedMessage());

    private void NextDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoNextDateRequestedMessage());

    public void Receive(CalendarDisplayTypeChangedMessage message) => ManageCalendarDisplayType(message.NewDisplayType);

    private async void NavigationViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
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
        if (!ViewModel.IsMailMode)
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
                navigationView.SelectedItem = foundMenuItem;

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
                    navigationView.SelectedItem = accountFolderMenuItem;
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

            navigationView.SelectionChanged -= MenuSelectionChanged;
            navigationView.SelectedItem = message.BaseFolderMenuItem;
            navigationView.SelectionChanged += MenuSelectionChanged;
        }
        else
        {
            message.FolderInitLoadAwaitTask?.TrySetResult(true);
        }
    }

    private void ShellFrameContentNavigated(object sender, NavigationEventArgs e)
    {
        ApplyTitleBarContent();

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

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => UpdateNavigationPaneLayout(args.DisplayMode);

    private void MailClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IShellClient.SelectedMenuItem) && ViewModel.IsMailMode)
        {
            navigationView.SelectionChanged -= MenuSelectionChanged;
            navigationView.SelectedItem = ViewModel.MailClient.SelectedMenuItem;
            navigationView.SelectionChanged += MenuSelectionChanged;
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

        if (e.PropertyName == nameof(ICalendarShellClient.DateNavigationHeaderItems))
        {
            DayHeaderNavigationItemsFlipView.ItemsSource = ViewModel.CalendarClient.DateNavigationHeaderItems;
            return;
        }

        if (e.PropertyName == nameof(ICalendarShellClient.SelectedDateNavigationHeaderIndex))
        {
            DayHeaderNavigationItemsFlipView.SelectedIndex = ViewModel.CalendarClient.SelectedDateNavigationHeaderIndex;
            return;
        }

        if (e.PropertyName == nameof(ICalendarShellClient.HighlightedDateRange))
        {
            CalendarView.HighlightedDateRange = ViewModel.CalendarClient.HighlightedDateRange;
            UpdateTitleBarSubtitle();
        }
    }

    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IStatePersistanceService.CalendarDisplayType))
        {
            ManageCalendarDisplayType(ViewModel.CalendarClient.StatePersistenceService.CalendarDisplayType);
            return;
        }

        if (propertyName == nameof(IStatePersistanceService.DayDisplayCount))
        {
            CalendarTypeSelector.DisplayDayCount = ViewModel.CalendarClient.StatePersistenceService.DayDisplayCount;
            return;
        }

        if (propertyName == nameof(IStatePersistanceService.IsEventDetailsVisible))
        {
            UpdateEventDetailsVisualState();
        }
    }

    private void UpdateNavigationPaneLayout(NavigationViewDisplayMode displayMode)
    {
        if (ViewModel.IsCalendarMode)
        {
            PaneCustomContent.Visibility = displayMode == NavigationViewDisplayMode.Expanded && navigationView.IsPaneOpen
                ? Visibility.Visible
                : Visibility.Collapsed;

            InnerShellFrame.Margin = new Thickness(0);
            return;
        }

        PaneCustomContent.Visibility = Visibility.Collapsed;
        InnerShellFrame.Margin = displayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(7, 0, 0, 0)
            : new Thickness(0);
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
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<AccountMenuItemExtended>(this);
        WeakReferenceMessenger.Default.Unregister<CreateNewMailWithMultipleAccountsRequested>(this);
        WeakReferenceMessenger.Default.Unregister<NavigateMailFolderEvent>(this);
        WeakReferenceMessenger.Default.Unregister<CalendarDisplayTypeChangedMessage>(this);
    }
}

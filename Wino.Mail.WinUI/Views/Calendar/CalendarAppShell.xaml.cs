using System;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Views.Abstract;
using Wino.Messaging.Client.Calendar;

namespace Wino.Mail.WinUI.Views.Calendar;

public sealed partial class CalendarAppShell : CalendarAppShellAbstract,
    IRecipient<CalendarDisplayTypeChangedMessage>
{
    private const string STATE_HorizontalCalendar = "HorizontalCalendar";
    private const string STATE_VerticalCalendar = "VerticalCalendar";

    public Frame GetShellFrame() => InnerShellFrame;

    public CalendarAppShell()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;

        ManageCalendarDisplayType(ViewModel.StatePersistenceService.CalendarDisplayType);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNavigationPaneLayout(navigationView.DisplayMode);
    }

    private void ManageCalendarDisplayType(Core.Domain.Enums.CalendarDisplayType displayType)
    {
        if (displayType == Core.Domain.Enums.CalendarDisplayType.Month)
        {
            VisualStateManager.GoToState(this, STATE_VerticalCalendar, false);
        }
        else
        {
            VisualStateManager.GoToState(this, STATE_HorizontalCalendar, false);
        }
    }

    private void PreviousDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoPreviousDateRequestedMessage());

    private void NextDateClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new GoNextDateRequestedMessage());

    private async void NavigationViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is FrameworkElement { DataContext: IMenuItem menuItem })
        {
            await ViewModel.HandleNavigationItemInvokedAsync(menuItem);
        }
    }

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => UpdateNavigationPaneLayout(args.DisplayMode);

    private void UpdateNavigationPaneLayout(NavigationViewDisplayMode displayMode)
    {
        var paneContentVisibility = displayMode == NavigationViewDisplayMode.Expanded && navigationView.IsPaneOpen
            ? Visibility.Visible
            : Visibility.Collapsed;

        PaneCustomContent.Visibility = paneContentVisibility;

        Debug.WriteLine($"NavigationView display mode changed to {displayMode}. Pane custom content visibility set to {paneContentVisibility}.");
    }

    public void Receive(CalendarDisplayTypeChangedMessage message)
    {
        ManageCalendarDisplayType(message.NewDisplayType);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        Bindings.StopTracking();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<CalendarDisplayTypeChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<CalendarDisplayTypeChangedMessage>(this);
    }

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.RepeatCount > 1 || ShouldIgnoreShortcut())
            return;

        var key = NormalizeKey(e.Key);
        if (string.IsNullOrEmpty(key))
            return;

        var shortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
        var shortcut = await shortcutService.GetShortcutForKeyAsync(WinoApplicationMode.Calendar, key, GetCurrentModifierKeys());

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

        await ViewModel.KeyboardShortcutHook(details);

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
}

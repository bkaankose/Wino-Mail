using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.ViewModels;
using Wino.Views.Mail;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Window-local keyboard accelerator owner. It never registers operating-system hot keys.
/// </summary>
internal sealed partial class KeyboardShortcutController : IDisposable
{
    private readonly UIElement _root;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly IKeyboardShortcutService _shortcutService;
    private readonly IWinoLogger _logger;
    private readonly Func<WinoApplicationMode> _modeProvider;
    private readonly Func<BasePage?> _rootPageProvider;
    private readonly Func<BasePage?> _activeSurfaceProvider;
    private readonly Func<KeyboardShortcutTriggerDetails, Task> _modeDispatcher;
    private readonly bool _isPopOut;
    private readonly Dictionary<KeyboardAccelerator, KeyboardShortcutSnapshot> _registrations = [];
    private readonly HashSet<VirtualKey> _pressedKeys = [];
    private int _isExecuting;
    private bool _disposed;

    public KeyboardShortcutController(
        UIElement root,
        IKeyboardShortcutService shortcutService,
        IWinoLogger logger,
        Func<WinoApplicationMode> modeProvider,
        Func<BasePage?> rootPageProvider,
        Func<BasePage?> activeSurfaceProvider,
        Func<KeyboardShortcutTriggerDetails, Task> modeDispatcher,
        bool isPopOut = false)
    {
        _root = root;
        _dispatcherQueue = root.DispatcherQueue;
        _shortcutService = shortcutService;
        _logger = logger;
        _modeProvider = modeProvider;
        _rootPageProvider = rootPageProvider;
        _activeSurfaceProvider = activeSurfaceProvider;
        _modeDispatcher = modeDispatcher;
        _isPopOut = isPopOut;

        _root.KeyUp += Root_KeyUp;
        _shortcutService.KeyboardShortcutsChanged += ShortcutService_KeyboardShortcutsChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
            return;

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Refresh);
            return;
        }

        foreach (var accelerator in _registrations.Keys)
        {
            accelerator.Invoked -= Accelerator_Invoked;
            _root.KeyboardAccelerators.Remove(accelerator);
        }

        _registrations.Clear();
        _pressedKeys.Clear();

        var mode = _modeProvider();
        foreach (var shortcut in _shortcutService.EnabledShortcutsSnapshot.Where(item => item.Mode == mode))
        {
            if (!Enum.TryParse(shortcut.Key, true, out VirtualKey key) || key == VirtualKey.None)
                continue;

            var accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = ToVirtualKeyModifiers(shortcut.ModifierKeys)
            };
            accelerator.Invoked += Accelerator_Invoked;
            _registrations.Add(accelerator, shortcut);
            _root.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void ShortcutService_KeyboardShortcutsChanged(object sender, EventArgs e) => Refresh();

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e) => _pressedKeys.Remove(e.Key);

    private async void Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_disposed || !_registrations.TryGetValue(sender, out var shortcut))
            return;

        var focusedElement = _root.XamlRoot is null ? null : FocusManager.GetFocusedElement(_root.XamlRoot);
        var activeSurface = _activeSurfaceProvider();
        var rootPage = _rootPageProvider();
        if (!CanRoute(shortcut, focusedElement, activeSurface, rootPage))
            return;

        args.Handled = true;

        if (!_pressedKeys.Add(sender.Key) || Interlocked.Exchange(ref _isExecuting, 1) != 0)
            return;

        try
        {
            await DispatchAsync(shortcut, focusedElement, activeSurface, rootPage);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "KeyboardShortcutController.Dispatch");
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
        }
    }

    private async Task DispatchAsync(
        KeyboardShortcutSnapshot shortcut,
        object focusedElement,
        BasePage activeSurface,
        BasePage rootPage)
    {
        if (activeSurface is ComposePage)
        {
            var composeDetails = CreateDetails(
                shortcut,
                focusedElement,
                _isPopOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose);
            await activeSurface.AssociatedViewModel.KeyboardShortcutHook(composeDetails);
            return;
        }

        if (activeSurface is MailRenderingPage)
        {
            var readerDetails = CreateDetails(
                shortcut,
                focusedElement,
                _isPopOut ? KeyboardShortcutInputContext.PopOutReader : KeyboardShortcutInputContext.Reader);
            await activeSurface.AssociatedViewModel.KeyboardShortcutHook(readerDetails);

            if (readerDetails.Handled || _isPopOut)
                return;
        }

        var details = CreateDetails(shortcut, focusedElement, GetRootContext(shortcut.Mode));

        if (rootPage?.AssociatedViewModel is not null && !ReferenceEquals(rootPage, activeSurface))
            await rootPage.AssociatedViewModel.KeyboardShortcutHook(details);
        else if (rootPage?.AssociatedViewModel is not null && activeSurface is not MailRenderingPage)
            await rootPage.AssociatedViewModel.KeyboardShortcutHook(details);

        if (!details.Handled && !_isPopOut)
            await _modeDispatcher(details);
    }

    private KeyboardShortcutTriggerDetails CreateDetails(
        KeyboardShortcutSnapshot shortcut,
        object focusedElement,
        KeyboardShortcutInputContext inputContext)
        => new()
        {
            ShortcutId = shortcut.Id,
            Mode = shortcut.Mode,
            Action = shortcut.Action,
            Key = shortcut.Key,
            ModifierKeys = shortcut.ModifierKeys,
            InputContext = inputContext,
            Sender = _root,
            Origin = focusedElement
        };

    private static bool CanRoute(
        KeyboardShortcutSnapshot shortcut,
        object focusedElement,
        BasePage activeSurface,
        BasePage rootPage)
    {
        if (!IsEligibleRootSurface(shortcut.Mode, rootPage))
            return false;

        var context = activeSurface switch
        {
            ComposePage => KeyboardShortcutInputContext.Compose,
            MailRenderingPage => KeyboardShortcutInputContext.Reader,
            _ => GetRootContext(shortcut.Mode)
        };

        return KeyboardShortcutContextPolicy.CanExecute(
            shortcut.Action,
            shortcut.Key,
            shortcut.ModifierKeys,
            context,
            IsTextInput(focusedElement));
    }

    private static bool IsEligibleRootSurface(WinoApplicationMode mode, BasePage rootPage)
        => mode switch
        {
            WinoApplicationMode.Contacts => rootPage?.AssociatedViewModel is ContactsPageViewModel,
            WinoApplicationMode.Tasks => rootPage?.AssociatedViewModel is ToDoPageViewModel,
            _ => true
        };

    private static bool IsTextInput(object focusedElement)
    {
        if (focusedElement is TextBox or AutoSuggestBox or PasswordBox or RichEditBox or ComboBox)
            return true;

        return focusedElement is FrameworkElement element &&
               element.GetType().Name.Contains("WebView", StringComparison.OrdinalIgnoreCase);
    }

    private static KeyboardShortcutInputContext GetRootContext(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Calendar => KeyboardShortcutInputContext.Calendar,
            WinoApplicationMode.Contacts => KeyboardShortcutInputContext.Contacts,
            WinoApplicationMode.Tasks => KeyboardShortcutInputContext.Tasks,
            _ => KeyboardShortcutInputContext.List
        };

    private static VirtualKeyModifiers ToVirtualKeyModifiers(ModifierKeys modifierKeys)
    {
        var modifiers = VirtualKeyModifiers.None;
        if (modifierKeys.HasFlag(ModifierKeys.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (modifierKeys.HasFlag(ModifierKeys.Alt)) modifiers |= VirtualKeyModifiers.Menu;
        if (modifierKeys.HasFlag(ModifierKeys.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (modifierKeys.HasFlag(ModifierKeys.Windows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shortcutService.KeyboardShortcutsChanged -= ShortcutService_KeyboardShortcutsChanged;
        _root.KeyUp -= Root_KeyUp;

        foreach (var accelerator in _registrations.Keys)
        {
            accelerator.Invoked -= Accelerator_Invoked;
            _root.KeyboardAccelerators.Remove(accelerator);
        }

        _registrations.Clear();
        _pressedKeys.Clear();
    }
}

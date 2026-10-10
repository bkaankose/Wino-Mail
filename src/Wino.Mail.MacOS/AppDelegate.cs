using System.Text.Json;
using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views;
using Wino.Messaging.Client.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS;

[Register("WinoMailAppDelegate")]
public sealed partial class AppDelegate : NSApplicationDelegate, IRecipient<LanguageChanged>
{
    private const string ApplicationName = "Wino Mail";
    private readonly AppKitDispatcher _dispatcher = new();
    private ServiceProvider? _services;
    private NSWindow? _window;
    private Task? _startup;
    private Task? _quit;
    private bool _terminating;
    private bool _shellWindow;
    private bool _runtimeStarted;

    /// <summary>The application services, once composition has finished.</summary>
    internal IServiceProvider? Services => _services;

    /// <summary>Whether the application runtime started; the *ServicesReady hooks have run.</summary>
    internal bool RuntimeStarted => _runtimeStarted;

    // Feature hooks, implemented in AppDelegate.<Feature>.cs partial files.

    /// <summary>First thing in DidFinishLaunching, before any window exists (OS activation, notification delegate).</summary>
    partial void ActivationLaunching();

    /// <summary>On the startup task right after the runtime started; services are available.</summary>
    partial void ActivationServicesReady();

    /// <inheritdoc cref="ActivationServicesReady"/>
    partial void NotificationsServicesReady();

    /// <inheritdoc cref="ActivationServicesReady"/>
    partial void DockServicesReady();

    /// <summary>
    /// At the start of quitting, while services are still alive. Assign <paramref name="stopping"/>
    /// to have quitting wait for asynchronous cleanup.
    /// </summary>
    partial void NotificationsStopping(ref Task? stopping);

    /// <summary>First thing in DidFinishLaunching: how Wino was launched (AppDelegate.Companion.cs).</summary>
    partial void CompanionLaunching();

    /// <inheritdoc cref="ActivationServicesReady"/>
    partial void CompanionServicesReady();

    /// <summary>After the runtime started: menu key equivalents follow the user's keyboard shortcuts.</summary>
    partial void ShortcutMenusServicesReady();

    /// <summary>At the start of quitting: stop following shortcut changes.</summary>
    partial void ShortcutMenusStopping();

    /// <summary>At the start of quitting, while services are still alive.</summary>
    partial void CompanionStopping();

    /// <summary>After the runtime started: App Store transaction handling (App Store builds only, AppDelegate.AppStore.cs).</summary>
    partial void AppStoreServicesReady();

    /// <summary>After the menus exist: start the updater (DMG builds only, AppDelegate.Updates.cs).</summary>
    partial void UpdatesLaunching();

    /// <summary>Adds Check for Updates… under About (DMG builds only).</summary>
    partial void AddUpdateMenuItems(NSMenu application);

    public override void DidFinishLaunching(NSNotification notification)
    {
        ActivationLaunching();
        CompanionLaunching();
        // A login launch starts without a Dock icon or window until the close behaviour is known.
        NSApplication.SharedApplication.ActivationPolicy = StartsInBackground
            ? NSApplicationActivationPolicy.Accessory
            : NSApplicationActivationPolicy.Regular;
        // The menu bar exists before the translation service loads the user's language. Seed the
        // English source so the first menu shows text, then rebuild on every LanguageChanged
        // (the translation service sends one when it finishes initializing).
        SeedEnglishTranslations();
        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        // The View menu has its own Enter Full Screen item; AppKit would add a second one.
        NSUserDefaults.StandardUserDefaults.SetBool(false, "NSFullScreenMenuItemEverywhere");
        InstallMenus();
        UpdatesLaunching();
        // No window until startup knows which one to show (HostController creates it): the shell
        // with accounts, Welcome without. A placeholder window here flashed before the shell.
        if (!StartsInBackground) NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        _startup = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            WinoIcons.Register();
            _services = Composition.Create(_dispatcher, () => _window, HostController, ReportError);
            var configuration = _services.GetRequiredService<IApplicationConfiguration>();
            _services.GetRequiredService<IWinoLogger>().SetupLogger(Path.Combine(configuration.ApplicationDataFolderPath, "Logs", "wino.log"));
            var runtime = _services.GetRequiredService<IApplicationRuntime>();
            await runtime.StartAsync();
            await _services.GetRequiredService<INewThemeService>().InitializeAsync();
            _runtimeStarted = true;
            ActivationServicesReady();
            NotificationsServicesReady();
            DockServicesReady();
            CompanionServicesReady();
            ShortcutMenusServicesReady();
            AppStoreServicesReady();
#if DEBUG
            await _dispatcher.ExecuteOnUIThread(() => MacDebugBridge.Start(_services));
#endif
            var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                var navigation = _services.GetRequiredService<AppKitNavigationService>();
                Observe(accounts.Count > 0 ? navigation.ShowShellAsync() : navigation.NavigateAsync(WinoPage.WelcomePageV2));
            });
        }
        catch (Exception error) { ReportError(error); }
    }

    private Action? HostController(NSViewController controller)
    {
        if (_terminating) throw new InvalidOperationException("The application is terminating.");
        var isShell = controller is WinoAppShellViewController;
        if (_window == null || isShell != _shellWindow)
        {
            var oldWindow = _window;
            NSWindow nextWindow = isShell ? new WinoShellWindow(controller) { ShouldClose = ShellWindowShouldClose } : new WelcomeWindow(controller);
            // In the background (closed to the menu bar, or a login launch) windows wait for the user.
            try { if (!IsInBackground) nextWindow.MakeKeyAndOrderFront(null); }
            catch { nextWindow.Dispose(); throw; }
            _window = nextWindow;
            _shellWindow = isShell;
            oldWindow?.OrderOut(null);
            // The router releases the old controller before disposing its native owner.
            return oldWindow is null ? null : () => { oldWindow.Close(); oldWindow.Dispose(); };
        }
        else
        {
            _window.ContentViewController = controller;
        }
        if (!IsInBackground) _window.MakeKeyAndOrderFront(null);
        return null;
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;

    /// <summary>Launching Wino again (Dock, Finder, Spotlight) also brings it back from the background.</summary>
    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        ShowMainWindow();
        return true;
    }

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_terminating) return NSApplicationTerminateReply.Now;
        if (_services != null && !_services.GetRequiredService<AppKitNavigationService>().CanQuit)
        {
            ReportError(new InvalidOperationException("Finish or cancel account setup before quitting Wino Mail."));
            return NSApplicationTerminateReply.Cancel;
        }
        if (_quit == null)
        {
            // Reply only after AppKit has received the Later result.
            sender.BeginInvokeOnMainThread(() => _quit ??= QuitAsync(sender));
        }
        return NSApplicationTerminateReply.Later;
    }

    private async Task QuitAsync(NSApplication application)
    {
        try
        {
            if (_startup != null) await _startup;
            if (_services != null)
            {
                CompanionStopping();
                ShortcutMenusStopping();
                if (_runtimeStarted)
                {
                    Task? notificationsStopping = null;
                    NotificationsStopping(ref notificationsStopping);
                    if (notificationsStopping != null) await notificationsStopping;
                }
                await _services.GetRequiredService<AppKitNavigationService>().StopAsync();
                if (_runtimeStarted) await _services.GetRequiredService<IApplicationRuntime>().StopAsync();
                await _services.DisposeAsync();
            }
            _terminating = true;
            await _dispatcher.ExecuteOnUIThread(() => application.ReplyToApplicationShouldTerminate(true));
        }
        catch (Exception error)
        {
            ReportError(error);
            _quit = null;
            await _dispatcher.ExecuteOnUIThread(() => application.ReplyToApplicationShouldTerminate(false));
        }
    }

    public void Receive(LanguageChanged message)
    {
        // The translation service publishes from a background thread.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (!_terminating) RetitleMenus();
        });
    }

    /// <summary>
    /// Re-labels the installed main menu from a freshly built copy. Replacing MainMenu instead makes
    /// AppKit insert its own Edit and View items (Dictation, Emoji &amp; Symbols, Full Screen) again.
    /// </summary>
    private void RetitleMenus()
    {
        var current = NSApplication.SharedApplication.MainMenu;
        if (current is null) { InstallMenus(); return; }
        Retitle(current, BuildMenus(install: false));

        static void Retitle(NSMenu target, NSMenu source)
        {
            // Assigning a title, even an unchanged one, makes AppKit add its Edit items again.
            if (target.Title != source.Title) target.Title = source.Title;
            for (nint index = 0; index < source.Count && index < target.Count; index++)
            {
                var from = source.ItemAt(index);
                var to = target.ItemAt(index);
                if (from is null || to is null || from.IsSeparatorItem) continue;
                if (to.Title != from.Title) to.Title = from.Title;
                if (from.Submenu is not null && to.Submenu is not null) Retitle(to.Submenu, from.Submenu);
            }
        }
    }

    private static void SeedEnglishTranslations()
    {
        var resources = Translator.Resources;
        if (resources.Count > 0) return;
        try
        {
            using var stream = WinoTranslationDictionary.GetLanguageStream(AppLanguage.English);
            if (stream == null) return;
            var values = JsonSerializer.Deserialize(stream, BasicTypesJsonContext.Default.DictionaryStringString);
            if (values == null) return;
            foreach (var pair in values) resources.TryAdd(pair.Key, pair.Value);
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Could not seed English menu translations."); }
    }

    private void InstallMenus() => BuildMenus(install: true);

    private NSMenu BuildMenus(bool install)
    {
        var menu = new NSMenu();

        var application = new NSMenu(ApplicationName);
        application.AddItem(new NSMenuItem(string.Format(Translator.MacOSMenu_About, ApplicationName), (_, _) => Navigate(navigation => navigation.Navigate(WinoPage.AboutPage))));
        AddUpdateMenuItems(application);
        application.AddItem(NSMenuItem.SeparatorItem);
        application.AddItem(Item(Translator.MenuSettings + "…", ",", NSEventModifierMask.CommandKeyMask,
            () => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Settings))));
        application.AddItem(NSMenuItem.SeparatorItem);
        application.AddItem(new NSMenuItem(string.Format(Translator.MacOSMenu_Hide, ApplicationName), new ObjCRuntime.Selector("hide:"), "h"));
        application.AddItem(new NSMenuItem(Translator.MacOSMenu_HideOthers, new ObjCRuntime.Selector("hideOtherApplications:"), "h")
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask
        });
        application.AddItem(new NSMenuItem(Translator.MacOSMenu_ShowAll, new ObjCRuntime.Selector("unhideAllApplications:"), string.Empty));
        application.AddItem(NSMenuItem.SeparatorItem);
        application.AddItem(new NSMenuItem(string.Format(Translator.MacOSMenu_Quit, ApplicationName), "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));
        menu.AddItem(new NSMenuItem { Submenu = application });

        var file = new NSMenu(Translator.MacOSMenu_File);
        var newItem = TrackShortcut(Item(Translator.MenuNewMail, "n", NSEventModifierMask.CommandKeyMask, NewItemForActiveMode), KeyboardShortcutAction.NewMail, install);
        file.AddItem(newItem);
        if (install) AttachFileMenu(file, newItem);
        file.AddItem(NSMenuItem.SeparatorItem);
        file.AddItem(new NSMenuItem(Translator.MacOSMenu_CloseWindow, new ObjCRuntime.Selector("performClose:"), "w"));
        menu.AddItem(new NSMenuItem { Submenu = file });

        var edit = new NSMenu(Translator.MacOSMenu_Edit);
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_Undo, new ObjCRuntime.Selector("undo:"), "z"));
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_Redo, new ObjCRuntime.Selector("redo:"), "z")
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask
        });
        edit.AddItem(NSMenuItem.SeparatorItem);
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_Cut, new ObjCRuntime.Selector("cut:"), "x"));
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_Copy, new ObjCRuntime.Selector("copy:"), "c"));
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_Paste, new ObjCRuntime.Selector("paste:"), "v"));
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_PasteAndMatchStyle, new ObjCRuntime.Selector("pasteAsPlainText:"), "v")
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.ShiftKeyMask
        });
        edit.AddItem(new NSMenuItem(Translator.MacOSMenu_SelectAll, new ObjCRuntime.Selector("selectAll:"), "a"));
        edit.AddItem(NSMenuItem.SeparatorItem);
        edit.AddItem(Item(Translator.SearchBarPlaceholder, "f", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask, () => Shell()?.FocusSearch()));
        menu.AddItem(new NSMenuItem { Submenu = edit });

        var view = new NSMenu(Translator.MacOSMenu_View);
        // Mode shortcuts follow the pane switcher order: Mail, Calendar, Contacts, To Do.
        view.AddItem(Item(Translator.KeyboardShortcuts_ModeMail, "1", NSEventModifierMask.CommandKeyMask,
            () => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Mail))));
        view.AddItem(Item(Translator.KeyboardShortcuts_ModeCalendar, "2", NSEventModifierMask.CommandKeyMask,
            () => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Calendar))));
        view.AddItem(Item(Translator.KeyboardShortcuts_ModeContacts, "3", NSEventModifierMask.CommandKeyMask,
            () => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Contacts))));
        view.AddItem(Item(Translator.KeyboardShortcuts_ModeTasks, "4", NSEventModifierMask.CommandKeyMask,
            () => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Tasks))));
        view.AddItem(NSMenuItem.SeparatorItem);
        view.AddItem(Item(Translator.MacOSMenu_ToggleSidebar, "s", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask, () => Shell()?.ToggleSidebar()));
        view.AddItem(new NSMenuItem(Translator.MacOSMenu_EnterFullScreen, new ObjCRuntime.Selector("toggleFullScreen:"), "f")
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask
        });
        menu.AddItem(new NSMenuItem { Submenu = view });

        // Message actions mirror the toolbar. Items stay enabled for their key equivalents and
        // reflect availability while the menu is open; the shell re-checks before executing.
        var message = new NSMenu(Translator.MacOSMenu_Message) { AutoEnablesItems = false };
        var commands = new (ShellCommand Command, string Title, string Key, NSEventModifierMask Mask)[]
        {
            (ShellCommand.Reply, Translator.MailOperation_Reply, "r", NSEventModifierMask.CommandKeyMask),
            (ShellCommand.ReplyAll, Translator.MailOperation_ReplyAll, "r", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask),
            (ShellCommand.Forward, Translator.MailOperation_Forward, "f", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask),
            (ShellCommand.Archive, Translator.MailOperation_Archive, "a", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            (ShellCommand.Delete, Translator.MailOperation_Delete, string.Empty, 0),
            (ShellCommand.Move, Translator.MailOperation_Move + "…", "m", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            (ShellCommand.Flag, Translator.MailOperation_Flag, "l", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask),
            (ShellCommand.ToggleRead, Translator.MailOperation_MarkAsRead, "u", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask)
        };
        var messageItems = new List<(ShellCommand, NSMenuItem)>();
        foreach (var command in commands)
        {
            if (command.Command is ShellCommand.Archive or ShellCommand.Flag) message.AddItem(NSMenuItem.SeparatorItem);
            var item = Item(command.Title, command.Key, command.Mask, () => Shell()?.ExecuteCommand(command.Command));
            if (ShortcutActionFor(command.Command) is { } action) TrackShortcut(item, action, install);
            message.AddItem(item);
            messageItems.Add((command.Command, item));
        }
        if (install)
        {
            _messageMenuDelegate = new MessageMenuDelegate(Shell, messageItems);
            message.Delegate = _messageMenuDelegate;
        }
        menu.AddItem(new NSMenuItem { Submenu = message });

        var window = new NSMenu(Translator.MacOSMenu_Window);
        window.AddItem(new NSMenuItem(Translator.MacOSMenu_Minimize, new ObjCRuntime.Selector("performMiniaturize:"), "m"));
        window.AddItem(new NSMenuItem(Translator.MacOSMenu_Zoom, new ObjCRuntime.Selector("performZoom:"), string.Empty));
        window.AddItem(NSMenuItem.SeparatorItem);
        window.AddItem(new NSMenuItem(Translator.MacOSMenu_BringAllToFront, new ObjCRuntime.Selector("arrangeInFront:"), string.Empty));
        menu.AddItem(new NSMenuItem { Submenu = window });

        if (!install) return menu;
        NSApplication.SharedApplication.MainMenu = menu;
        NSApplication.SharedApplication.WindowsMenu = window;
        return menu;
    }

    private MessageMenuDelegate? _messageMenuDelegate;

    /// <summary>The configurable shortcut action behind a Message menu command (Forward has none).</summary>
    private static KeyboardShortcutAction? ShortcutActionFor(ShellCommand command) => command switch
    {
        ShellCommand.Reply => KeyboardShortcutAction.Reply,
        ShellCommand.ReplyAll => KeyboardShortcutAction.ReplyAll,
        ShellCommand.Archive => KeyboardShortcutAction.ToggleArchive,
        ShellCommand.Delete => KeyboardShortcutAction.Delete,
        ShellCommand.Move => KeyboardShortcutAction.Move,
        ShellCommand.Flag => KeyboardShortcutAction.ToggleFlag,
        ShellCommand.ToggleRead => KeyboardShortcutAction.ToggleReadUnread,
        _ => null
    };

    private static NSMenuItem Item(string title, string key, NSEventModifierMask mask, Action action)
        => new(title, key, (_, _) => action()) { KeyEquivalentModifierMask = mask };

    private WinoAppShellViewController? Shell()
        => _services?.GetRequiredService<AppKitNavigationService>().Shell;

    private void Navigate(Func<AppKitNavigationService, bool> navigate)
    {
        if (_services != null) navigate(_services.GetRequiredService<AppKitNavigationService>());
    }

    /// <summary>Shows Message menu availability while it is open; re-enables afterwards so shortcuts keep working.</summary>
    private sealed class MessageMenuDelegate(Func<WinoAppShellViewController?> shell, List<(ShellCommand Command, NSMenuItem Item)> items) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu)
        {
            var current = shell();
            foreach (var (command, item) in items) item.Enabled = current?.CanExecuteCommand(command) == true;
        }

        public override void MenuDidClose(NSMenu menu)
        {
            foreach (var (_, item) in items) item.Enabled = true;
        }

        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem? item) { }
    }

    private void Observe(Task task) => _ = task.ContinueWith(t => ReportError(t.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

    private void ReportError(Exception error)
    {
        Serilog.Log.Error(error, "Mac application operation failed.");
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            var alert = new NSAlert { MessageText = "Wino Mail", InformativeText = error.Message };
            alert.AddButton(Translator.Buttons_OK);
            if (_window != null) alert.BeginSheet(_window, _ => alert.Dispose());
            else { alert.RunModal(); alert.Dispose(); }
        });
    }
}

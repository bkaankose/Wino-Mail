using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views;

namespace Wino.Mail.MacOS;

[Register("WinoMailAppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private readonly AppKitDispatcher _dispatcher = new();
    private ServiceProvider? _services;
    private NSWindow? _window;
    private Task? _startup;
    private Task? _quit;
    private bool _terminating;
    private bool _shellWindow;
    private bool _runtimeStarted;

    public override void DidFinishLaunching(NSNotification notification)
    {
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        InstallMenus();
        var loading = new NSViewController { View = new NSView() };
        _window = new WelcomeWindow(loading);
        _window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        _startup = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            _services = Composition.Create(_dispatcher, () => _window, HostController, ReportError);
            var configuration = _services.GetRequiredService<IApplicationConfiguration>();
            _services.GetRequiredService<IWinoLogger>().SetupLogger(Path.Combine(configuration.ApplicationDataFolderPath, "Logs", "wino.log"));
            var runtime = _services.GetRequiredService<IApplicationRuntime>();
            await runtime.StartAsync();
            _runtimeStarted = true;
            var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                var navigation = _services.GetRequiredService<AppKitNavigationService>();
                Observe(accounts.Count > 0 ? navigation.ShowShellAsync() : navigation.NavigateAsync(WinoPage.WelcomePageV2));
            });
        }
        catch (Exception error) { ReportError(error); }
    }

    private void HostController(NSViewController controller)
    {
        if (_terminating) { controller.Dispose(); return; }
        var isShell = controller is WinoAppShellViewController;
        if (_window == null || isShell != _shellWindow)
        {
            var oldWindow = _window;
            if (oldWindow != null) oldWindow.ContentViewController = null;
            _window = isShell ? new WinoShellWindow(controller) : new WelcomeWindow(controller);
            _shellWindow = isShell;
            oldWindow?.Close();
            oldWindow?.Dispose();
        }
        else
        {
            _window.ContentViewController = controller;
        }
        _window.MakeKeyAndOrderFront(null);
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        _window?.MakeKeyAndOrderFront(null);
        sender.ActivateIgnoringOtherApps(true);
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

    private void InstallMenus()
    {
        var menu = new NSMenu();
        var application = new NSMenu("Wino Mail");
        application.AddItem(new NSMenuItem("About Wino Mail", (_, _) =>
        {
            if (_services != null) Observe(_services.GetRequiredService<AppKitNavigationService>().NavigateAsync(WinoPage.AboutPage));
        }));
        application.AddItem(NSMenuItem.SeparatorItem);
        application.AddItem(new NSMenuItem("Quit Wino Mail", "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));
        menu.AddItem(new NSMenuItem { Submenu = application });
        var edit = new NSMenu("Edit");
        edit.AddItem(new NSMenuItem("Cut", new ObjCRuntime.Selector("cut:"), "x"));
        edit.AddItem(new NSMenuItem("Copy", new ObjCRuntime.Selector("copy:"), "c"));
        edit.AddItem(new NSMenuItem("Paste", new ObjCRuntime.Selector("paste:"), "v"));
        edit.AddItem(new NSMenuItem("Select All", new ObjCRuntime.Selector("selectAll:"), "a"));
        menu.AddItem(new NSMenuItem { Submenu = edit });
        NSApplication.SharedApplication.MainMenu = menu;
    }

    private void Observe(Task task) => _ = task.ContinueWith(t => ReportError(t.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

    private void ReportError(Exception error)
    {
        Serilog.Log.Error(error, "Mac application operation failed.");
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            var alert = new NSAlert { MessageText = "Wino Mail", InformativeText = error.Message };
            alert.AddButton("OK");
            if (_window != null) alert.BeginSheet(_window, _ => alert.Dispose());
            else { alert.RunModal(); alert.Dispose(); }
        });
    }
}

#if DEBUG
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Views;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the info bar. <c>debug-infobar</c> <c>as</c>: <c>info</c>, <c>success</c>, <c>warning</c>,
/// <c>error</c>, <c>action</c>, <c>queue</c>, <c>messenger</c>, <c>thread</c>, <c>hover on|off</c>, <c>dismiss</c>,
/// <c>suspend on|off</c>, <c>gallery</c>. <c>debug-infobar-state</c> <c>s</c>: writes the main window host's state.
/// </summary>
internal static class InfoBarDebugActions
{
    private const string Text = "Tom & Jerry <b>not bold</b>";
    private const string GalleryTitle = "Wino info bars";

    private static Adw.Window? _gallery;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window)
    {
        var infobar = Gio.SimpleAction.New("debug-infobar", GLib.VariantType.New("as"));
        infobar.OnActivate += (_, args) =>
        {
            var parameters = args.Parameter?.GetStrv(out var _) ?? [];
            try
            {
                Run(application, services, window, parameters);
                Serilog.Log.Information("Debug action debug-infobar {Parameters} done.", string.Join(' ', parameters));
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-infobar failed."); }
        };
        application.AddAction(infobar);

        var state = Gio.SimpleAction.New("debug-infobar-state", GLib.VariantType.New("s"));
        state.OnActivate += (_, args) =>
        {
            var path = args.Parameter?.GetString(out var _) ?? string.Empty;
            try
            {
                var text = window()?.InfoBars.Describe() ?? "no window";
                var temporary = path + ".partial";
                File.WriteAllText(temporary, text + Environment.NewLine);
                File.Move(temporary, path, overwrite: true);
                Serilog.Log.Information("Debug action: info bar state {State}", text);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-infobar-state failed."); }
        };
        application.AddAction(state);
    }

    private static void Run(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window, string[] parameters)
    {
        var command = parameters.Length > 0 ? parameters[0] : string.Empty;
        var on = parameters.Length > 1 && parameters[1] == "on";
        if (command == "gallery")
        {
            ShowGallery(application);
            return;
        }
        if (command is "hover" or "dismiss" or "suspend")
        {
            if (window()?.InfoBars is not { } host) throw new InvalidOperationException("The window does not exist.");
            if (command == "hover") host.Bar.SetHovered(on);
            else if (command == "dismiss") host.Dismiss();
            else host.IsSuspended = on;
            return;
        }

        if (services() is not { } provider) throw new InvalidOperationException("The app has not started.");
        var dialogs = provider.GetRequiredService<IMailDialogService>();
        switch (command)
        {
            case "info": dialogs.InfoBarMessage("Information", Text, InfoBarMessageType.Information); break;
            case "success": dialogs.InfoBarMessage("Success", Text, InfoBarMessageType.Success); break;
            case "warning": dialogs.InfoBarMessage("Warning", Text, InfoBarMessageType.Warning); break;
            case "error": dialogs.InfoBarMessage("Error", Text, InfoBarMessageType.Error); break;
            case "action":
                dialogs.InfoBarMessage("Action", "The button runs an action.", InfoBarMessageType.Warning, "Retry",
                    static () => Serilog.Log.Information("Debug action: info bar action ran"));
                break;
            case "queue":
                foreach (var name in (string[])["One", "Two", "Three"]) dialogs.InfoBarMessage(name, "Queued message " + name, InfoBarMessageType.Information);
                break;
            case "messenger":
                provider.GetRequiredService<IMessenger>().Send(new InfoBarMessageRequested(InfoBarMessageType.Success, "Messenger", "Sent through InfoBarMessageRequested"));
                break;
            case "thread":
                _ = Task.Run(() => dialogs.InfoBarMessage("Thread", "Raised from a worker thread", InfoBarMessageType.Information));
                break;
            default: throw new ArgumentException($"'{command}' is not an info bar command.");
        }
    }

    private static void ShowGallery(Adw.Application application)
    {
        if (_gallery is not null)
        {
            _gallery.Present();
            return;
        }

        var bars = new List<WinoInfoBar>
        {
            new(WinoInfoBarSeverity.Informational, "Closable", "Close this bar with its button.") { IsClosable = true },
            new(WinoInfoBarSeverity.Success, "With an action", "The action button sits at the end.") { ActionTitle = "Retry" },
            new(WinoInfoBarSeverity.Warning, "With content", "A widget under the text.") { Content = Gtk.Label.New("Extra content label") },
            new(WinoInfoBarSeverity.Error, "Long message", string.Join(' ', Enumerable.Repeat("This message is long enough to wrap over several lines in the gallery window.", 4))),
        };

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        content.SetMarginTop(16);
        content.SetMarginBottom(16);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        foreach (var bar in bars)
        {
            bar.Closed += static (_, reason) => Serilog.Log.Information("Debug action: gallery info bar closed ({Reason})", reason);
            content.Append(bar.Root);
        }

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(content);
        var window = Adw.Window.New();
        window.SetApplication(application);
        window.SetTitle(GalleryTitle);
        window.SetDefaultSize(640, 520);
        window.SetContent(toolbar);
        window.OnCloseRequest += (_, _) =>
        {
            foreach (var bar in bars) bar.Dispose();
            _gallery = null;
            return false;
        };
        _gallery = window;
        window.Present();
    }
}
#endif

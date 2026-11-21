using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views;

/// <summary>The one window: a toast overlay over the info bar host and the theme backdrop, which holds either a status page (startup, errors) or the shell.</summary>
internal sealed class MainWindow
{
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();
    private readonly ThemeBackdrop _backdrop = new();
    private readonly InfoBarHost _infoBars = new();

    public MainWindow(Adw.Application application, WindowPlacement placement)
    {
        WinoStyle.Install(message => Log.Warning("Wino stylesheet: {Message}", message));
        Window = Adw.ApplicationWindow.New(application);
        Window.Title = "Wino Mail";
        Window.SetDefaultSize(placement.Width, placement.Height);
        Window.SetSizeRequest(ShellMetrics.WindowMinimumWidth, ShellMetrics.WindowMinimumHeight);
        if (placement.IsMaximized) Window.Maximize();
        Window.SetContent(_toasts);
        _infoBars.Child = _backdrop.Root;
        _toasts.SetChild(_infoBars.Root);
        ShowStatus("mail-send-receive-symbolic", "Wino Mail", null, busy: true);
    }

    public Adw.ApplicationWindow Window { get; }

    public void ShowContent(Gtk.Widget content) => _backdrop.Child = content;

    public void ShowStatus(string iconName, string title, string? description, bool busy = false)
    {
        var page = Adw.StatusPage.New();
        page.SetIconName(iconName);
        page.SetTitle(title);
        if (!string.IsNullOrEmpty(description)) page.SetDescription(description);
        if (busy)
        {
            var spinner = Adw.Spinner.New();
            spinner.SetSizeRequest(32, 32);
            page.SetChild(spinner);
        }

        // A header bar keeps the window movable and closable while no shell is shown.
        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(page);
        _backdrop.Child = view;
    }

    internal InfoBarHost InfoBars => _infoBars;

    /// <summary>GTK keeps the default size at the last unmaximised size, which is the size to restore.</summary>
    public WindowPlacement CapturePlacement()
    {
        Window.GetDefaultSize(out var width, out var height);
        return new WindowPlacement(width, height, Window.IsMaximized());
    }

    public void ShowMessage(string title, string message, InfoBarMessageType type, string? actionText, Action? action)
        => _infoBars.Show(title, message, type, actionText, action);
}

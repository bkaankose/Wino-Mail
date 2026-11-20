using Serilog;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views;

/// <summary>The one window: a toast overlay over either a status page (startup, errors) or the shell.</summary>
internal sealed class MainWindow
{
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();

    public MainWindow(Adw.Application application)
    {
        WinoStyle.Install(message => Log.Warning("Wino stylesheet: {Message}", message));
        Window = Adw.ApplicationWindow.New(application);
        Window.Title = "Wino Mail";
        Window.SetDefaultSize(1360, 860);
        Window.SetContent(_toasts);
        ShowStatus("mail-send-receive-symbolic", "Wino Mail", null, busy: true);
    }

    public Adw.ApplicationWindow Window { get; }

    public void ShowContent(Gtk.Widget content) => _toasts.SetChild(content);

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
        _toasts.SetChild(view);
    }

    public void ShowToast(string? title, string? message, string? actionText, Action? action)
    {
        var text = string.Join(": ", new[] { title, message }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (text.Length == 0) return;
        var toast = Adw.Toast.New(GLib.Functions.MarkupEscapeText(text, -1));
        if (!string.IsNullOrEmpty(actionText) && action is not null)
        {
            toast.SetButtonLabel(actionText);
            toast.OnButtonClicked += (_, _) => action();
        }
        _toasts.AddToast(toast);
    }
}

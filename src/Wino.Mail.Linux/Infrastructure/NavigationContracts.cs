using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.Linux.Infrastructure;

internal enum LinuxPageHost
{
    Window,
    ShellContent,
    RenderingFrame,
    Settings,
    // The owner of the window creates such a page itself; the coordinator never routes here.
    SecondaryWindow
}

/// <summary>A shell content page shown beside the shell's reading pane. Other pages get the whole content area.</summary>
internal interface IUsesReadingPane;

internal interface IRenderingFrameHost
{
    /// <summary>The host decides whether it reuses the view it already has for this page; it calls <paramref name="create"/> at most once.</summary>
    Task ShowAsync(WinoPage page, object? parameter, Func<IWinoView> create);
    Task ClearAsync();

    /// <summary>The ViewModel of the page the pane shows now; null while it is empty.</summary>
    object? CurrentViewModel { get; }

    /// <summary>Covers the pane with a list page's own widget (the multi-selection summary) while the shown page stays alive underneath; null removes it.</summary>
    void SetCover(Gtk.Widget? cover);

    /// <summary>Hands a kept view to the caller without releasing it; the pane no longer knows it. False when the pane does not hold it.</summary>
    bool Detach(IWinoView view);
}

/// <summary>A rendering frame that takes back a live view it handed out with <see cref="IRenderingFrameHost.Detach"/> (a docked composer).</summary>
internal interface IRenderingFrameAdopter
{
    /// <summary>Shows <paramref name="view"/> (already active) as the pane's page; the page shown before is released. False when the pane is gone.</summary>
    bool Adopt(WinoPage page, IWinoView view);
}

/// <summary>The Settings window (ST-02). Optional: <see cref="GtkNavigationService"/> refuses Settings while none is registered.</summary>
internal interface ISettingsWindowPresenter
{
    Task ShowAsync(WinoPage? page, object? parameter);
    bool TryGoBack();
    Task CloseAsync();
    Task StopAsync();
}

/// <summary>
/// The secondary onboarding window (SH-07). Optional, like <see cref="ISettingsWindowPresenter"/>. A presenter whose
/// window the user closes calls <see cref="GtkNavigationService.OnSecondaryWindowClosedByUser"/>.
/// </summary>
internal interface ISecondaryWindowPresenter
{
    bool IsShown { get; }
    Task ShowAsync(WinoPage page, IWinoView view, object? parameter, NavigationMode mode, Action committed);
    Task CloseAsync(bool closedByUser);
}

/// <summary>A window that owns dialogs and shows info bar messages. The main window is the default surface.</summary>
internal interface IDialogSurface
{
    Gtk.Widget Owner { get; }
    void ShowMessage(string title, string message, InfoBarMessageType type, string? actionText, Action? action);
}

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

internal interface IRenderingFrameHost
{
    /// <summary>The host decides whether it reuses the view it already has for this page; it calls <paramref name="create"/> at most once.</summary>
    Task ShowAsync(WinoPage page, object? parameter, Func<IWinoView> create);
    Task ClearAsync();
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

#nullable enable

using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Interfaces;

public interface INavigationService
{
    /// <summary>
    /// Navigates to a page. Leave <paramref name="frame"/> null to use the frame the page's
    /// route declares; pass one only to override that default.
    /// </summary>
    bool Navigate(MailCalPage page,
                  object? parameter = null,
                  NavigationReferenceFrame? frame = null,
                  NavigationTransitionType transition = NavigationTransitionType.None);

    Type? GetPageType(MailCalPage mailCalPage);
    bool ChangeApplicationMode(MailCalApplicationMode mode);
    bool ChangeApplicationMode(MailCalApplicationMode mode, ShellModeActivationContext activationContext);
    bool ParkShell();
    bool RestoreShell(MailCalApplicationMode mode);
    bool RestoreShell(MailCalApplicationMode mode, ShellModeActivationContext activationContext);
    bool CanGoBack();

    /// <summary>
    /// Fire and forget back navigation. Prefer <see cref="GoBackAsync"/> when the caller
    /// needs to know whether the page allowed it.
    /// </summary>
    void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight);

    /// <summary>
    /// Returns false when the active page refused to leave, for example because it holds
    /// unsaved changes and the user cancelled the prompt.
    /// </summary>
    Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight);

    /// <summary>
    /// Publishes the outcome of the page that is about to be left. Delivered to the page
    /// returned to when it implements <see cref="IBackNavigationAware"/>.
    /// </summary>
    void SetNavigationResult(NavigationResult result);
}

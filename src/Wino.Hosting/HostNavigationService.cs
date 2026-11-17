using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Hosting;

/// <summary>Navigation for a host with no pages: every request is recorded and refused, so no caller waits for a page.</summary>
internal sealed class HostNavigationService(HostInteractionLog log) : INavigationService
{
    private bool Refuse(string name, string? detail)
    {
        log.Record(HostInteractionKind.Navigation, name, detail, "no page");
        return false;
    }

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null,
        NavigationTransitionType transition = NavigationTransitionType.None)
        => Refuse(nameof(Navigate), page.ToString());

    public Type? GetPageType(WinoPage winoPage) => null;

    public bool ChangeApplicationMode(WinoApplicationMode mode) => Refuse(nameof(ChangeApplicationMode), mode.ToString());

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => Refuse(nameof(ChangeApplicationMode), mode.ToString());

    public bool ParkShell() => Refuse(nameof(ParkShell), null);

    public bool RestoreShell(WinoApplicationMode mode) => Refuse(nameof(RestoreShell), mode.ToString());

    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => Refuse(nameof(RestoreShell), mode.ToString());

    public bool CanGoBack() => false;

    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => Refuse(nameof(GoBack), null);

    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
        => Task.FromResult(Refuse(nameof(GoBackAsync), null));

    public void SetNavigationResult(NavigationResult result) => log.Record(HostInteractionKind.Navigation, nameof(SetNavigationResult), result?.ToString());
}

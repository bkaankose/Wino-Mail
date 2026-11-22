using System.Diagnostics.CodeAnalysis;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed record LinuxPageRoute(Type ViewType, LinuxPageHost Host, Func<IServiceProvider, IWinoView> Create)
{
    /// <summary>
    /// A rendering-frame page that only a shell content page can host (the event composer covers the calendar page).
    /// When the frame belongs to another view, the container page is navigated first with the parameter it maps.
    /// </summary>
    public LinuxPageContainer? Container { get; init; }
}

internal sealed record LinuxPageContainer(WinoPage Page, Func<object?, object?> Parameter);

/// <summary>
/// Maps shared page identifiers to the view that shows them and the host it is shown in. Each area adds its routes in
/// its own partial file: hook <c>RegisterSettingsGeneralPages</c> lives in <c>LinuxPageRegistry.Settings.General.cs</c>.
/// A hook without an implementing file compiles to nothing. New hooks are added at the end of both lists, one per line.
/// </summary>
internal sealed partial class LinuxPageRegistry
{
    private readonly Dictionary<WinoPage, LinuxPageRoute> _pages = [];
    private readonly Dictionary<WinoPage, Func<object?, LinuxPageHost?>> _hostResolvers = [];

    public LinuxPageRegistry()
    {
        RegisterMailPages();
        RegisterComposePages();
        RegisterOnboardingPages();
        RegisterCalendarPages();
        RegisterContactsPages();
        RegisterTasksPages();
        RegisterShellExtrasPages();
        RegisterSettingsPages();
        RegisterSettingsGeneralPages();
        RegisterSettingsMailPages();
        RegisterSettingsNotificationsPages();
        RegisterSettingsModesPages();
        RegisterSettingsThemesPages();
        RegisterSettingsShortcutsPages();
        RegisterSettingsBackupPages();
        RegisterSettingsTemplatesPages();
        RegisterSettingsSignaturePages();
        RegisterSettingsFiltersPages();
        RegisterSettingsCategoryPages();
        RegisterSettingsAccountsPages();
        RegisterSettingsWinoAccountPages();
        RegisterSettingsIntelligencePages();
        RegisterSettingsAppLockPages();
        RegisterSettingsSmimePages();
    }

    // The static generic lambda keeps the view's constructor for the trimmer; a stored Type alone would not.
    public LinuxPageRegistry Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView>(WinoPage page, LinuxPageHost host)
        where TView : class, IWinoView
    {
        _pages[page] = new LinuxPageRoute(typeof(TView), host, static services => PageScopes.Create<TView>(services));
        return this;
    }

    public LinuxPageRegistry RequireContainer(WinoPage page, WinoPage container, Func<object?, object?> parameter)
    {
        if (_pages.TryGetValue(page, out var route)) _pages[page] = route with { Container = new LinuxPageContainer(container, parameter) };
        return this;
    }

    /// <summary>Lets one page pick its host from the navigation parameter. A null result keeps the registered host.</summary>
    public LinuxPageRegistry RegisterHostResolver(WinoPage page, Func<object?, LinuxPageHost?> resolve)
    {
        _hostResolvers[page] = resolve;
        return this;
    }

    public bool TryGet(WinoPage page, object? parameter, [NotNullWhen(true)] out LinuxPageRoute? route)
    {
        if (!TryGet(page, out route)) return false;
        if (_hostResolvers.TryGetValue(page, out var resolve) && resolve(parameter) is { } host) route = route with { Host = host };
        return true;
    }

    public bool TryGet(WinoPage page, [NotNullWhen(true)] out LinuxPageRoute? route) => _pages.TryGetValue(page, out route);

    partial void RegisterMailPages();
    partial void RegisterComposePages();
    partial void RegisterOnboardingPages();
    partial void RegisterCalendarPages();
    partial void RegisterContactsPages();
    partial void RegisterTasksPages();
    partial void RegisterShellExtrasPages();
    partial void RegisterSettingsPages();
    partial void RegisterSettingsGeneralPages();
    partial void RegisterSettingsMailPages();
    partial void RegisterSettingsNotificationsPages();
    partial void RegisterSettingsModesPages();
    partial void RegisterSettingsThemesPages();
    partial void RegisterSettingsShortcutsPages();
    partial void RegisterSettingsBackupPages();
    partial void RegisterSettingsTemplatesPages();
    partial void RegisterSettingsSignaturePages();
    partial void RegisterSettingsFiltersPages();
    partial void RegisterSettingsCategoryPages();
    partial void RegisterSettingsAccountsPages();
    partial void RegisterSettingsWinoAccountPages();
    partial void RegisterSettingsIntelligencePages();
    partial void RegisterSettingsAppLockPages();
    partial void RegisterSettingsSmimePages();
}

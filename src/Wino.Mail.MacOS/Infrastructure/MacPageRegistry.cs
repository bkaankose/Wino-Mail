using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views;
using Wino.Mail.MacOS.Views.Onboarding;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Maps Windows page identifiers to native controllers and the host that shows them.
/// Each feature adds its routes in its own partial file (MacPageRegistry.Mail.cs,
/// MacPageRegistry.Settings.cs) so features can be developed independently.
/// </summary>
public sealed partial class MacPageRegistry
{
    private readonly Dictionary<WinoPage, (Type Controller, MacPageHost Host)> _pages = new();
    private readonly Dictionary<WinoPage, Func<object?, MacPageHost?>> _hostResolvers = new();

    public MacPageRegistry()
    {
        RegisterCorePages();
        RegisterMailPages();
        RegisterSettingsPages();
        RegisterCalendarPages();
        RegisterTasksPages();
        RegisterContactsPages();
        RegisterShellExtrasPages();
    }

    public MacPageRegistry Register<TController>(WinoPage page, MacPageHost host) where TController : NSViewController
    {
        _pages[page] = (typeof(TController), host);
        return this;
    }

    /// <summary>
    /// Lets one page pick its host from the navigation parameter (for example the server settings page:
    /// inside Settings when editing an account, in the onboarding window when adding one).
    /// A null result keeps the registered host.
    /// </summary>
    public MacPageRegistry RegisterHostResolver(WinoPage page, Func<object?, MacPageHost?> resolve)
    {
        _hostResolvers[page] = resolve;
        return this;
    }

    public bool TryGet(WinoPage page, object? parameter, out Type controller, out MacPageHost host)
    {
        if (!TryGet(page, out controller, out host)) return false;
        if (_hostResolvers.TryGetValue(page, out var resolve) && resolve(parameter) is { } resolved) host = resolved;
        return true;
    }

    public bool TryGet(WinoPage page, out Type controller, out MacPageHost host)
    {
        if (_pages.TryGetValue(page, out var route))
        {
            controller = route.Controller;
            host = route.Host;
            return true;
        }
        controller = null!;
        host = default;
        return false;
    }

    private void RegisterCorePages()
    {
        Register<WelcomePageV2ViewController>(WinoPage.WelcomePageV2, MacPageHost.Window);
        Register<WelcomePageV2ViewController>(WinoPage.WelcomeHostPage, MacPageHost.Window);
        Register<ProviderSelectionPageViewController>(WinoPage.ProviderSelectionPage, MacPageHost.Window);
        Register<AccountSetupProgressPageViewController>(WinoPage.AccountSetupProgressPage, MacPageHost.Window);
        Register<SpecialImapCredentialsPageViewController>(WinoPage.SpecialImapCredentialsPage, MacPageHost.Window);
        // Account details edits server settings inside Settings; adding an account stays in the onboarding window.
        Register<ImapCalDavSettingsPageViewController>(WinoPage.ImapCalDavSettingsPage, MacPageHost.SettingsWindow);
        RegisterHostResolver(WinoPage.ImapCalDavSettingsPage, parameter =>
            parameter is ImapCalDavSettingsNavigationContext { Mode: not ImapCalDavSettingsPageMode.Edit } ? MacPageHost.Window : null);
    }

    partial void RegisterMailPages();
    partial void RegisterSettingsPages();
    partial void RegisterCalendarPages();
    partial void RegisterTasksPages();
    partial void RegisterContactsPages();
    partial void RegisterShellExtrasPages();
}

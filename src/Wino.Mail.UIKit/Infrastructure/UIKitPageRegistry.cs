using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Views;
using Wino.Mail.UIKit.Views.Onboarding;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Where a UIKit page is shown. The phone and pad registries choose per idiom: the same reader is a
/// <see cref="Push"/> on iPhone and a <see cref="RenderingFrame"/> on iPad.
/// </summary>
public enum UIKitPageHost
{
    /// <summary>The window's root (onboarding without a shell). While the shell is up it opens in the add-account sheet.</summary>
    Root,

    /// <summary>The shell's content: the current tab's root page (iPhone) or the list column (iPad).</summary>
    ShellContent,

    /// <summary>The reader frame: the reader column (iPad). On iPhone the shell host pushes it.</summary>
    RenderingFrame,

    /// <summary>Pushed on the current navigation stack (the open sheet, else the selected tab or the content column).</summary>
    Push,

    /// <summary>A page sheet (iPad form sheet); a further sheet page pushes inside it.</summary>
    Sheet,

    /// <summary>A full-screen sheet (Settings on iPhone); a further sheet page pushes inside it.</summary>
    FullScreenSheet
}

/// <summary>Maps Windows page identifiers to UIKit controllers and the host that shows them, per idiom.</summary>
public interface IUIKitPageRegistry
{
    /// <summary>The route of <paramref name="page"/>; a host resolver may pick another host from the parameter.</summary>
    bool TryGet(WinoPage page, object? parameter, out Type controller, out UIKitPageHost host);

    /// <summary>The registered route without parameter-specific host resolution.</summary>
    bool TryGet(WinoPage page, out Type controller, out UIKitPageHost host);
}

/// <summary>
/// The registry the idiom registries derive from (the Mac MacPageRegistry). It registers the onboarding pages,
/// which are the same on both idioms; PhonePageRegistry and PadPageRegistry add their routes in their constructors.
/// </summary>
public abstract class UIKitPageRegistry : IUIKitPageRegistry
{
    private readonly Dictionary<WinoPage, (Type Controller, UIKitPageHost Host)> _pages = new();
    private readonly Dictionary<WinoPage, Func<object?, UIKitPageHost?>> _hostResolvers = new();
    private readonly Dictionary<WinoPage, UIKitPageHost> _hostOverrides = new();

    protected UIKitPageRegistry()
    {
        RegisterOnboardingPages();
    }

    public UIKitPageRegistry Register<TController>(WinoPage page, UIKitPageHost host) where TController : UIViewController
    {
        _pages[page] = (typeof(TController), host);
        return this;
    }

    /// <summary>Lets one page pick its host from the navigation parameter. A null result keeps the registered host.</summary>
    public UIKitPageRegistry RegisterHostResolver(WinoPage page, Func<object?, UIKitPageHost?> resolve)
    {
        _hostResolvers[page] = resolve;
        return this;
    }

    /// <summary>
    /// Sets this idiom's host for a page whose controller another registration supplies (a shared page registered once
    /// for both idioms, such as the reader or the composer). It wins over that registration's host, whichever is
    /// registered first; a host resolver still runs after it. It adds no route on its own.
    /// </summary>
    public UIKitPageRegistry RegisterHost(WinoPage page, UIKitPageHost host)
    {
        _hostOverrides[page] = host;
        return this;
    }

    public bool TryGet(WinoPage page, object? parameter, out Type controller, out UIKitPageHost host)
    {
        if (!TryGet(page, out controller, out host)) return false;
        if (_hostResolvers.TryGetValue(page, out var resolve) && resolve(parameter) is { } resolved) host = resolved;
        return true;
    }

    public bool TryGet(WinoPage page, out Type controller, out UIKitPageHost host)
    {
        if (_pages.TryGetValue(page, out var route))
        {
            controller = route.Controller;
            host = _hostOverrides.TryGetValue(page, out var idiomHost) ? idiomHost : route.Host;
            return true;
        }
        controller = typeof(UIViewController);
        host = default;
        return false;
    }

    /// <summary>Maps a UIKit host to the shared coordinator's host kind.</summary>
    public static NavigationHostKind ToHostKind(UIKitPageHost host) => host switch
    {
        UIKitPageHost.ShellContent => NavigationHostKind.ShellContent,
        // Pushes are presentation-only (the stack's back button owns them), like the reading pane.
        UIKitPageHost.RenderingFrame or UIKitPageHost.Push => NavigationHostKind.RenderingFrame,
        // Sheets use the coordinator's Settings surface: one presented stack with its own back.
        UIKitPageHost.Sheet or UIKitPageHost.FullScreenSheet => NavigationHostKind.Settings,
        _ => NavigationHostKind.Window
    };

    /// <summary>Onboarding is the same on both idioms (Mac MacPageRegistry.RegisterCorePages).</summary>
    private void RegisterOnboardingPages()
    {
        Register<WelcomePageV2ViewController>(WinoPage.WelcomePageV2, UIKitPageHost.Root);
        Register<WelcomePageV2ViewController>(WinoPage.WelcomeHostPage, UIKitPageHost.Root);
        Register<ProviderSelectionPageViewController>(WinoPage.ProviderSelectionPage, UIKitPageHost.Root);
        Register<AccountSetupProgressPageViewController>(WinoPage.AccountSetupProgressPage, UIKitPageHost.Root);
        Register<SpecialImapCredentialsPageViewController>(WinoPage.SpecialImapCredentialsPage, UIKitPageHost.Root);
        // Editing an account's servers belongs to Settings (a sheet); adding an account stays in onboarding.
        Register<ImapCalDavSettingsPageViewController>(WinoPage.ImapCalDavSettingsPage, UIKitPageHost.Sheet);
        RegisterHostResolver(WinoPage.ImapCalDavSettingsPage, parameter =>
            parameter is ImapCalDavSettingsNavigationContext { Mode: not ImapCalDavSettingsPageMode.Edit } ? UIKitPageHost.Root : null);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Views.Calendar;
using Wino.Mail.Android.Views.Mail;
using Wino.Mail.Android.Views.Mail.Compose;
using Wino.Mail.Android.Views.Onboarding;
using Wino.Mail.Android.Views.Contacts;
using Wino.Mail.Android.Views.Settings;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>Where an Android page is shown (the UIKitPageHost twin).</summary>
public enum AndroidPageHost
{
    /// <summary>The activity's main content (onboarding without a shell).</summary>
    Root,

    /// <summary>The shell's content area (the list pane of the current mode).</summary>
    ShellContent,

    /// <summary>
    /// The reader pane on medium and expanded widths; full screen on compact. One at a time per mode: a new one replaces
    /// the previous one.
    /// </summary>
    RenderingFrame,

    /// <summary>Pushed on the current mode's back stack, full screen over the shell on every width.</summary>
    Push,

    /// <summary>The Settings surface: its own stack over the shell.</summary>
    Settings
}

/// <summary>A registered page: how to create its fragment and ViewModel, and where it goes.</summary>
public sealed record AndroidPageRoute(Func<WinoFragment> CreateFragment, Func<IServiceProvider, CoreBaseViewModel>? CreateViewModel, AndroidPageHost Host, Type FragmentType)
{
    /// <summary>Runs on the UI thread before the ViewModel's OnNavigatedTo (see <see cref="PageEntry.Preparing"/>).</summary>
    public Action<PageEntry>? Preparing { get; init; }

    /// <summary>Runs after OnNavigatedTo as part of the activation (see <see cref="PageEntry.Activated"/>).</summary>
    public Func<PageEntry, Task>? Activated { get; init; }

    /// <summary>Replaces OnNavigatedTo (see <see cref="PageEntry.Initialize"/>).</summary>
    public Func<PageEntry, NavigationMode, Task>? Initialize { get; init; }

    /// <summary>Replaces OnNavigatedFrom (see <see cref="PageEntry.Deactivate"/>).</summary>
    public Func<PageEntry, Task>? Deactivate { get; init; }

    /// <summary>
    /// A shell-content page that takes a new parameter in place (see <see cref="PageEntry.Renavigate"/>): navigating to
    /// the page that is already the shell's content keeps its entry and view instead of replacing them.
    /// </summary>
    public Func<PageEntry, Task>? Renavigate { get; init; }
}

/// <summary>
/// Maps Windows page identifiers (<see cref="WinoPage"/>) to fragments and their hosts, like UIKitPageRegistry and the
/// Mac MacPageRegistry. Mail (T7): the folder list is the Mail mode's shell content, the reader a rendering-frame page.
/// Compose (T8) is a rendering-frame page like the reader. Pages that later items build are placeholders here so the
/// shell can reach them: the Contacts and To Do roots (T10) are mode placeholders. Calendar pages are registered by
/// <see cref="CalendarPageRoutes"/>. Settings pages are
/// registered by <see cref="SettingsPageRoutes"/>.
/// Those items replace the registrations with their pages.
/// </summary>
public sealed class AndroidPageRegistry
{
    private readonly Dictionary<WinoPage, AndroidPageRoute> _pages = new();

    public AndroidPageRegistry()
    {
        // Onboarding (canvas Material pass boards Welcome, AddAccount, ImapSetup, Permissions). The setup pages that
        // own long work start it through InitializeAsync, as on iOS, so the entry reports pending work meanwhile.
        Register<WelcomePageFragment, WelcomePageV2ViewModel>(WinoPage.WelcomePageV2, AndroidPageHost.Root);
        Register<WelcomePageFragment, WelcomePageV2ViewModel>(WinoPage.WelcomeHostPage, AndroidPageHost.Root);
        Register<ProviderSelectionFragment, ProviderSelectionPageViewModel>(WinoPage.ProviderSelectionPage, AndroidPageHost.Root);
        Register<SpecialImapCredentialsFragment, SpecialImapCredentialsPageViewModel>(WinoPage.SpecialImapCredentialsPage, AndroidPageHost.Root);
        Register<ImapSetupFragment, ImapCalDavSettingsPageViewModel>(WinoPage.ImapCalDavSettingsPage, AndroidPageHost.Root,
            initialize: static (viewModel, mode, parameter) => viewModel.InitializeAsync(mode, parameter));
        Register<AccountSetupFragment, AccountSetupProgressPageViewModel>(WinoPage.AccountSetupProgressPage, AndroidPageHost.Root,
            initialize: static (viewModel, mode, parameter) => viewModel.InitializeAsync(mode, parameter));

        // Mail (canvas boards Inbox, Reader, Landscape, Tablet).
        Register<MailListFragment, MailListPageViewModel>(WinoPage.MailListPage, AndroidPageHost.ShellContent, MailListFragment.Prepare, MailListFragment.ActivatedAsync);
        Register<MailReaderFragment, MailRenderingPageViewModel>(WinoPage.MailRenderingPage, AndroidPageHost.RenderingFrame, MailReaderFragment.Prepare);
        // Compose (canvas boards Compose, ComposeDark): a rendering-frame page like Windows and the iPad, so it is full
        // screen on compact widths and in the reader pane on medium and expanded widths.
        Register<ComposeFragment, ComposePageViewModel>(WinoPage.ComposePage, AndroidPageHost.RenderingFrame, ComposeFragment.Prepare);

        // Placeholders until their items build them.
        this.RegisterCalendarPages();

        // Contacts and To Do (canvas Material pass boards Contacts, ContactEditor, ToDo, TaskDetail).
        this.RegisterContactsAndToDoPages();

        // Settings (canvas boards Settings, SettingsMessageList): every page in the Settings surface.
        this.RegisterSettingsPages();
    }

    public AndroidPageRegistry Register<TFragment, TViewModel>(WinoPage page, AndroidPageHost host,
        Action<PageEntry>? preparing = null, Func<PageEntry, Task>? activated = null)
        where TFragment : WinoFragment<TViewModel>, new()
        where TViewModel : CoreBaseViewModel
    {
        _pages[page] = new AndroidPageRoute(static () => new TFragment(), static services => services.GetRequiredService<TViewModel>(), host, typeof(TFragment))
        {
            Preparing = preparing,
            Activated = activated
        };
        return this;
    }

    /// <summary>
    /// A page whose activation and release call the ViewModel's own awaitable methods instead of OnNavigatedTo and
    /// OnNavigatedFrom (the iOS and Mac InitializeAsync and DeactivateAsync overrides).
    /// </summary>
    public AndroidPageRegistry Register<TFragment, TViewModel>(WinoPage page, AndroidPageHost host,
        Func<TViewModel, NavigationMode, object?, Task>? initialize, Func<TViewModel, Task>? deactivate = null, Func<PageEntry, Task>? renavigate = null)
        where TFragment : WinoFragment<TViewModel>, new()
        where TViewModel : CoreBaseViewModel
    {
        _pages[page] = new AndroidPageRoute(static () => new TFragment(), static services => services.GetRequiredService<TViewModel>(), host, typeof(TFragment))
        {
            Initialize = initialize is null ? null : (entry, mode) => entry.ViewModel is TViewModel viewModel ? initialize(viewModel, mode, entry.Parameter) : Task.CompletedTask,
            Deactivate = deactivate is null ? null : entry => entry.ViewModel is TViewModel viewModel ? deactivate(viewModel) : Task.CompletedTask,
            Renavigate = renavigate
        };
        return this;
    }

    /// <summary>A page without a ViewModel.</summary>
    public AndroidPageRegistry Register<TFragment>(WinoPage page, AndroidPageHost host) where TFragment : WinoFragment, new()
    {
        _pages[page] = new AndroidPageRoute(static () => new TFragment(), null, host, typeof(TFragment));
        return this;
    }

    public bool TryGet(WinoPage page, out AndroidPageRoute route)
    {
        if (_pages.TryGetValue(page, out var found))
        {
            route = found;
            return true;
        }
        route = new AndroidPageRoute(static () => new PendingPageFragment(), null, AndroidPageHost.Root, typeof(PendingPageFragment));
        return false;
    }

    /// <summary>Maps an Android host to the shared coordinator's host kind (UIKitPageRegistry.ToHostKind).</summary>
    public static NavigationHostKind ToHostKind(AndroidPageHost host) => host switch
    {
        AndroidPageHost.ShellContent => NavigationHostKind.ShellContent,
        AndroidPageHost.RenderingFrame or AndroidPageHost.Push => NavigationHostKind.RenderingFrame,
        AndroidPageHost.Settings => NavigationHostKind.Settings,
        _ => NavigationHostKind.Window
    };
}

using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Navigation;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class NavigationCoordinatorTests
{
    private readonly FakeNavigationHost _host = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly NavigationCoordinator _coordinator;

    public NavigationCoordinatorTests()
    {
        _accounts.Setup(service => service.GetAccountsAsync()).ReturnsAsync(new List<MailAccount>());
        var services = new ServiceCollection()
            .AddSingleton(_accounts.Object)
            .BuildServiceProvider();
        _coordinator = new NavigationCoordinator(_host, services, new ImmediateDispatcher(), _messenger);
    }

    [Fact]
    public async Task ShellContentNavigationKeepsHistoryAndGoesBack()
    {
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ContactsPage, "list");
        await _coordinator.NavigateAsync(WinoPage.ContactEditPage, "edit");

        _coordinator.CurrentPage.Should().Be(WinoPage.ContactEditPage);
        _coordinator.CanGoBack().Should().BeTrue();

        (await _coordinator.GoBackAsync()).Should().BeTrue();

        _coordinator.CurrentPage.Should().Be(WinoPage.ContactsPage);
        _coordinator.CurrentParameter.Should().Be("list");
        _host.ShellContent.Should().Equal(WinoPage.ContactsPage, WinoPage.ContactEditPage, WinoPage.ContactsPage);
    }

    [Fact]
    public async Task GoingBackToTheFirstShellPageClearsTheShellContent()
    {
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ContactsPage);

        (await _coordinator.GoBackAsync()).Should().BeTrue();

        _host.ShellContentCleared.Should().BeTrue();
        _coordinator.CurrentPage.Should().Be(WinoPage.None);
        _coordinator.CanGoBack().Should().BeFalse();
    }

    [Fact]
    public async Task ShowingTheShellClearsOnboardingHistory()
    {
        await _coordinator.NavigateAsync(WinoPage.WelcomePageV2);
        await _coordinator.NavigateAsync(WinoPage.ProviderSelectionPage);
        _coordinator.CanGoBack().Should().BeTrue();

        await _coordinator.ShowShellAsync();

        _host.ShellActivations.Should().Be(1);
        _coordinator.IsShellReady.Should().BeTrue();
        _coordinator.CanGoBack().Should().BeFalse();
    }

    [Fact]
    public async Task PageThatRefusesToLeaveCancelsBackNavigation()
    {
        var editor = new ConfirmingViewModel(allow: false);
        _host.ViewModels[WinoPage.ContactEditPage] = editor;
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ContactsPage);
        await _coordinator.NavigateAsync(WinoPage.ContactEditPage);
        _coordinator.SetNavigationResult(NavigationResult.Saved());

        (await _coordinator.GoBackAsync()).Should().BeFalse();

        editor.Asked.Should().BeTrue();
        _coordinator.CurrentPage.Should().Be(WinoPage.ContactEditPage);
        _coordinator.CanGoBack().Should().BeTrue();
    }

    [Fact]
    public async Task PageReturnedToReceivesParameterAndResult()
    {
        var list = new BackAwareViewModel();
        _host.ViewModels[WinoPage.ContactsPage] = list;
        _host.ViewModels[WinoPage.ContactEditPage] = new ConfirmingViewModel(allow: true);
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ContactsPage, "list");
        await _coordinator.NavigateAsync(WinoPage.ContactEditPage);

        _messenger.Send(new BackBreadcrumNavigationRequested(Result: NavigationResult.Deleted("row")));
        await _coordinator.PendingNavigation;

        list.Parameter.Should().Be("list");
        list.Result.Should().NotBeNull();
        list.Result?.Kind.Should().Be(NavigationResultKind.Deleted);
        _coordinator.CurrentPage.Should().Be(WinoPage.ContactsPage);
    }

    [Fact]
    public async Task OnboardingPageWhileShellIsUpOpensSecondarySurface()
    {
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ProviderSelectionPage);
        await _coordinator.NavigateAsync(WinoPage.AccountSetupProgressPage);

        _host.Secondary.Should().Equal(WinoPage.ProviderSelectionPage, WinoPage.AccountSetupProgressPage);
        _host.IsShellShownValue.Should().BeTrue();

        (await _coordinator.GoBackAsync()).Should().BeTrue();
        _host.Secondary.Last().Should().Be(WinoPage.ProviderSelectionPage);

        (await _coordinator.GoBackAsync()).Should().BeTrue();
        _host.SecondaryShown.Should().BeFalse();
    }

    [Fact]
    public async Task RemovingTheLastAccountReturnsToWelcome()
    {
        await _coordinator.ShowShellAsync();
        await _coordinator.NavigateAsync(WinoPage.ContactsPage);

        _messenger.Send(new AccountRemovedMessage(new MailAccount()));
        await _coordinator.PendingNavigation;

        _host.MainContent.Last().Should().Be(WinoPage.WelcomePageV2);
        _host.IsShellShownValue.Should().BeFalse();
        _host.SurfacesDismissed.Should().BeTrue();
        _coordinator.CurrentPage.Should().Be(WinoPage.WelcomePageV2);
        _coordinator.CanGoBack().Should().BeFalse();
    }

    [Fact]
    public async Task RemovingAnAccountKeepsTheShellWhileOthersRemain()
    {
        _accounts.Setup(service => service.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { new() });
        await _coordinator.ShowShellAsync();

        _messenger.Send(new AccountRemovedMessage(new MailAccount()));
        await _coordinator.PendingNavigation;

        _host.IsShellShownValue.Should().BeTrue();
        _host.MainContent.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAnAccountDuringOnboardingDoesNotNavigate()
    {
        await _coordinator.NavigateAsync(WinoPage.WelcomePageV2);

        _messenger.Send(new AccountRemovedMessage(new MailAccount()));
        await _coordinator.PendingNavigation;

        _host.MainContent.Should().Equal(WinoPage.WelcomePageV2);
        _accounts.Verify(service => service.GetAccountsAsync(), Times.Never);
    }

    [Fact]
    public void ModeIsAvailableOnlyWhenTheHeadHasItsRootRoute()
    {
        _host.Routes.Remove(WinoPage.ToDoPage);

        _coordinator.IsModeAvailable(WinoApplicationMode.Mail).Should().BeTrue();
        _coordinator.IsModeAvailable(WinoApplicationMode.Calendar).Should().BeTrue();
        _coordinator.IsModeAvailable(WinoApplicationMode.Contacts).Should().BeTrue();
        _coordinator.IsModeAvailable(WinoApplicationMode.Tasks).Should().BeFalse();
        _coordinator.IsModeAvailable(WinoApplicationMode.Settings).Should().BeTrue();

        _host.SettingsAvailable = false;
        _coordinator.IsModeAvailable(WinoApplicationMode.Settings).Should().BeFalse();
        _coordinator.ChangeApplicationMode(WinoApplicationMode.Settings).Should().BeFalse();
    }

    [Fact]
    public async Task AccountSetupInProgressOwnsTheMainWindow()
    {
        await _coordinator.NavigateAsync(WinoPage.AccountSetupProgressPage);
        _host.PendingWork = true;

        _coordinator.CanQuit.Should().BeFalse();
        (await _coordinator.EnsureShellAsync(WinoApplicationMode.Mail)).Should().BeFalse();
        _host.IsShellShownValue.Should().BeFalse();

        _host.PendingWork = false;
        _coordinator.CanQuit.Should().BeTrue();
        (await _coordinator.EnsureShellAsync(WinoApplicationMode.Mail)).Should().BeTrue();
        _host.ActivatedModes.Should().Equal(WinoApplicationMode.Mail);
    }

    [Fact]
    public async Task RoutesByHostKind()
    {
        await _coordinator.ShowShellAsync();

        _coordinator.Navigate(WinoPage.MailRenderingPage, "mail").Should().BeTrue();
        await _coordinator.PendingNavigation;
        _coordinator.Navigate(WinoPage.AboutPage).Should().BeTrue();
        await _coordinator.PendingNavigation;
        _coordinator.Navigate(WinoPage.SettingsPage).Should().BeFalse();

        _host.RenderingFrame.Should().Equal(WinoPage.MailRenderingPage);
        _host.SettingsPages.Should().Equal(WinoPage.AboutPage);
        _coordinator.CurrentPage.Should().Be(WinoPage.None);
    }

    [Fact]
    public async Task StopReleasesAndRefusesFurtherNavigation()
    {
        await _coordinator.ShowShellAsync();

        await _coordinator.StopAsync();

        _host.Released.Should().BeTrue();
        _host.SettingsStopped.Should().BeTrue();
        _coordinator.Navigate(WinoPage.ContactsPage).Should().BeFalse();
        _coordinator.ChangeApplicationMode(WinoApplicationMode.Mail).Should().BeFalse();
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class ConfirmingViewModel(bool allow) : IConfirmBackNavigation
    {
        public bool Asked { get; private set; }

        public ValueTask<bool> CanNavigateBackAsync()
        {
            Asked = true;
            return ValueTask.FromResult(allow);
        }
    }

    private sealed class BackAwareViewModel : IBackNavigationAware
    {
        public object? Parameter { get; private set; }
        public NavigationResult? Result { get; private set; }

        public void OnNavigatedBack(object? parameter, NavigationResult? result)
        {
            Parameter = parameter;
            Result = result;
        }
    }

    private sealed class FakeNavigationHost : INavigationHost
    {
        public Dictionary<WinoPage, NavigationHostKind> Routes { get; } = new()
        {
            [WinoPage.MailListPage] = NavigationHostKind.ShellContent,
            [WinoPage.CalendarPage] = NavigationHostKind.ShellContent,
            [WinoPage.ContactsPage] = NavigationHostKind.ShellContent,
            [WinoPage.ContactEditPage] = NavigationHostKind.ShellContent,
            [WinoPage.ToDoPage] = NavigationHostKind.ShellContent,
            [WinoPage.WelcomePageV2] = NavigationHostKind.Window,
            [WinoPage.ProviderSelectionPage] = NavigationHostKind.Window,
            [WinoPage.AccountSetupProgressPage] = NavigationHostKind.Window,
            [WinoPage.MailRenderingPage] = NavigationHostKind.RenderingFrame,
            [WinoPage.AboutPage] = NavigationHostKind.Settings
        };

        public Dictionary<WinoPage, object> ViewModels { get; } = new();
        public List<WinoPage> MainContent { get; } = new();
        public List<WinoPage> ShellContent { get; } = new();
        public List<WinoPage> Secondary { get; } = new();
        public List<WinoPage> RenderingFrame { get; } = new();
        public List<WinoPage> SettingsPages { get; } = new();
        public List<WinoApplicationMode> ActivatedModes { get; } = new();
        public bool IsShellShownValue { get; private set; }
        public bool SecondaryShown { get; private set; }
        public bool ShellContentCleared { get; private set; }
        public bool SurfacesDismissed { get; private set; }
        public bool Released { get; private set; }
        public bool SettingsStopped { get; private set; }
        public bool SettingsAvailable { get; set; } = true;
        public bool PendingWork { get; set; }
        public int ShellActivations { get; private set; }
        private WinoPage? _shellPage;

        public bool TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind) => Routes.TryGetValue(page, out hostKind);
        public Type? GetPageType(WinoPage page) => Routes.ContainsKey(page) ? typeof(object) : null;
        public bool IsShellShown => IsShellShownValue;
        public bool HasMainContent => IsShellShownValue || MainContent.Count > 0;
        public bool MainContentHasPendingWork => PendingWork;
        public object? ShellContentViewModel => _shellPage is { } page && ViewModels.TryGetValue(page, out var viewModel) ? viewModel : null;
        public bool IsSecondaryWindowShown => SecondaryShown;
        public bool IsSettingsAvailable => SettingsAvailable;

        public Task ShowInRenderingFrameAsync(WinoPage page, object? parameter)
        {
            RenderingFrame.Add(page);
            return Task.CompletedTask;
        }

        public Task SetShellContentAsync(WinoPage page, object? parameter)
        {
            ShellContent.Add(page);
            _shellPage = page;
            return Task.CompletedTask;
        }

        public Task ClearShellContentAsync()
        {
            ShellContentCleared = true;
            return Task.CompletedTask;
        }

        public Task ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
        {
            committed();
            MainContent.Add(page);
            IsShellShownValue = false;
            _shellPage = null;
            return Task.CompletedTask;
        }

        public Task ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
        {
            committed();
            SecondaryShown = true;
            Secondary.Add(page);
            return Task.CompletedTask;
        }

        public Task CloseSecondaryWindowAsync(bool closedByUser)
        {
            SecondaryShown = false;
            return Task.CompletedTask;
        }

        public Task<Func<Task>> PresentShellAsync(Action committed)
        {
            IsShellShownValue = true;
            committed();
            return Task.FromResult<Func<Task>>(() =>
            {
                ShellActivations++;
                return Task.CompletedTask;
            });
        }

        public Task BringShellToFrontAsync() => Task.CompletedTask;

        public Task ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
        {
            ActivatedModes.Add(mode);
            return Task.CompletedTask;
        }

        public Task DismissShellSurfacesAsync()
        {
            SurfacesDismissed = true;
            return Task.CompletedTask;
        }

        public Task ReleaseMainContentAsync()
        {
            Released = true;
            IsShellShownValue = false;
            return Task.CompletedTask;
        }

        public Task ShowSettingsAsync(WinoPage? page, object? parameter)
        {
            if (page is { } shown) SettingsPages.Add(shown);
            return Task.CompletedTask;
        }

        public bool TrySettingsGoBack() => false;
        public Task CloseSettingsAsync() => Task.CompletedTask;

        public Task StopSettingsAsync()
        {
            SettingsStopped = true;
            return Task.CompletedTask;
        }
    }
}

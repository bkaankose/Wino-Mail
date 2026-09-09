using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Core.Tests;

public class SettingsPageViewModelTests
{
    [Theory]
    [InlineData(WinoIntelligenceEntitlementState.SignedOut, false)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription, false)]
    [InlineData(WinoIntelligenceEntitlementState.Expired, false)]
    [InlineData(WinoIntelligenceEntitlementState.Unavailable, false)]
    [InlineData(WinoIntelligenceEntitlementState.Active, true)]
    [InlineData(WinoIntelligenceEntitlementState.QuotaExhausted, true)]
    public void SettingsMenu_ProjectsIntelligenceOnlyForSurfaceAccess(
        WinoIntelligenceEntitlementState state,
        bool expected)
    {
        var entitlement = Entitlement(state);
        var service = EntitlementService(entitlement);
        var provider = new SettingsMenuProvider(Mock.Of<INavigationService>(), service.Object)
        {
            Dispatcher = new ImmediateDispatcher(),
        };

        provider.ShellMenu.Items
            .OfType<SettingsShellPageMenuItem>()
            .Any(item => item.PageType == WinoPage.WinoIntelligencePage)
            .Should().Be(expected);
    }

    [Fact]
    public void SettingsMenu_CalendarGroupStartsWithPreferencesAndUsesRenderingPathIcon()
    {
        var service = EntitlementService(Entitlement(WinoIntelligenceEntitlementState.Active));
        var provider = new SettingsMenuProvider(Mock.Of<INavigationService>(), service.Object)
        {
            Dispatcher = new ImmediateDispatcher(),
        };

        var calendarGroup = provider.ShellMenu.Items
            .OfType<SettingsShellGroupMenuItem>()
            .Single(group => group.Title == Translator.SettingsOptions_CalendarSection);

        calendarGroup.SubMenuItems.Select(item => item.PageType).Should().Equal(
            WinoPage.CalendarPreferenceSettingsPage,
            WinoPage.CalendarRenderingSettingsPage,
            WinoPage.CalendarNotificationSettingsPage);
        calendarGroup.SubMenuItems[1].HasIconPathData.Should().BeTrue();
        calendarGroup.SubMenuItems[1].IconPathData.Should().StartWith("F1 M 15.078125 1.25");
    }

    [Fact]
    public async Task SearchSettingsAsync_HidesIntelligenceRoutesWhenAccessIsDenied()
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync(
        [
            new MailAccount
            {
                Id = Guid.NewGuid(),
                Name = "Work",
                Address = "work@example.com",
                ProviderType = MailProviderType.Gmail,
                IsMailAccessGranted = true,
            },
        ]);
        var entitlementService = EntitlementService(Entitlement(WinoIntelligenceEntitlementState.Expired));
        var viewModel = new SettingsPageViewModel(
            Mock.Of<INavigationService>(),
            Mock.Of<IStatePersistanceService>(),
            accountService.Object,
            entitlementService.Object,
            new SettingsMenuProvider(Mock.Of<INavigationService>(), entitlementService.Object));

        var results = await viewModel.SearchSettingsAsync("intelligence");

        results.Select(item => item.PageType).Should().NotContain(
        [
            WinoPage.WinoIntelligencePage,
            WinoPage.WinoIntelligenceManagementPage,
            WinoPage.IntelligenceCoveragePage,
        ]);
    }

    [Fact]
    public async Task UpdateActivePageAsync_RefreshesAccountCount()
    {
        var accountService = new Mock<IAccountService>();
        accountService.SetupSequence(service => service.GetAccountsAsync())
            .ReturnsAsync([new MailAccount(), new MailAccount()])
            .ReturnsAsync([new MailAccount()]);
        var viewModel = CreateViewModel(accountService.Object);

        await viewModel.UpdateActivePageAsync(WinoPage.ManageAccountsPage);
        viewModel.CurrentDescription.Should().Be(string.Format(Translator.SettingsOptions_AccountsSummary, 2));

        await viewModel.UpdateActivePageAsync(WinoPage.ManageAccountsPage);
        viewModel.CurrentDescription.Should().Be(string.Format(Translator.SettingsOptions_AccountsSummary, 1));
    }

    [Fact]
    public async Task UpdateActivePageAsync_UsesAccountNameForAccountDetails()
    {
        var accountId = Guid.NewGuid();
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        accountService.Setup(service => service.GetAccountAsync(accountId))
            .ReturnsAsync(new MailAccount { Id = accountId, Name = "Work" });
        var viewModel = CreateViewModel(accountService.Object);

        await viewModel.UpdateActivePageAsync(WinoPage.AccountDetailsPage, accountId, "Account details");

        viewModel.CurrentDescription.Should().Be(string.Format(Translator.SettingsAccountDetails_Subtitle, "Work"));
    }

    [Theory]
    [InlineData(WinoPage.MailFiltersPage)]
    [InlineData(WinoPage.MailFilterEditorPage)]
    public void GetRootPage_MapsMailFilterSubpagesToManageAccounts(WinoPage pageType)
    {
        SettingsNavigationInfoProvider.GetRootPage(pageType).Should().Be(WinoPage.ManageAccountsPage);
    }

    [Fact]
    public async Task SearchSettingsAsync_ReturnsAccountSpecificMailFiltersRoute()
    {
        var accountId = Guid.NewGuid();
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync(
        [
            new MailAccount
            {
                Id = accountId,
                Name = "Work",
                Address = "work@example.com",
                ProviderType = MailProviderType.Gmail,
                IsMailAccessGranted = true
            }
        ]);
        var viewModel = CreateViewModel(accountService.Object);

        var result = (await viewModel.SearchSettingsAsync("mail filters"))
            .Single(item => item.PageType == WinoPage.MailFiltersPage);

        result.NavigationRoute.Should().NotBeNull();
        result.NavigationRoute!.Steps.Select(step => step.PageType).Should().Equal(
            WinoPage.ManageAccountsPage,
            WinoPage.AccountDetailsPage,
            WinoPage.MailFiltersPage);
        result.NavigationRoute.Steps[1].Parameter.Should().Be(
            new AccountDetailsNavigationContext(accountId, AccountDetailsTab.Mail));
        result.NavigationRoute.Destination.Parameter.Should().Be(accountId);
    }

    [Fact]
    public async Task SearchSettingsAsync_ReturnsImapSettingsThroughGeneralTab()
    {
        var accountId = Guid.NewGuid();
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync(
        [
            new MailAccount
            {
                Id = accountId,
                Name = "Personal",
                Address = "personal@example.com",
                ProviderType = MailProviderType.IMAP4,
                IsMailAccessGranted = true
            }
        ]);
        var viewModel = CreateViewModel(accountService.Object);

        var result = (await viewModel.SearchSettingsAsync("SMTP"))
            .Single(item => item.PageType == WinoPage.ImapCalDavSettingsPage);

        result.NavigationRoute!.Steps[1].Parameter.Should().Be(
            new AccountDetailsNavigationContext(accountId, AccountDetailsTab.General));
        result.NavigationRoute.Destination.Parameter.Should().Be(accountId);
    }

    private static SettingsPageViewModel CreateViewModel(IAccountService accountService)
    {
        var entitlementService = EntitlementService(Entitlement(WinoIntelligenceEntitlementState.Active));

        return new SettingsPageViewModel(
            Mock.Of<INavigationService>(),
            Mock.Of<IStatePersistanceService>(),
            accountService,
            entitlementService.Object,
            new SettingsMenuProvider(Mock.Of<INavigationService>(), entitlementService.Object));
    }

    private static WinoIntelligenceEntitlementSnapshot Entitlement(WinoIntelligenceEntitlementState state)
        => new(state, state == WinoIntelligenceEntitlementState.SignedOut ? null : Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static Mock<IWinoIntelligenceEntitlementService> EntitlementService(
        WinoIntelligenceEntitlementSnapshot entitlement)
    {
        var service = new Mock<IWinoIntelligenceEntitlementService>();
        service.SetupGet(item => item.Current).Returns(entitlement);
        service.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entitlement);
        service.Setup(item => item.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entitlement);
        return service;
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}

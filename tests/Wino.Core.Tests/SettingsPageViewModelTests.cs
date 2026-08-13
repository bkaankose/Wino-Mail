using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Core.Tests;

public class SettingsPageViewModelTests
{
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

    private static SettingsPageViewModel CreateViewModel(IAccountService accountService)
        => new(
            Mock.Of<INavigationService>(),
            Mock.Of<IStatePersistanceService>(),
            accountService);
}

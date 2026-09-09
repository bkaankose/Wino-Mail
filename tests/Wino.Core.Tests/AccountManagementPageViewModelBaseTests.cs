using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Core.Tests;

public class AccountManagementPageViewModelBaseTests
{
    [Fact]
    public async Task AccountUsage_CountsEveryMailboxInsideMergedAccounts()
    {
        var billingService = new Mock<IWinoBillingService>();
        billingService.Setup(service => service.HasUnlimitedAccountsAsync(default)).ReturnsAsync(false);

        var viewModel = new TestAccountManagementPageViewModel(billingService.Object);
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        var provider = Mock.Of<IProviderDetail>();
        var mergedAccounts = new[]
        {
            new AccountProviderDetailViewModel(provider, CreateAccount()),
            new AccountProviderDetailViewModel(provider, CreateAccount())
        };

        viewModel.Accounts.Add(new MergedAccountProviderDetailViewModel(
            new MergedInbox { Id = Guid.NewGuid(), Name = "Linked" },
            [.. mergedAccounts]));

        viewModel.UsedAccountCount.Should().Be(2);
        viewModel.IsAccountCreationAlmostOnLimit.Should().BeTrue();
        changedProperties.Should().Contain(nameof(viewModel.UsedAccountsString));
        changedProperties.Should().Contain(nameof(viewModel.IsAccountCreationAlmostOnLimit));

        viewModel.Accounts.Add(new AccountProviderDetailViewModel(provider, CreateAccount()));
        await viewModel.ManageStorePurchasesAsync();

        viewModel.UsedAccountCount.Should().Be(3);
        viewModel.IsAccountCreationBlocked.Should().BeTrue();
    }

    private static MailAccount CreateAccount() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Account",
        Address = $"{Guid.NewGuid():N}@example.test"
    };

    private sealed class TestAccountManagementPageViewModel : AccountManagementPageViewModelBase
    {
        public TestAccountManagementPageViewModel(IWinoBillingService billingService)
            : base(
                Mock.Of<IDialogServiceBase>(),
                Mock.Of<INavigationService>(),
                Mock.Of<IAccountService>(),
                Mock.Of<IProviderService>(),
                billingService,
                Mock.Of<IWinoAccountProfileService>(),
                Mock.Of<IAuthenticationProvider>(),
                Mock.Of<IPreferencesService>())
        {
        }

        public override Task InitializeAccountsAsync() => Task.CompletedTask;
    }
}

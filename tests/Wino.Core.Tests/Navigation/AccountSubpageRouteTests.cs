using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Xunit;

namespace Wino.Core.Tests.Navigation;

public class AccountSubpageRouteTests
{
    [Fact]
    public void ForAccountSubpage_WalksManageAccountsThenTheAccountThenThePage()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Name = "Work", Address = "user@contoso.test", ProviderType = MailProviderType.Exchange };

        var route = SettingsNavigationRoute.ForAccountSubpage(account, "Junk email", WinoPage.JunkEmailSettingsPage, AccountDetailsTab.Mail);

        route.Steps.Select(step => step.PageType).Should().Equal(
            WinoPage.ManageAccountsPage,
            WinoPage.AccountDetailsPage,
            WinoPage.JunkEmailSettingsPage);
        route.Steps[1].Parameter.Should().Be(new AccountDetailsNavigationContext(account.Id, AccountDetailsTab.Mail));
        route.Destination.Parameter.Should().Be(account.Id);
        route.Destination.PageTitle.Should().Be("Junk email");
    }

    [Fact]
    public void ActivationContext_CarriesNoRouteUnlessAskedTo()
    {
        new SettingsPageActivationContext(WinoPage.ManageAccountsPage).Route.Should().BeNull();
    }

    [Theory]
    [InlineData(MailProviderType.Exchange, true)]
    [InlineData(MailProviderType.Outlook, false)]
    [InlineData(MailProviderType.Gmail, false)]
    [InlineData(MailProviderType.IMAP4, false)]
    [InlineData(MailProviderType.POP3, false)]
    public void JunkEmailEntry_IsOfferedForExchangeAccountsOnly(MailProviderType providerType, bool expected)
    {
        var menuItem = new AccountMenuItem(new MailAccount { Id = Guid.NewGuid(), Name = "Account", ProviderType = providerType });

        ((IAccountNavigationMenuItem)menuItem).SupportsJunkEmailSettings.Should().Be(expected);
    }
}

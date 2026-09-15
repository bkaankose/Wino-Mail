using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// Exchange in the account wizard: mail runs on the server, while calendar and contacts stay on the
/// local backends until their Exchange surfaces are ported.
/// </summary>
public sealed class ExchangeProviderSelectionTests
{
    private static ProviderSelectionPageViewModel CreateViewModel()
        => new(
            Mock.Of<IAccountService>(),
            Mock.Of<IDialogServiceBase>(),
            Mock.Of<IProviderService>(),
            Mock.Of<INewThemeService>(),
            new WelcomeWizardContext())
        {
            SelectedProvider = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None)
        };

    [Fact]
    public void Exchange_IsNeitherOAuthNorImapFamily()
    {
        var viewModel = CreateViewModel();

        viewModel.IsExchange.Should().BeTrue();
        viewModel.IsOAuthProvider.Should().BeFalse();
        viewModel.IsImapFamily.Should().BeFalse();
        viewModel.IsPop3.Should().BeFalse();
        viewModel.MailProviderModeLabel.Should().Be(Translator.ProviderDetail_Exchange_Title);
    }

    [Fact]
    public void Exchange_CoercesCalendarContactsAndTasksToLocal()
    {
        var viewModel = CreateViewModel();

        viewModel.IsCalendarProviderModeAvailable.Should().BeFalse();
        viewModel.IsContactProviderModeAvailable.Should().BeFalse();
        viewModel.IsTaskProviderModeAvailable.Should().BeFalse();
        viewModel.MailMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Local);
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Local);
    }

    [Fact]
    public void ProviderDetail_DescribesExchange()
    {
        var detail = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None);

        detail.IsSupported.Should().BeTrue();
        detail.Name.Should().Be(Translator.ProviderDetail_Exchange_Title);
        detail.Description.Should().Be(Translator.ProviderDetail_Exchange_Description);
        detail.ProviderImage.Should().Be("/Assets/Providers/Exchange.png");
    }

    [Fact]
    public void WizardContext_FlagsExchange()
    {
        var context = new WelcomeWizardContext
        {
            SelectedProvider = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None)
        };

        context.IsExchange.Should().BeTrue();
        context.IsGenericCustomMail.Should().BeFalse("Exchange has its own settings page");
        context.IsOAuthProvider.Should().BeFalse();
    }
}

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
/// Exchange in the account wizard: mail, calendar, contacts and tasks all run on the server, the way
/// they do for the OAuth providers, with the local backends available as the alternative.
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
    public void Exchange_OffersProviderCalendarContactsAndTasks()
    {
        var viewModel = CreateViewModel();

        viewModel.IsServerProvider.Should().BeTrue();
        viewModel.IsCalendarProviderModeAvailable.Should().BeTrue();
        viewModel.IsContactProviderModeAvailable.Should().BeTrue();
        viewModel.IsTaskProviderModeAvailable.Should().BeTrue();
        viewModel.MailMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.ContactMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.TaskMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.CalendarProviderModeLabel.Should().Be(Translator.ProviderDetail_Exchange_Title);
        viewModel.ContactProviderModeLabel.Should().Be(Translator.ProviderDetail_Exchange_Title);
        viewModel.TaskProviderModeLabel.Should().Be(Translator.ProviderDetail_Exchange_Title);
        viewModel.CalendarSourceOptions.Should().ContainSingle(o => o == Translator.ProviderSelection_SourceProviderCalendar);
        viewModel.TaskSourceOptions.Should().Contain(Translator.ProviderSelection_SourceProviderTasks);
    }

    [Fact]
    public void Exchange_LocalChoicesStayAvailable()
    {
        var viewModel = CreateViewModel();

        viewModel.CalendarMode = AccountCapabilityMode.Local;
        viewModel.ContactMode = AccountCapabilityMode.Local;
        viewModel.TaskMode = AccountCapabilityMode.Local;

        viewModel.IsCalendarChoiceLocal.Should().BeTrue();
        viewModel.IsContactChoiceLocal.Should().BeTrue();
        viewModel.IsTaskChoiceLocal.Should().BeTrue();
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

using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ExchangeSettingsPageViewModelTests
{
    private const string EwsUrl = "https://mail.example.com/EWS/Exchange.asmx";

    [Fact]
    public void Save_WithValidInput_BuildsExchangeSetupResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = new ExchangeSettingsPageViewModel(context)
        {
            DisplayName = "Test User",
            EmailAddress = "user@example.com",
            EwsUrl = EwsUrl,
            Password = "secret"
        };

        viewModel.SaveCommand.Execute(null);

        viewModel.ValidationMessage.Should().BeEmpty();

        var result = context.ImapCalDavSetupResult;
        result.Should().NotBeNull();
        result!.IsMailAccessGranted.Should().BeTrue();
        result.IsCalendarAccessGranted.Should().BeFalse("EWS calendar is deferred to Phase 2");
        result.ServerInformation.Should().NotBeNull();
        result.ServerInformation!.IncomingServer.Should().Be(EwsUrl);
        result.ServerInformation.IncomingServerType.Should().Be(CustomIncomingServerType.Exchange);
        result.ServerInformation.IncomingServerUsername.Should().Be("user@example.com");
    }

    [Fact]
    public void Save_WithMissingFields_SetsValidationAndDoesNotBuildResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = new ExchangeSettingsPageViewModel(context); // all inputs empty

        viewModel.SaveCommand.Execute(null);

        viewModel.ValidationMessage.Should().NotBeNullOrEmpty();
        context.ImapCalDavSetupResult.Should().BeNull();
    }

    [Fact]
    public void Save_WithInvalidEwsUrl_SetsValidationAndDoesNotBuildResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = new ExchangeSettingsPageViewModel(context)
        {
            DisplayName = "Test User",
            EmailAddress = "user@example.com",
            EwsUrl = "not-a-url",
            Password = "secret"
        };

        viewModel.SaveCommand.Execute(null);

        viewModel.ValidationMessage.Should().NotBeNullOrEmpty();
        context.ImapCalDavSetupResult.Should().BeNull();
    }
}

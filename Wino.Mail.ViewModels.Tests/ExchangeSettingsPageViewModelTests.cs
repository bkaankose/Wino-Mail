using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ExchangeSettingsPageViewModelTests
{
    private const string EwsUrl = "https://mail.example.com/EWS/Exchange.asmx";

    // The NTLM and validation paths exercised here never touch the OAuth authenticator or the
    // autodiscovery service, so null dependencies are safe.
    private static ExchangeSettingsPageViewModel CreateViewModel(WelcomeWizardContext context)
        => new(context, null, null);

    [Fact]
    public async Task Save_WithValidInput_BuildsExchangeSetupResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context);
        viewModel.DisplayName = "Test User";
        viewModel.EmailAddress = "user@example.com";
        viewModel.EwsUrl = EwsUrl;
        viewModel.Password = "secret";

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ValidationMessage.Should().BeEmpty();

        var result = context.ImapCalDavSetupResult;
        result.Should().NotBeNull();
        result!.IsMailAccessGranted.Should().BeTrue();
        result.IsCalendarAccessGranted.Should().BeFalse("EWS calendar is deferred to Phase 2");
        result.ServerInformation.Should().NotBeNull();
        result.ServerInformation!.IncomingServer.Should().Be(EwsUrl);
        result.ServerInformation.IncomingServerType.Should().Be(CustomIncomingServerType.Exchange);
        result.ServerInformation.IncomingServerUsername.Should().Be("user@example.com");
        result.ServerInformation.UseOAuthAuthentication.Should().BeFalse();
    }

    [Fact]
    public async Task Save_WithMissingFields_SetsValidationAndDoesNotBuildResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context); // all inputs empty

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ValidationMessage.Should().NotBeNullOrEmpty();
        context.ImapCalDavSetupResult.Should().BeNull();
    }

    [Fact]
    public async Task Save_WithInvalidEwsUrl_SetsValidationAndDoesNotBuildResult()
    {
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context);
        viewModel.DisplayName = "Test User";
        viewModel.EmailAddress = "user@example.com";
        viewModel.EwsUrl = "not-a-url";
        viewModel.Password = "secret";

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ValidationMessage.Should().NotBeNullOrEmpty();
        context.ImapCalDavSetupResult.Should().BeNull();
    }

    [Fact]
    public async Task Save_ModernAuthWithoutAuthority_SetsValidationAndDoesNotSignIn()
    {
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context);
        viewModel.DisplayName = "Test User";
        viewModel.EmailAddress = "user@example.com";
        viewModel.EwsUrl = EwsUrl;
        viewModel.UseModernAuth = true;
        viewModel.OAuthAuthority = string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ValidationMessage.Should().Contain("Authority");
        context.ImapCalDavSetupResult.Should().BeNull();
    }

    [Fact]
    public async Task Save_ModernAuthMissingPasswordNotRequired()
    {
        // With modern auth on, the absence of a password must not trip the NTLM password check.
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context);
        viewModel.DisplayName = "Test User";
        viewModel.EmailAddress = "user@example.com";
        viewModel.EwsUrl = EwsUrl;
        viewModel.UseModernAuth = true;
        viewModel.OAuthAuthority = string.Empty; // stops before interactive sign-in

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ValidationMessage.Should().NotContain("Password");
    }
}

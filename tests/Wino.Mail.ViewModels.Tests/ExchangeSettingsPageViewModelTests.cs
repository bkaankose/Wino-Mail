using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ExchangeSettingsPageViewModelTests
{
    private const string EwsUrl = "https://mail.example.com/EWS/Exchange.asmx";

    private static ExchangeSettingsPageViewModel CreateViewModel(WelcomeWizardContext context)
        => new(context, null!, null!, null!, null!, new UndecidedProbe());

    /// <summary>No server in a unit test: transport detection stays undecided, as it does offline.</summary>
    private sealed class UndecidedProbe : IMapiConnectionProbe
    {
        public Task<MapiProbeResult> ProbeAsync(MailAccount account, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default)
            => Task.FromResult(ExchangeTransport.Automatic);
    }

    [Fact]
    public async Task Save_WithValidInput_BuildsExchangeSetupResult()
    {
        var context = new WelcomeWizardContext { IsCalendarAccessEnabled = true };
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
        result.IsCalendarAccessGranted.Should().BeFalse("Exchange calendar sync is not ported yet; the calendar stays local");
        result.ServerInformation.Should().NotBeNull();
        result.ServerInformation!.IncomingServer.Should().Be(EwsUrl);
        result.ServerInformation.IncomingServerType.Should().Be(CustomIncomingServerType.Exchange);
        result.ServerInformation.IncomingServerUsername.Should().Be("user@example.com");
        result.ServerInformation.UseOAuthAuthentication.Should().BeFalse();
        result.ServerInformation.ExchangeTransport.Should().Be(ExchangeTransport.Automatic);
        result.ServerInformation.DetectedExchangeTransport.Should().Be(ExchangeTransport.Automatic, "the probe could not decide");
        result.ServerInformation.CalendarSupportMode.Should().Be(ImapCalendarSupportMode.LocalOnly, "the wizard asked for a local calendar");
    }

    [Fact]
    public async Task Save_WithExplicitTransport_DoesNotDetect()
    {
        var context = new WelcomeWizardContext();
        var viewModel = CreateViewModel(context);
        viewModel.DisplayName = "Test User";
        viewModel.EmailAddress = "user@example.com";
        viewModel.EwsUrl = EwsUrl;
        viewModel.Password = "secret";
        viewModel.TransportIndex = (int)ExchangeTransport.Ews;

        await viewModel.SaveCommand.ExecuteAsync(null);

        context.ImapCalDavSetupResult!.ServerInformation!.ExchangeTransport.Should().Be(ExchangeTransport.Ews);
        context.ImapCalDavSetupResult.ServerInformation.EffectiveExchangeTransport.Should().Be(ExchangeTransport.Ews);
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

    [Fact]
    public void ModernAuth_TogglesTheAuthMethodIndexAndExpandsAdvancedWhenAuthorityIsMissing()
    {
        var viewModel = CreateViewModel(new WelcomeWizardContext());

        viewModel.UseModernAuth = true;

        viewModel.AuthMethodIndex.Should().Be(1);
        viewModel.IsPasswordAuth.Should().BeFalse();
        viewModel.IsAdvancedExpanded.Should().BeTrue("the authority field is required and empty");

        viewModel.AuthMethodIndex = 0;

        viewModel.UseModernAuth.Should().BeFalse();
        viewModel.IsPasswordAuth.Should().BeTrue();
    }
}

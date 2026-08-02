using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authentication;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class MailAuthenticatorConfigurationTests
{
    private readonly MailAuthenticatorConfiguration _configuration = new();

    [Fact]
    public void GetGmailScopes_BaseMail_DoesNotRequestMailFilterPermission()
    {
        var scopes = _configuration.GetGmailScopes(new ProviderAuthorizationRequest(true, false, []));

        scopes.Should().Contain("https://mail.google.com/");
        scopes.Should().NotContain("https://www.googleapis.com/auth/gmail.settings.basic");
    }

    [Fact]
    public void GetGmailScopes_MailFilters_RequestsOnlyFeaturePermissionInAdditionToBaseScopes()
    {
        var scopes = _configuration.GetGmailScopes(
            new ProviderAuthorizationRequest(true, false, [ProviderFeature.MailFilters]));

        scopes.Should().Contain("https://www.googleapis.com/auth/gmail.settings.basic");
    }

    [Fact]
    public void GetOutlookScopes_BaseMail_DoesNotRequestMailFilterPermission()
    {
        var scopes = _configuration.GetOutlookScopes(new ProviderAuthorizationRequest(true, false, []));

        scopes.Should().Contain("mail.readwrite");
        scopes.Should().NotContain("MailboxSettings.ReadWrite");
    }

    [Fact]
    public void GetOutlookScopes_MailFilters_RequestsFeaturePermission()
    {
        var scopes = _configuration.GetOutlookScopes(
            new ProviderAuthorizationRequest(true, false, [ProviderFeature.MailFilters]));

        scopes.Should().Contain("MailboxSettings.ReadWrite");
    }
}

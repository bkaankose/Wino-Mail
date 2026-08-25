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

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ProviderScopes_ReflectEveryCapabilityCombination(bool mail, bool calendar, bool contacts)
    {
        var request = new ProviderAuthorizationRequest(mail, calendar, [], contacts);
        var gmail = _configuration.GetGmailScopes(request);
        var outlook = _configuration.GetOutlookScopes(request);

        gmail.Contains("https://mail.google.com/").Should().Be(mail);
        gmail.Contains("https://www.googleapis.com/auth/calendar").Should().Be(calendar);
        gmail.Contains("https://www.googleapis.com/auth/contacts").Should().Be(contacts);
        outlook.Contains("mail.readwrite").Should().Be(mail);
        outlook.Contains("Calendars.ReadWrite").Should().Be(calendar);
        outlook.Contains("Contacts.ReadWrite").Should().Be(contacts);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public void ProviderScopes_ReflectIndependentTasksCapability(bool mail, bool calendar, bool tasks, bool expectedTasks)
    {
        var request = new ProviderAuthorizationRequest(mail, calendar, [], IncludeTasks: tasks);
        var gmail = _configuration.GetGmailScopes(request);
        var outlook = _configuration.GetOutlookScopes(request);

        gmail.Contains("https://www.googleapis.com/auth/tasks").Should().Be(expectedTasks);
        outlook.Contains("Tasks.ReadWrite").Should().Be(expectedTasks);
    }
}

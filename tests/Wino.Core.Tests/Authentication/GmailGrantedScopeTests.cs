using Wino.Authentication;
using Wino.Core.Domain.Models.Authentication;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GmailGrantedScopeTests
{
    private const string Mail = "https://mail.google.com/";
    private const string Labels = "https://www.googleapis.com/auth/gmail.labels";
    private const string Calendar = "https://www.googleapis.com/auth/calendar";
    private const string CalendarEvents = "https://www.googleapis.com/auth/calendar.events";
    private const string CalendarSettings = "https://www.googleapis.com/auth/calendar.settings.readonly";
    private const string DriveFile = "https://www.googleapis.com/auth/drive.file";
    private const string Contacts = "https://www.googleapis.com/auth/contacts";
    private const string Tasks = "https://www.googleapis.com/auth/tasks";
    private const string Email = "https://www.googleapis.com/auth/userinfo.email";
    private const string Profile = "https://www.googleapis.com/auth/userinfo.profile";

    private static readonly string[] AllScopes = new MailAuthenticatorConfiguration()
        .GetGmailScopes(new ProviderAuthorizationRequest(true, true, [], true, true));

    [Fact]
    public void GetMissingScopes_FullGrant_IsEmpty()
        => Assert.Empty(GmailAuthenticator.GetMissingScopes(AllScopes, string.Join(' ', AllScopes.Append("openid"))));

    [Fact]
    public void GetMissingScopes_UntickedCalendarAndTasks_ReportsThem()
    {
        var granted = string.Join(' ', Email, Profile, "openid", Mail, Labels, Contacts);

        var missing = GmailAuthenticator.GetMissingScopes(AllScopes, granted);

        Assert.Equal(new[] { Calendar, CalendarEvents, CalendarSettings, DriveFile, Tasks }, missing);
    }

    [Fact]
    public void GetMissingScopes_UntickedMail_ReportsMailScopes()
    {
        var granted = string.Join(' ', Email, Profile, Calendar, CalendarEvents, CalendarSettings, DriveFile, Contacts, Tasks);

        Assert.Equal(new[] { Mail, Labels }, GmailAuthenticator.GetMissingScopes(AllScopes, granted));
    }

    [Fact]
    public void GetMissingScopes_BroaderGrantCoversNarrowerScopes()
    {
        var granted = string.Join(' ', Mail, Calendar, DriveFile, Contacts, Tasks);

        Assert.Empty(GmailAuthenticator.GetMissingScopes(AllScopes, granted));
    }

    [Fact]
    public void GetMissingScopes_IdentityScopesReportedAsAliases_AreNotRequired()
        => Assert.Empty(GmailAuthenticator.GetMissingScopes([Email, Profile, Mail], $"email profile openid {Mail}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetMissingScopes_UnreportedGrant_CannotBeVerifiedAndIsAccepted(string? granted)
        => Assert.Empty(GmailAuthenticator.GetMissingScopes(AllScopes, granted));
}

using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Services;

public class MailAuthenticatorConfiguration : IAuthenticatorConfig
{
    public string OutlookAuthenticatorClientId => "b19c2035-d740-49ff-b297-de6ec561b208";
    public string GmailAuthenticatorClientId => "973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn.apps.googleusercontent.com";
    public string GmailTokenStoreIdentifier => "WinoMailGmailTokenStore";

    public string[] GetOutlookScopes(ProviderAuthorizationRequest request)
    {
        var scopes = new List<string> { "email", "offline_access", "User.Read" };

        if (request.IncludeMail)
        {
            scopes.AddRange([
                "mail.readwrite",
                "mail.send",
                "Mail.Send.Shared",
                "Mail.ReadWrite.Shared"
            ]);
        }

        if (request.IncludeCalendar)
        {
            scopes.AddRange([
                "Calendars.ReadBasic",
                "Calendars.ReadWrite",
                "Calendars.ReadWrite.Shared",
                "Calendars.Read",
                "Calendars.Read.Shared"
            ]);
        }

        if (request.IncludeContacts)
            scopes.Add("Contacts.ReadWrite");

        if (request.Features?.Contains(ProviderFeature.MailFilters) == true)
            scopes.Add("MailboxSettings.ReadWrite");

        return scopes.ToArray();
    }

    public string[] GetGmailScopes(ProviderAuthorizationRequest request)
    {
        var scopes = new List<string>
        {
            "https://www.googleapis.com/auth/userinfo.profile",
            "https://www.googleapis.com/auth/userinfo.email"
        };

        if (request.IncludeMail)
        {
            scopes.AddRange([
                "https://mail.google.com/",
                "https://www.googleapis.com/auth/gmail.labels"
            ]);
        }

        if (request.IncludeCalendar)
        {
            scopes.AddRange([
                "https://www.googleapis.com/auth/calendar",
                "https://www.googleapis.com/auth/calendar.events",
                "https://www.googleapis.com/auth/calendar.settings.readonly",
                "https://www.googleapis.com/auth/drive.file"
            ]);
        }

        if (request.IncludeContacts)
            scopes.Add("https://www.googleapis.com/auth/contacts");

        if (request.Features?.Contains(ProviderFeature.MailFilters) == true)
            scopes.Add("https://www.googleapis.com/auth/gmail.settings.basic");

        return scopes.ToArray();
    }
}

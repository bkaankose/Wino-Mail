using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class MailAuthenticatorConfiguration : IAuthenticatorConfig
{
    public string OutlookAuthenticatorClientId => "b19c2035-d740-49ff-b297-de6ec561b208";

    public string GmailAuthenticatorClientId => "973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn.apps.googleusercontent.com";

    public string GmailTokenStoreIdentifier => "WinoMailGmailTokenStore";

    public string[] GetOutlookScope(bool isMailAccessGranted, bool isCalendarAccessGranted)
    {
        var scopes = new List<string>
        {
            "email",
            "offline_access",
            "User.Read"
        };

        if (isMailAccessGranted)
        {
            scopes.AddRange(new[]
            {
                "mail.readwrite",
                "mail.send",
                "Mail.Send.Shared",
                "Mail.ReadWrite.Shared",
                "MailboxSettings.ReadWrite"
            });
        }

        if (isCalendarAccessGranted)
        {
            scopes.AddRange(new[]
            {
                "Calendars.ReadBasic",
                "Calendars.ReadWrite",
                "Calendars.ReadWrite.Shared",
                "Calendars.Read",
                "Calendars.Read.Shared"
            });
        }

        return scopes.ToArray();
    }

    public string[] GetGmailScope(bool isMailAccessGranted, bool isCalendarAccessGranted)
    {
        var scopes = new List<string>
        {
            "https://www.googleapis.com/auth/userinfo.profile",
            "https://www.googleapis.com/auth/userinfo.email"
        };

        if (isMailAccessGranted)
        {
            scopes.AddRange(new[]
            {
                "https://mail.google.com/",
                "https://www.googleapis.com/auth/gmail.labels",
                "https://www.googleapis.com/auth/gmail.settings.basic"
            });
        }

        if (isCalendarAccessGranted)
        {
            scopes.AddRange(new[]
            {
                "https://www.googleapis.com/auth/calendar",
                "https://www.googleapis.com/auth/calendar.events",
                "https://www.googleapis.com/auth/calendar.settings.readonly",
                "https://www.googleapis.com/auth/drive.file"
            });
        }

        return scopes.ToArray();
    }
}

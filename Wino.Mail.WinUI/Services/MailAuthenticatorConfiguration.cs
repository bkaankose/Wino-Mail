using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class MailAuthenticatorConfiguration : IAuthenticatorConfig
{
    public string OutlookAuthenticatorClientId => "b19c2035-d740-49ff-b297-de6ec561b208";

    public string[] OutlookScope => new string[]
    {
        "email",
        "mail.readwrite",
        "offline_access",
        "mail.send",
        "Mail.Send.Shared",
        "Mail.ReadWrite.Shared",
        "User.Read"
    };

    public string GmailAuthenticatorClientId => "973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn.apps.googleusercontent.com";

    public string[] GmailScope => new string[]
    {
        "https://mail.google.com/",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/gmail.labels",
        "https://www.googleapis.com/auth/userinfo.email"
    };

    public string GmailTokenStoreIdentifier => "WinoMailGmailTokenStore";
}

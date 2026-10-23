using MailCal.Core.Domain.Models.Authentication;

namespace MailCal.Core.Domain.Interfaces;

public interface IAuthenticatorConfig
{
    string ApplicationDisplayName => "Mail-Cal";
    string OutlookAuthenticatorClientId { get; }
    string[] GetOutlookScopes(ProviderAuthorizationRequest request);
    string GmailAuthenticatorClientId { get; }
    string[] GetGmailScopes(ProviderAuthorizationRequest request);
    string GmailTokenStoreIdentifier { get; }
    string GmailTokenStorePath { get; }
}

using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticatorConfig
{
    string OutlookAuthenticatorClientId { get; }
    string[] GetOutlookScopes(ProviderAuthorizationRequest request);
    string GmailAuthenticatorClientId { get; }
    string[] GetGmailScopes(ProviderAuthorizationRequest request);
    string GmailTokenStoreIdentifier { get; }
    string GmailTokenStorePath { get; }
}

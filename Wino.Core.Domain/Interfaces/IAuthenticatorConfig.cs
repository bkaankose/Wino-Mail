namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticatorConfig
{
    string OutlookAuthenticatorClientId { get; }
    string[] OutlookScope { get; }
    string GmailAuthenticatorClientId { get; }
    string[] GmailScope { get; }
    string GmailTokenStoreIdentifier { get; }
}

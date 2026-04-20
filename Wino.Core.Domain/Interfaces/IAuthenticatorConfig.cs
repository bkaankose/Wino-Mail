namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticatorConfig
{
    string OutlookAuthenticatorClientId { get; }
    string[] GetOutlookScope(bool isMailAccessGranted, bool isCalendarAccessGranted);
    string GmailAuthenticatorClientId { get; }
    string[] GetGmailScope(bool isMailAccessGranted, bool isCalendarAccessGranted);
    string GmailTokenStoreIdentifier { get; }
}

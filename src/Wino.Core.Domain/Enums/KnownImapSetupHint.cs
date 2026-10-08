namespace Wino.Core.Domain.Enums;

/// <summary>
/// The one instruction a known IMAP provider needs before its credentials work.
/// Each value maps to a translated sentence shown on the credentials page.
/// </summary>
public enum KnownImapSetupHint
{
    None,

    /// <summary>The provider rejects the account password; an app password must be generated.</summary>
    AppPasswordRequired,

    /// <summary>IMAP is off for new accounts and must be enabled on the provider's website first.</summary>
    ImapDisabledByDefault,

    /// <summary>Mail is only reachable through a bridge application running on this PC.</summary>
    LocalBridgeRequired,

    /// <summary>The provider issues an authorization code that replaces the password in mail apps.</summary>
    AuthorizationCodeRequired,

    /// <summary>Third-party client access is a setting the user must switch on first.</summary>
    ThirdPartyAccessRequired,

    /// <summary>A separate email password exists next to the account password.</summary>
    SeparateEmailPasswordRequired,

    /// <summary>With two-factor authentication on, the current code is appended to the password.</summary>
    TwoFactorCodeSuffix
}

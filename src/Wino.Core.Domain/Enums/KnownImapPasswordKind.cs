namespace Wino.Core.Domain.Enums;

/// <summary>
/// What a known IMAP provider expects in the password field during setup.
/// </summary>
public enum KnownImapPasswordKind
{
    /// <summary>The regular account password.</summary>
    AccountPassword,

    /// <summary>A password generated for third-party apps in the provider's security settings.</summary>
    AppPassword,

    /// <summary>The password shown by a local bridge application, such as Proton Mail Bridge.</summary>
    BridgePassword
}

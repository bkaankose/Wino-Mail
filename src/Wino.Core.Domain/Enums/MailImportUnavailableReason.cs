namespace Wino.Core.Domain.Enums;

public enum MailImportUnavailableReason
{
    /// <summary>The user has not granted, or has withdrawn, the permission the import needs.</summary>
    AuthorizationRequired,

    /// <summary>The server refused the sign-in for the import protocol, for example IMAP turned off by an administrator.</summary>
    SignInRejected
}

namespace Wino.Core.Domain.Enums;

public enum AccountAttentionReason
{
    None,
    InvalidCredentials,
    MissingSystemFolderConfiguration,
    CertificateValidationFailed,

    /// <summary>The server's administrator wiped the account from this device; only removing the account resolves it.</summary>
    RemovedByAdministrator
}

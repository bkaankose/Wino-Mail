using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Domain.Extensions;

public static class MailAccountReauthenticationExtensions
{
    /// <summary>
    /// Whether one interactive provider sign-in (Fix account) repairs the account: a Gmail or
    /// Outlook account whose credentials expired, or whose enabled contacts or To Do mode is
    /// waiting for provider consent. Certificate and folder problems need other fixes.
    /// </summary>
    public static bool CanBeFixedBySigningIn(this MailAccount account)
    {
        if (account?.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            return false;

        if (account.AttentionReason == AccountAttentionReason.InvalidCredentials)
            return true;

        // Only enabled, provider-backed modes have a consent to renew; local modes never sign in.
        return account.AttentionReason == AccountAttentionReason.None &&
               (IsContactConsentPending(account) || IsTaskConsentPending(account));
    }

    /// <summary>
    /// Whether an account row in <paramref name="mode"/>'s navigation pane offers Fix account
    /// instead of synchronizing. Expired or rejected credentials block every mode; a pending
    /// provider consent only blocks the mode that waits for it. Mail also covers its
    /// folder-configuration issue.
    /// </summary>
    public static bool RequiresAttention(this MailAccount account, WinoApplicationMode mode)
    {
        if (account == null)
            return false;

        if (account.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed)
            return true;

        return mode switch
        {
            WinoApplicationMode.Mail => account.AttentionReason != AccountAttentionReason.None,
            // Only a consent that Fix account can renew; the sign-in covers every enabled mode.
            WinoApplicationMode.Contacts => account.CanBeFixedBySigningIn() && IsContactConsentPending(account),
            WinoApplicationMode.Tasks => account.CanBeFixedBySigningIn() && IsTaskConsentPending(account),
            _ => false
        };
    }

    /// <summary>
    /// Whether the account must not reach its servers. Any attention reason blocks all
    /// synchronization and background connections (IDLE, keepalive) until the user fixes it;
    /// only the fix itself (sign-in or connectivity test) talks to the server meanwhile.
    /// </summary>
    public static bool IsNetworkAccessBlocked(this MailAccount account)
        => account != null && (IsOfflineDemoMode || account.AttentionReason != AccountAttentionReason.None);

    /// <summary>
    /// Set when the data folder holds an offline demo marker. Every account then stays off the
    /// network without showing attention UI, so recordings can use fabricated local data.
    /// </summary>
    public static bool IsOfflineDemoMode { get; set; }

    public static void ThrowIfNetworkAccessBlocked(this MailAccount account)
    {
        if (account.IsNetworkAccessBlocked())
            throw new AccountAttentionRequiredException(account);
    }

    private static bool IsContactConsentPending(MailAccount account)
        => account.IsContactAccessEnabled &&
           account.ContactIntegrationSource == AccountIntegrationSource.Provider &&
           account.IsContactReauthorizationRequired;

    private static bool IsTaskConsentPending(MailAccount account)
        => account.IsTaskAccessEnabled &&
           account.TaskIntegrationSource == AccountIntegrationSource.Provider &&
           account.IsTaskReauthorizationRequired;
}

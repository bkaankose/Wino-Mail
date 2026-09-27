using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

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
               (account.IsContactAccessEnabled &&
                account.ContactIntegrationSource == AccountIntegrationSource.Provider &&
                account.IsContactReauthorizationRequired ||
                account.IsTaskAccessEnabled &&
                account.TaskIntegrationSource == AccountIntegrationSource.Provider &&
                account.IsTaskReauthorizationRequired);
    }
}
